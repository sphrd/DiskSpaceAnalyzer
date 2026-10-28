import AppKit
import CryptoKit
import Darwin

/// Finds files with identical content: same size (from the index), then the same hash of the first 64 KB, then the same
/// hash of the whole file. Several hard links to one file (same device and inode) are one file, not duplicates. Files
/// not downloaded from iCloud are skipped (reading them would download them).
final class DuplicateFinder {
    private let lock = NSLock()
    private var _toRead: Int64 = 0, _read: Int64 = 0, _checked: Int64 = 0, _current = ""
    private var _cancelled = false
    var groups: [[FileEntry]] = []

    var bytesToRead: Int64 { lock.lock(); defer { lock.unlock() }; return _toRead }
    var bytesRead: Int64 { lock.lock(); defer { lock.unlock() }; return _read }
    var filesChecked: Int64 { lock.lock(); defer { lock.unlock() }; return _checked }
    var current: String { lock.lock(); defer { lock.unlock() }; return _current }
    var cancelled: Bool { lock.lock(); defer { lock.unlock() }; return _cancelled }
    func cancel() { lock.lock(); _cancelled = true; lock.unlock() }

    static let headSize = 64 * 1024

    final class Candidate {
        let file: FileEntry
        let path: String
        var id: UInt64 = 0          // device and inode: equal for hard links
        var head: String?, full: String?
        init(_ f: FileEntry, _ p: String) { file = f; path = p }
    }

    /// Step 1, reading the index (the trees must not change meanwhile): files of the same size.
    func collect(_ scope: DirNode, _ minSize: Int64) -> [[Candidate]] {
        var bySize: [Int64: [Candidate]] = [:]
        for d in scope.descendants() {
            guard let list = d.fileList else { continue }
            var dirPath: String?
            for f in list where f.bytes >= max(1, minSize) && !f.dataless {
                if dirPath == nil { dirPath = d.fullPath }
                bySize[f.bytes, default: []].append(Candidate(FileEntry(d, f), PathUtil.join(dirPath!, f.name)))
            }
            if cancelled { return [] }
        }
        return bySize.values.filter { $0.count > 1 }
    }

    /// Steps 2 and 3, reading the files only.
    func run(_ sizeGroups: [[Candidate]]) {
        lock.lock(); _toRead = sizeGroups.reduce(Int64(0)) { $0 + Int64($1.count) * min(Int64(DuplicateFinder.headSize), $1[0].file.bytes) }; lock.unlock()
        var headGroups: [[Candidate]] = []
        for g in sizeGroups {
            if cancelled { return }
            hashAll(g, limit: Int64(DuplicateFinder.headSize), identify: true, full: false)
            var seen = Set<UInt64>()
            var distinct: [Candidate] = []
            for c in g where c.head != nil { if seen.insert(c.id).inserted { distinct.append(c) } }
            for hg in Dictionary(grouping: distinct, by: { $0.head! }).values where hg.count > 1 { headGroups.append(hg) }
        }
        lock.lock(); _toRead = _read + headGroups.reduce(Int64(0)) { $0 + ($1[0].file.bytes > Int64(DuplicateFinder.headSize) ? Int64($1.count) * $1[0].file.bytes : 0) }; lock.unlock()
        var result: [[FileEntry]] = []
        for g in headGroups {
            if cancelled { return }
            if g[0].file.bytes > Int64(DuplicateFinder.headSize) { hashAll(g, limit: Int64.max, identify: false, full: true) }
            else { for c in g { c.full = c.head } }
            for fg in Dictionary(grouping: g.filter { $0.full != nil }, by: { $0.full! }).values where fg.count > 1 {
                result.append(fg.map { $0.file }.sorted { $0.modified > $1.modified })
            }
        }
        result.sort { Int64($0.count - 1) * $0[0].bytes > Int64($1.count - 1) * $1[0].bytes }
        groups = result
    }

    /// Hashes the files of a group, three at a time.
    private func hashAll(_ g: [Candidate], limit: Int64, identify: Bool, full: Bool) {
        let n = g.count
        let chunk = max(1, (n + 2) / 3)
        DispatchQueue.concurrentPerform(iterations: min(3, n)) { k in
            var i = k * chunk
            while i < min(n, (k + 1) * chunk) {
                if cancelled { return }
                let h = hash(g[i], limit, identify)
                if full { g[i].full = h } else { g[i].head = h }
                i += 1
            }
        }
    }

    private func hash(_ c: Candidate, _ limit: Int64, _ identify: Bool) -> String? {
        lock.lock(); _current = c.path; lock.unlock()
        let fd = open(c.path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        if fd < 0 { return nil }
        defer { close(fd) }
        if identify {
            var st = stat()
            if fstat(fd, &st) == 0 { c.id = (UInt64(UInt32(bitPattern: st.st_dev)) << 40) ^ UInt64(st.st_ino) }
            else { c.id = UInt64(UInt(bitPattern: c.path.hashValue)) }
        }
        _ = fcntl(fd, F_NOCACHE, 1)
        var md5 = Insecure.MD5()
        let bufSize = 1 << 20
        let buf = UnsafeMutableRawPointer.allocate(byteCount: bufSize, alignment: 16)
        defer { buf.deallocate() }
        var left = limit
        while left > 0 {
            if cancelled { return nil }
            let n = read(fd, buf, Int(min(Int64(bufSize), left)))
            if n < 0 { return nil }
            if n == 0 { break }
            md5.update(bufferPointer: UnsafeRawBufferPointer(start: buf, count: n))
            left -= Int64(n)
            lock.lock(); _read += Int64(n); lock.unlock()
        }
        lock.lock(); _checked += 1; lock.unlock()
        return Data(md5.finalize()).base64EncodedString()
    }
}

extension MainWindowController {
    static let dupeSizes: [Int64] = [1 << 20, 10 << 20, 100 << 20, 1]

    func buildDuplicatesPage() -> Page {
        btnFindDupes.tip = "Compare the files in the selected folder by content"
        btnSelectExtra.tip = "Select every copy except the newest one of each group; then add them to the delete list or delete them"
        let p = Page("Duplicates", [Column("name", "Name", 260), Column("size", "Size", 84, true), Column("files", "Can free", 90, true),
                                    Column("folder", "Folder", 330), Column("mod", "Modified", 128)],
                     barColumn: -1, bar: ActionBar([btnFindDupes, dupeMin, btnSelectExtra], right: [btnSelectExtra]))
        p.sortable = false
        p.chartTitle = "Duplicates"
        p.chartScope = "of the duplicates"
        p.empty = "Press Find duplicates to compare the files in the selected folder."
        p.fill = { [unowned self] in self.fillDuplicates() }
        return p
    }

    func findDuplicates() {
        if let f = dupeFinder { f.cancel(); return }
        guard let sc = scope() else { pDupes.setCaption("Scan a drive or folder first."); return }
        let finder = DuplicateFinder()
        dupeFinder = finder
        dupeScope = sc.fullPath
        let minSize = MainWindowController.dupeSizes[dupeMin.selectedIndex]
        btnFindDupes.title = "Stop"
        btnFindDupes.kind = .secondary
        dupeMin.isEnabled = false
        pDupes.bar?.relayout()
        pDupes.setCaption("Comparing files in \(Fmt.shorten(dupeScope, 50))...")
        dupeTimer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { [weak self] _ in
            guard let self = self, let f = self.dupeFinder else { return }
            let toRead = f.bytesToRead
            let pct = toRead > 0 ? 100 * Double(f.bytesRead) / Double(toRead) : 0
            self.pDupes.setCaption("Comparing files in \(Fmt.shorten(self.dupeScope, 50))...  \(Int(min(99, pct))) %  (\(Fmt.count(f.filesChecked)) files read)  \(Fmt.shorten(f.current, 60))")
        }
        readInBackground({ finder.collect(sc, minSize) }, { [weak self] groups in
            DispatchQueue.global(qos: .userInitiated).async {
                finder.run(groups)
                DispatchQueue.main.async { self?.duplicatesDone(finder) }
            }
        })
    }

    private func duplicatesDone(_ finder: DuplicateFinder) {
        dupeTimer?.invalidate(); dupeTimer = nil
        dupeFinder = nil
        btnFindDupes.title = "Find duplicates"
        btnFindDupes.kind = .primary
        dupeMin.isEnabled = true
        pDupes.bar?.relayout()
        if finder.cancelled { pDupes.setCaption("Stopped."); onDupesFinished?(); return }
        dupeGroups = finder.groups
        fillDuplicates()
        onDupesFinished?()
    }

    /// True while the file is still in the scanned index (not deleted since).
    func stillIndexed(_ f: FileEntry) -> Bool {
        guard isAttached(f.dir), let list = f.dir.fileList else { return false }
        return list.contains { $0.name == f.name }
    }

    func fillDuplicates() {
        let p = pDupes!
        p.chartTitle = "Duplicates"
        guard var groups = dupeGroups else {
            if dupeFinder == nil {
                p.setCaption("Find files with the same content in the selected folder (" + (scope().map { Fmt.shorten($0.fullPath, 70) } ?? "nothing scanned yet") + ").")
            }
            p.setRows([], "Press Find duplicates to compare the files in the selected folder by content.\nHard links and files not downloaded from iCloud are not counted.")
            p.chartSub = ""
            if activePage === p { refreshChart() }
            return
        }
        groups = groups.map { $0.filter { stillIndexed($0) } }.filter { $0.count > 1 }
        dupeGroups = groups
        var rows: [ItemInfo] = []
        var free: Int64 = 0
        for g in groups {
            let each = g[0].bytes, wasted = each * Int64(g.count - 1)
            free += wasted
            let head = ItemInfo()
            head.isGroup = true
            head.name = "\(g.count) copies of \(g[0].name)"
            head.size = each; head.chartValue = wasted
            head.folder = Set(g.map { $0.dir.fullPath }).count == 1 ? "same folder" : ""
            head.cells = [head.name, Fmt.size(each), Fmt.size(wasted), head.folder, ""]
            rows.append(head)
            for f in g {
                let it = ItemInfo.forFile(f, 0)
                it.folder = f.dir.fullPath
                it.groupOf = head
                it.cells = [it.name, Fmt.size(f.bytes), "", it.folder, Fmt.date(f.modified)]
                rows.append(it)
            }
        }
        p.setRows(rows, "No duplicates found in \(dupeScope).")
        p.setCaption(groups.isEmpty ? "No duplicates found in \(Fmt.shorten(dupeScope, 80))."
            : "\(Fmt.count(groups.count)) groups of identical files in \(Fmt.shorten(dupeScope, 60))  -  \(Fmt.size(free)) can be freed by keeping one copy of each. Newest copy first.")
        p.chartSub = groups.isEmpty ? "" : Fmt.size(free) + " in extra copies"
        p.chartTotal = 0
        p.empty = "No duplicates found."
        if activePage === p { refreshChart() }
    }

    func selectExtraCopies() {
        let p = pDupes!
        var rows = IndexSet()
        var head: ItemInfo?
        var first = false
        for (i, it) in p.items.enumerated() {
            if it.isGroup { head = it; first = true; continue }
            guard let h = head, it.groupOf === h else { continue }
            if first { first = false; continue }          // keep the newest copy (listed first)
            rows.insert(i)
        }
        p.select(rows)
        window?.makeFirstResponder(p.table)
        lastView = p.table
        let size = rows.reduce(Int64(0)) { $0 + p.items[$1].size }
        statusLabel.stringValue = "Selected \(Fmt.count(rows.count)) extra copies (\(Fmt.size(size))). Add them to the delete list (⇧⌘D) or delete them."
    }
}
