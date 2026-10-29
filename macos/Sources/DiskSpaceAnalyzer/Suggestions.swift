import AppKit

/// A known place that usually holds space that can be freed, with what deleting it means.
final class Suggestion {
    var name = "", note = "", location = ""
    var safe = true                       // safe = rebuilt or downloaded again automatically
    var targets: [DeleteTarget] = []
    var size: Int64 = 0
    init(_ name: String, safe: Bool, note: String) { self.name = name; self.safe = safe; self.note = note }
}

extension MainWindowController {
    func buildSuggestionsPage() -> Page {
        btnLookAgain.tip = "Measure the suggestions again"
        let p = Page("Suggestions", [Column("name", "Name", 190), Column("size", "Size", 80, true), Column("size", "% of suggestions", 135, true),
                                     Column("folder", "Location", 280), Column("note", "What deleting means", 420)],
                     barColumn: 2, bar: ActionBar([btnLookAgain]))
        p.chartTitle = "Cleanup suggestions"
        p.chartScope = "of the suggestions"
        p.empty = "Nothing to suggest."
        p.fill = { [unowned self] in self.fillSuggestions() }
        return p
    }

    func fillSuggestions() {
        let p = pSuggest!
        if suggestions != nil { showSuggestions(); return }
        suggestGen += 1
        let gen = suggestGen
        p.setCaption("Looking for space that can be freed...")
        p.setRows([], "Looking for space that can be freed...")
        let scanned = roots.compactMap { $0.node }
        readInBackground({ [unowned self] in self.findSuggestions(scanned) }, { [weak self] found in
            guard let self = self, gen == self.suggestGen else { return }
            self.suggestions = found
            self.showSuggestions()
        })
    }

    func showSuggestions() {
        let p = pSuggest!
        let list = suggestions ?? []
        let total = list.reduce(Int64(0)) { $0 + $1.size }
        var rows: [ItemInfo] = []
        for s in list where s.size > 0 {
            let it = ItemInfo()
            it.targets = s.targets; it.name = s.name; it.size = s.size; it.folderIcon = true; it.folder = s.location
            it.path = s.targets.count == 1 && !s.targets[0].emptyTrash ? s.targets[0].path : nil
            it.note = s.note
            it.percent = total > 0 ? 100 * Double(s.size) / Double(total) : 0
            it.cells = [it.name, Fmt.size(it.size), Fmt.pct(it.percent), it.folder, it.note]
            if !s.safe { it.color = T.WARN_FG }
            rows.append(it)
        }
        p.setRows(rows, "Nothing to suggest: no caches, logs or leftovers worth deleting were found.")
        p.setCaption(rows.isEmpty ? "Nothing to suggest." :
            "\(Fmt.size(total)) can probably be freed. White: safe, rebuilt or downloaded again when needed.  Amber: review first.  Select rows, then Add to delete list.")
        p.chartSub = Fmt.size(total) + " can be freed"
        p.chartTotal = 0
        if activePage === p { refreshChart() }
    }

    // MARK: catalogue (runs on a background thread while the trees are held still)

    func findSuggestions(_ scanned: [DirNode]) -> [Suggestion] {
        let home = PathUtil.home
        let lib = home + "/Library"
        let caches = lib + "/Caches"
        var list: [Suggestion] = []

        // The Trash: emptied (its contents deleted for good), even when "Move to Trash" is chosen.
        let trashDir = home + "/.Trash"
        let trashItems = DeleteJob.children(trashDir).count
        let trashSize = trashItems > 0 ? measureFolder(trashDir) : 0
        if trashSize > 0 {
            let s = Suggestion("Trash", safe: true, note: "Deleted items waiting in the Trash. Emptying it frees the space for good.")
            let t = DeleteTarget(path: trashDir, label: "Trash", isDir: true, size: trashSize, note: "empty the Trash")
            t.contentsOnly = true; t.emptyTrash = true
            s.targets = [t]; s.size = trashSize
            s.location = "\(trashDir) (\(Fmt.count(trashItems)) items)"
            list.append(s)
        }

        // Caches that get their own row are not counted again in "User caches".
        let ownRows: Set<String> = ["com.apple.Safari", "Google", "Firefox", "Microsoft Edge", "BraveSoftware", "Yarn", "pip", "Homebrew",
                                    "go-build", "CocoaPods"]
        let userCaches = Suggestion("User caches", safe: true, note: "Files apps keep to start and work faster. Rebuilt when needed; quit apps first. Files in use are skipped.")
        addContents(userCaches, caches, exclude: ownRows)
        finish(&list, userCaches)
        cacheRow(&list, "Logs", lib + "/Logs", true, "Diagnostic logs of apps. Only needed to investigate a problem.")

        let dev = lib + "/Developer"
        cacheRow(&list, "Xcode DerivedData", dev + "/Xcode/DerivedData", true, "Xcode's build products and indexes. Rebuilt on the next build.")
        cacheRow(&list, "Xcode Archives", dev + "/Xcode/Archives", false, "Archived app builds. Keep the ones you may need to symbolicate crashes or resubmit.")
        cacheRow(&list, "iOS DeviceSupport", dev + "/Xcode/iOS DeviceSupport", true, "Debug symbols for connected devices. Copied again when a device connects.")
        cacheRow(&list, "CoreSimulator caches", dev + "/CoreSimulator/Caches", true, "Simulator caches. Rebuilt when a simulator runs.")

        let browser = "Web pages stored to load faster. Quit the browser first; sites load a little slower once."
        cacheRow(&list, "Safari cache", caches + "/com.apple.Safari", true, browser)
        cacheRow(&list, "Google Chrome cache", caches + "/Google/Chrome", true, browser)
        cacheRow(&list, "Microsoft Edge cache", caches + "/Microsoft Edge", true, browser)
        cacheRow(&list, "Brave cache", caches + "/BraveSoftware", true, browser)
        let firefox = Suggestion("Firefox cache", safe: true, note: browser)
        for d in DeleteJob.children(caches + "/Firefox/Profiles") { addContents(firefox, d + "/cache2") }
        finish(&list, firefox)

        let devNote = "Developer cache: downloaded packages. Downloaded again when a project needs them."
        cacheRow(&list, "npm cache", home + "/.npm/_cacache", true, devNote)
        cacheRow(&list, "Yarn cache", caches + "/Yarn", true, devNote)
        cacheRow(&list, "pnpm store", lib + "/pnpm/store", true, devNote)
        cacheRow(&list, "pip cache", caches + "/pip", true, devNote)
        cacheRow(&list, "Homebrew cache", caches + "/Homebrew", true, "Downloaded Homebrew bottles and sources. Downloaded again when needed.")
        cacheRow(&list, "Gradle caches", home + "/.gradle/caches", true, devNote)
        cacheRow(&list, "Maven repository", home + "/.m2/repository", true, devNote)
        cacheRow(&list, "Cargo registry cache", home + "/.cargo/registry/cache", true, devNote)
        cacheRow(&list, "Go build cache", caches + "/go-build", true, "Go's compiled packages. Rebuilt on the next build.")
        cacheRow(&list, "CocoaPods cache", caches + "/CocoaPods", true, devNote)

        // node_modules folders in the scanned trees (outermost only).
        let nm = Suggestion("node_modules folders", safe: true, note: "JavaScript packages of projects. Restored with npm install (or yarn / pnpm).")
        for r in scanned {
            for d in MainWindowController.outermostNamed(r, "node_modules") {
                let t = DeleteTarget(path: d.fullPath, label: (d.parent?.name ?? "") + "/node_modules", isDir: true, size: d.size, note: "node_modules")
                t.dir = d
                nm.targets.append(t)
                nm.size += d.size
            }
        }
        if !nm.targets.isEmpty { nm.location = "\(Fmt.count(nm.targets.count)) projects in the scanned folders"; list.append(nm) }

        // Old installers and disk images in Downloads: worth a look, not automatic.
        let dl = Suggestion("Old installers in Downloads", safe: false, note: "Disk images, installer packages and archives older than 30 days. Keep the ones you still need.")
        let downloads = home + "/Downloads"
        let cutoff = Date().addingTimeInterval(-30 * 86400)
        // Without Full Disk Access, listing Downloads makes macOS ask; only look when a scan has read it already.
        let mayList = FullDiskAccess.granted || (findScanned(downloads, exact: true).map { !$0.insideSkipped && $0.fileList != nil } ?? false)
        for p in mayList ? DeleteJob.children(downloads) : [] {
            let ext = (p as NSString).pathExtension.lowercased()
            guard ["dmg", "pkg", "zip"].contains(ext), let a = try? FileManager.default.attributesOfItem(atPath: p),
                  (a[.type] as? FileAttributeType) == .typeRegular,
                  let mod = a[.modificationDate] as? Date, mod < cutoff else { continue }
            let size = (a[.size] as? NSNumber)?.int64Value ?? 0
            dl.targets.append(DeleteTarget(path: p, label: PathUtil.name(p), size: size, note: "old download"))
            dl.size += size
        }
        dl.location = downloads
        if !dl.targets.isEmpty { list.append(dl) }
        return list
    }

    private func cacheRow(_ list: inout [Suggestion], _ name: String, _ dir: String, _ safe: Bool, _ note: String) {
        let s = Suggestion(name, safe: safe, note: note)
        addContents(s, dir)
        finish(&list, s)
    }

    private func addContents(_ s: Suggestion, _ dir: String, exclude: Set<String> = []) {
        guard PathUtil.isDir(dir) else { return }
        var size = measureFolder(dir)
        for n in exclude where PathUtil.isDir(PathUtil.join(dir, n)) { size -= measureFolder(PathUtil.join(dir, n)) }
        if size <= 0 { return }
        let t = DeleteTarget(path: PathUtil.trim(dir), label: s.name, isDir: true, size: size, note: "contents only")
        t.contentsOnly = true
        t.exclude = exclude
        s.targets.append(t)
        s.size += size
    }

    private func finish(_ list: inout [Suggestion], _ s: Suggestion) {
        if s.targets.isEmpty || s.size <= 0 { return }
        s.location = s.targets.count == 1 ? s.targets[0].path : "\(Fmt.count(s.targets.count)) locations"
        if s.targets.count > 1 { for t in s.targets where t.label == s.name { t.label = s.name + ": " + PathUtil.name(PathUtil.parent(t.path)) } }
        list.append(s)
    }

    /// Size of a folder: from the scanned tree when it is there, otherwise read now.
    func measureFolder(_ dir: String) -> Int64 {
        if let n = findScanned(dir, exact: true) { return n.size }
        return Scanner().run(dir).size
    }

    static func outermostNamed(_ root: DirNode, _ name: String) -> [DirNode] {
        var out: [DirNode] = []
        var stack = [root]
        while let n = stack.popLast() {
            if n !== root && n.name == name { out.append(n); continue }
            stack.append(contentsOf: n.children)
        }
        return out
    }
}
