import AppKit
import CoreServices

/// FSEvents stream on one scanned drive or folder; reports changed paths (in the app's spelling of the root) on the main
/// queue, and whether changes were lost (the kernel or the stream dropped events: a rescan is needed).
final class LiveStream {
    let root: ScanRoot
    private let path: String
    private let real: String
    private var stream: FSEventStreamRef?
    var onEvents: (([String], Bool) -> Void)?

    init?(_ root: ScanRoot) {
        self.root = root
        path = PathUtil.trim(root.path)
        var buf = [CChar](repeating: 0, count: Int(PATH_MAX) + 1)
        real = realpath(path, &buf) != nil ? PathUtil.trim(String(cString: buf)) : path
        var ctx = FSEventStreamContext(version: 0, info: Unmanaged.passUnretained(self).toOpaque(), retain: nil, release: nil, copyDescription: nil)
        let callback: FSEventStreamCallback = { _, info, count, paths, flags, _ in
            guard let info = info else { return }
            let me = Unmanaged<LiveStream>.fromOpaque(info).takeUnretainedValue()
            let arr = Unmanaged<CFArray>.fromOpaque(paths).takeUnretainedValue() as NSArray
            let lost = UInt32(kFSEventStreamEventFlagMustScanSubDirs) | UInt32(kFSEventStreamEventFlagUserDropped) | UInt32(kFSEventStreamEventFlagKernelDropped)
            var list: [String] = []
            var must = false
            for i in 0..<count {
                if flags[i] & lost != 0 { must = true }
                if i < arr.count, let p = arr[i] as? String { list.append(me.appPath(p)) }
            }
            me.onEvents?(list, must)
        }
        let flags = FSEventStreamCreateFlags(kFSEventStreamCreateFlagUseCFTypes) | FSEventStreamCreateFlags(kFSEventStreamCreateFlagFileEvents)
            | FSEventStreamCreateFlags(kFSEventStreamCreateFlagNoDefer)
        guard let s = FSEventStreamCreate(kCFAllocatorDefault, callback, &ctx, [path] as CFArray,
                                          FSEventStreamEventId(kFSEventStreamEventIdSinceNow), 0.5, flags) else { return nil }
        stream = s
        FSEventStreamSetDispatchQueue(s, DispatchQueue.main)
        FSEventStreamStart(s)
    }

    /// FSEvents reports resolved paths (e.g. /private/var for /var): map them back onto the scanned root.
    private func appPath(_ p0: String) -> String {
        let p = PathUtil.trim(p0)
        if real != path && PathUtil.isWithin(p, real) { return path + p.dropFirst(real.count) }
        return p
    }

    func stop() {
        guard let s = stream else { return }
        FSEventStreamStop(s)
        FSEventStreamInvalidate(s)
        FSEventStreamRelease(s)
        stream = nil
    }

    deinit { stop() }
}

/// Live updates: every scanned drive or folder is watched, and the scan is updated in place every two seconds (a changed
/// folder's own files are read again; new folders are scanned; removed ones dropped). Nothing is rescanned as a whole.
/// When FSEvents reports that changes were lost, the status line suggests a rescan.
extension MainWindowController {
    func startWatching(_ r: ScanRoot) {
        stopWatching(r)
        missed.removeAll { $0 === r }
        if liveTimer == nil {
            liveTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in self?.applyChanges() }
        }
        if let s = LiveStream(r) {
            s.onEvents = { [weak self, weak r] paths, lost in
                guard let self = self, let r = r else { return }
                self.enqueue(r, paths, lost)
            }
            streams.append(s)
        }
        updateLiveLabel()
    }

    func stopWatching(_ r: ScanRoot) {
        for s in streams where s.root === r { s.stop() }
        streams.removeAll { $0.root === r }
    }

    func stopAllWatching() { for s in streams { s.stop() }; streams.removeAll() }

    private func enqueue(_ r: ScanRoot, _ paths: [String], _ lost: Bool) {
        // Our own deletions are applied directly; their echo is ignored.
        if busy || Date() < ignoreUntil { return }
        if lost && !missed.contains(where: { $0 === r }) { missed.append(r); updateLiveLabel() }
        if fsPending.count > 200_000 { return }
        fsPending.append(contentsOf: paths)
    }

    /// Called when our own delete or rescan finishes: changes until now are already in the scan. Only a short echo is
    /// ignored, so changes made outside the app right after are not lost (an echo processed again is harmless).
    func ignoreOwnChanges() {
        ignoreUntil = Date().addingTimeInterval(0.5)
        fsPending.removeAll()
    }

    func updateLiveLabel() {
        for s in streams where !roots.contains(where: { $0 === s.root }) || s.root.node == nil { stopWatching(s.root) }
        missed.removeAll { m in !roots.contains { $0 === m } }
        if !missed.isEmpty {
            liveLabel.stringValue = "Changes missed - rescan"
            liveLabel.textColor = T.WARN_FG
            liveLabel.toolTip = "Too many changes happened at once on " + missed.map { $0.path }.joined(separator: ", ") + "; the scan may be out of date. Rescan (⌘R) to be sure."
        } else {
            liveLabel.stringValue = streams.isEmpty ? "" : "Live"
            liveLabel.textColor = T.MUTED
            liveLabel.toolTip = "The scanned drives and folders are watched; files that appear, change or go away update the results within seconds."
        }
        layoutStatus()
    }

    func applyChanges() {
        if busy || scanning || readers > 0 || fsPending.isEmpty {
            if livePending && !busy && !scanning && readers == 0 { refreshAfterLive() }
            return
        }
        let batch = fsPending.prefix(20000)
        fsPending.removeFirst(batch.count)
        var files: [DirNode] = []
        var fileIds = Set<ObjectIdentifier>()
        var added: [String] = []
        var structure = false
        var seen = Set<String>()
        var revive: [DirNode] = []
        func refresh(_ d: DirNode) { if fileIds.insert(ObjectIdentifier(d)).inserted { files.append(d) } }
        for p0 in batch {
            let p = PathUtil.trim(p0)
            if !seen.insert(p).inserted { continue }
            // Nothing inside a folder the scan did not enter is read: it would download it or make macOS ask for access.
            // A folder that was not downloaded and now is (opened in Finder) is scanned.
            if let holder = findScanned(p, exact: false), holder.insideSkipped {
                let hp = holder.fullPath
                if holder.skipped == .notDownloaded && PathUtil.isDir(hp) && !Dataless.isDataless(hp)
                    && !revive.contains(where: { $0 === holder }) { revive.append(holder) }
                continue
            }
            let node = findScanned(p, exact: true)
            let exists = PathUtil.exists(p)
            if let n = node, n.parent == nil { if exists { refresh(n) }; continue }   // the scanned root itself
            let parent = findScanned(PathUtil.parent(p), exact: true)
            if !exists {
                if let n = node { removeNode(n); structure = true }
                else if let par = parent { refresh(par) }
            } else if PathUtil.isDir(p) {
                if let n = node { refresh(n) }
                else if parent != nil { added.append(p); structure = true }
            } else if let par = parent {
                refresh(par)
            }
        }
        for d in files where isAttached(d) { Scanner.refreshFiles(d) }
        var done = Set<String>()
        for p in added where done.insert(p).inserted {
            guard let parent = findScanned(PathUtil.parent(p), exact: true), parent.child(PathUtil.name(p)) == nil, PathUtil.isDir(p) else { continue }
            let fresh = Scanner().run(p)   // usually small (a new folder); large copies arrive folder by folder anyway
            fresh.name = PathUtil.name(p)
            fresh.parent = parent
            fresh.modified = Int64(Date().timeIntervalSince1970)
            parent.children.append(fresh)
            parent.addUp(fresh.bytes, fresh.alloc, fresh.files, fresh.dirs + 1)
        }
        for d in revive where isAttached(d) && d.parent != nil {
            replaceNode(d, Scanner().run(d.fullPath))
            structure = true
        }
        if !files.isEmpty || !added.isEmpty || structure {
            livePending = true
            markSuggestionsDirty()
            if structure { for p in pages where p !== pContents { p.stale = true } }
        }
        if livePending && Date().timeIntervalSince(lastLiveRefresh) >= 4 { refreshAfterLive() }
    }

    /// Shows live changes without disturbing the user: values update in place, the selection and scroll position stay.
    func refreshAfterLive() {
        livePending = false
        lastLiveRefresh = Date()
        refreshTreeQuietly()
        updateDriveLabel()
        if activePage === pSuggest && suggestDirty && !suggestFinding { pSuggest.stale = false; fillSuggestions(quiet: true) }
        guard let c = current, isAttached(c) else { return }
        if activePage !== pContents { return }
        if !contents.selectedRowIndexes.isEmpty || NSEvent.pressedMouseButtons != 0 { return }
        let first = contents.rows(in: contents.visibleRect).location
        let topName = first >= 0 && first < pContents.items.count ? pContents.items[first].name : nil
        showContents(c)
        if let n = topName, let i = pContents.items.firstIndex(where: { $0.name == n }) {
            contents.scrollRowToVisible(min(pContents.items.count - 1, i + max(0, contents.rows(in: contents.visibleRect).length - 1)))
            contents.scrollRowToVisible(i)
        }
    }
}
