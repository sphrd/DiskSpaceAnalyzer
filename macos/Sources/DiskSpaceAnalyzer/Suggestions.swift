import AppKit

/// A known place that usually holds space that can be freed, with what deleting it means. Its targets (each folder,
/// cache or file) can be checked one by one in the Suggestions view.
final class Suggestion {
    var name = "", note = "", location = ""
    var safe = true                       // safe = rebuilt or downloaded again automatically
    var targets: [DeleteTarget] = []
    var checked: [Bool] = []              // per target: picked in the Suggestions view
    var expanded = false
    var size: Int64 = 0
    init(_ name: String, safe: Bool, note: String) { self.name = name; self.safe = safe; self.note = note }

    func isChecked(_ i: Int) -> Bool { i >= 0 && i < checked.count && checked[i] }

    /// 0 = nothing checked, 1 = every target checked, 2 = some (mixed).
    var checkState: Int {
        let n = targets.indices.filter { isChecked($0) }.count
        return n == 0 ? 0 : n == targets.count ? 1 : 2
    }

    func checkAll(_ on: Bool) { checked = Array(repeating: on, count: targets.count) }

    func setChecked(_ i: Int, _ on: Bool) {
        if checked.count != targets.count { checked = targets.indices.map { isChecked($0) } }
        if i >= 0 && i < checked.count { checked[i] = on }
    }

    func index(of t: DeleteTarget) -> Int? { targets.firstIndex { $0 === t } }

    var checkedTargets: [DeleteTarget] { targets.indices.filter { isChecked($0) }.map { targets[$0] } }

    /// Safe suggestions start checked, the ones to review first unchecked.
    static func resetChecks(_ list: [Suggestion]) { for s in list { s.checkAll(s.safe) } }
    static func checkAll(_ list: [Suggestion], _ on: Bool) { for s in list { s.checkAll(on) } }
    static func checked(_ list: [Suggestion]) -> [DeleteTarget] { list.flatMap { $0.checkedTargets } }

    /// After finding the suggestions again: each target keeps its check by path (new ones get the default) and each
    /// suggestion its expanded state by name. Without an earlier list, the defaults.
    static func carryOver(_ found: [Suggestion], from old: [Suggestion]?) {
        guard let old = old else { resetChecks(found); return }
        var checks: [String: Bool] = [:]
        var open = Set<String>()
        for s in old {
            if s.expanded { open.insert(s.name) }
            for (i, t) in s.targets.enumerated() { checks[PathUtil.trim(t.path)] = s.isChecked(i) }
        }
        for s in found {
            s.expanded = open.contains(s.name)
            s.checked = s.targets.map { checks[PathUtil.trim($0.path)] ?? s.safe }
        }
    }
}

extension MainWindowController {
    func buildSuggestionsPage() -> Page {
        btnLookAgain.tip = "Look for space that can be freed again"
        btnSugAll.tip = "Check every suggested item"
        btnSugNone.tip = "Uncheck every suggested item"
        btnSugDelete.tip = "Delete the checked items now, bypassing the Trash (caches: their contents)"
        for b in [btnLookAgain, btnSugAll, btnSugNone, btnSugDelete] { b.compact = true }
        let bar = ActionBar([btnLookAgain, btnSugAll, btnSugNone, btnSugDelete], right: [btnSugDelete])
        bar.minRowHeight = 0
        let p = Page("Suggestions", [Column("name", "Name", 230), Column("size", "Size", 80, true), Column("size", "% of suggestions", 135, true),
                                     Column("folder", "Location", 280), Column("note", "What deleting means", 420)],
                     barColumn: 2, bar: bar)
        p.headline.color = T.SUGGEST
        p.chartTitle = "Cleanup suggestions"
        p.chartScope = "of the suggestions"
        p.empty = "Nothing to suggest."
        p.fill = { [unowned self] in self.fillSuggestions() }
        p.table.onClickAt = { [unowned self] r, pt in self.suggestClick(r, pt) }
        return p
    }

    func fillSuggestions(clear: Bool = false, quiet: Bool = false) {
        let p = pSuggest!
        if suggestions != nil && !suggestDirty { showSuggestions(); return }
        if suggestFinding && !suggestDirty && !clear { return }      // already looking (e.g. the start-up refills)
        suggestDirty = false
        suggestFinding = true
        suggestGen += 1
        let gen = suggestGen
        if suggestions == nil || clear {
            p.setHeadline("")
            p.setCaption("Looking for space that can be freed...")
            p.setRows([], "Looking for space that can be freed...")
            if activePage === p { rowSlice = [:]; chart.showLogo(p.chartTitle, "", "Looking for space that can be freed...") }
        } else if !quiet {
            p.setCaption("Looking again for space that can be freed (the scanned data changed)...")
        }
        updateButtons()
        let scanned = roots.compactMap { $0.node }
        readInBackground({ [unowned self] in self.findSuggestions(scanned) }, { [weak self] found in
            guard let self = self, gen == self.suggestGen else { return }
            self.suggestFinding = false
            Suggestion.carryOver(found, from: self.suggestions)
            self.suggestions = found
            self.showSuggestions()
        })
    }

    /// The scanned data changed (scan, rescan, live update, delete, measure): the suggestions are found again when the
    /// view is shown next (viewsChanged / refreshAfterLive refill it at once when it is open).
    func markSuggestionsDirty() {
        suggestDirty = true
        pSuggest?.stale = true
    }

    /// The suggestions shown in the view.
    var shownSuggestions: [Suggestion] { (suggestions ?? []).filter { $0.size > 0 && !$0.targets.isEmpty } }

    /// A row of the Suggestions view: a suggestion (head) with a chevron and a checkbox, and when it is expanded one row
    /// per target under it (sharing its chart slice), each with its own checkbox.
    func showSuggestions() {
        let p = pSuggest!
        let list = shownSuggestions
        let total = list.reduce(Int64(0)) { $0 + $1.size }
        let keep = Set(p.selectedItems.compactMap { suggestKey($0) })
        var rows: [ItemInfo] = []
        for s in list {
            let it = ItemInfo()
            it.suggestion = s
            it.targets = s.targets; it.name = s.name; it.size = s.size; it.folderIcon = true; it.folder = s.location
            it.path = s.targets.count == 1 && !s.targets[0].emptyTrash ? s.targets[0].path : nil
            it.note = s.note
            it.percent = total > 0 ? 100 * Double(s.size) / Double(total) : 0
            it.cells = [it.name, Fmt.size(it.size), Fmt.pct(it.percent), it.folder, it.note]
            if !s.safe { it.color = T.WARN_FG }
            it.chevron = s.expanded ? 1 : 0
            it.check = s.checkState
            rows.append(it)
            if !s.expanded { continue }
            for (i, t) in s.targets.enumerated() {
                let c = ItemInfo()
                c.suggestion = s
                c.groupOf = it
                c.targets = [t]; c.name = t.label; c.size = t.size; c.folderIcon = t.isDir
                c.path = t.emptyTrash ? nil : t.path
                c.folder = t.path
                c.note = t.note.hasPrefix("partly deleted") ? t.note : ""
                c.percent = total > 0 ? 100 * Double(t.size) / Double(total) : 0
                c.cells = [c.name, Fmt.size(c.size), Fmt.pct(c.percent), c.folder, c.note]
                c.color = it.color
                c.check = s.isChecked(i) ? 1 : 0
                rows.append(c)
            }
        }
        p.setRows(rows, "Nothing to suggest: no caches, logs or leftovers worth deleting were found.")
        if !keep.isEmpty {
            var sel = IndexSet()
            for (i, it) in p.items.enumerated() {
                if let k = suggestKey(it), keep.contains(k) { sel.insert(i) }
                else if it.groupOf == nil, let s = it.suggestion, !s.expanded, s.targets.contains(where: { keep.contains(suggestKey(target: $0)) }) { sel.insert(i) }
            }
            p.select(sel)
        }
        updateSuggestCaption()
        p.chartSub = Fmt.size(total) + " can be freed"
        p.chartTotal = 0
        updateButtons()
        if activePage === p { refreshChart() }
    }

    /// Identity of a row across refills (also when the suggestions were found again): its target's path (member rows)
    /// or its suggestion's name (heads).
    private func suggestKey(_ it: ItemInfo) -> String? {
        guard let s = it.suggestion else { return nil }
        if it.groupOf != nil, let t = it.targets?.first { return suggestKey(target: t) }
        return "s\n" + s.name
    }

    private func suggestKey(target t: DeleteTarget) -> String { "t\n" + PathUtil.trim(t.path) }

    func updateSuggestCaption() {
        let list = shownSuggestions
        let total = list.reduce(Int64(0)) { $0 + $1.size }
        let checked = Suggestion.checked(list)
        let size = checked.reduce(Int64(0)) { $0 + $1.size }
        pSuggest.setHeadline(list.isEmpty ? "" : "\(Fmt.size(total)) can be freed")
        pSuggest.setCaption(list.isEmpty ? "Nothing to suggest." :
            "Checked: \(Fmt.count(checked.count)) item\(checked.count == 1 ? "" : "s"), \(Fmt.size(size)).  White: safe, rebuilt or downloaded again when needed.  Amber: review first.")
    }

    /// The check marks changed: rows, caption and buttons follow (the row selection stays).
    func suggestChecksChanged() {
        for it in pSuggest.items {
            guard let s = it.suggestion else { continue }
            if it.groupOf == nil { it.check = s.checkState }
            else if let t = it.targets?.first, let i = s.index(of: t) { it.check = s.isChecked(i) ? 1 : 0 }
        }
        pSuggest.table.redrawCells()
        updateSuggestCaption()
        updateButtons()
    }

    func checkAllSuggestions(_ on: Bool) {
        Suggestion.checkAll(shownSuggestions, on)
        suggestChecksChanged()
    }

    /// Toggles rows: a head checks or unchecks all its targets; when any of the rows is not fully checked, all get checked.
    func toggleSuggestChecks(_ rows: [ItemInfo]) {
        let rows = rows.filter { $0.suggestion != nil }
        if rows.isEmpty { return }
        let on = rows.contains { $0.check != 1 }
        for it in rows {
            guard let s = it.suggestion else { continue }
            if it.groupOf == nil { s.checkAll(on) }
            else if let t = it.targets?.first, let i = s.index(of: t) { s.setChecked(i, on) }
        }
        suggestChecksChanged()
    }

    /// Expands or collapses the suggestions of the rows (nil = toggle).
    func expandSuggestions(_ rows: [ItemInfo], _ open: Bool?) {
        var changed = false
        for it in rows {
            guard let s = it.suggestion else { continue }
            let to = open ?? !s.expanded
            if s.expanded != to { s.expanded = to; changed = true }
        }
        if changed { showSuggestions() }
    }

    /// Clicks on a chevron or a checkbox in the name column; the row selection does not change.
    func suggestClick(_ row: Int, _ pt: NSPoint) -> Bool {
        let p = pSuggest!
        guard row < p.items.count, p.items[row].suggestion != nil,
              let ci = p.table.tableColumns.firstIndex(where: { $0.identifier.rawValue == "c0" }) else { return false }
        let it = p.items[row]
        let x = pt.x - p.table.rect(ofColumn: ci).minX
        if it.chevron >= 0 && x >= ListCell.chevronX - 3 && x < ListCell.chevronX + 17 { expandSuggestions([it], nil); return true }
        let cx = ListCell.checkX(it)
        if it.check >= 0 && x >= cx - 4 && x < cx + 18 { toggleSuggestChecks([it]); return true }
        return false
    }

    /// Space toggles the checks of the selected rows, Left / Right collapse / expand them.
    func suggestKeyDown(_ e: NSEvent) -> Bool {
        if e.modifierFlags.contains(.command) || e.modifierFlags.contains(.option) || e.modifierFlags.contains(.control) { return false }
        let sel = pSuggest.selectedItems
        if sel.isEmpty { return false }
        switch e.keyCode {
        case 49: toggleSuggestChecks(sel); return true
        case 123: expandSuggestions(sel, false); return true
        case 124: expandSuggestions(sel, true); return true
        default: return false
        }
    }

    /// Deletes (or moves to the Trash) the checked targets, not the selected rows.
    func deleteCheckedSuggestions(trash: Bool, _ done: ((DeleteJob?) -> Void)? = nil) {
        if busy || scanning { done?(nil); return }
        let targets = Suggestion.checked(shownSuggestions)
        if targets.isEmpty { done?(nil); return }
        confirmAndDelete(targets, trash: trash) { [weak self] job in
            guard let self = self, job != nil else { done?(job); return }
            self.basketChanged()
            done?(job)
        }
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

        // node_modules folders of the user's own projects in the scanned trees (outermost only). Never one of an app,
        // an editor extension or a tool (see isProjectNodeModules).
        let nm = Suggestion("node_modules folders", safe: true, note: "JavaScript packages of projects. Restored with npm install (or yarn / pnpm).")
        for r in scanned {
            for d in MainWindowController.projectNodeModules(r) {
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

    /// The outermost node_modules folders under root that belong to a user's project (isProjectNodeModules).
    static func projectNodeModules(_ root: DirNode) -> [DirNode] {
        outermostNamed(root, "node_modules").filter { isProjectNodeModules($0.fullPath) }
    }

    static let systemRoots = ["/Applications", "/System", "/Library", "/usr", "/opt", "/private", "/bin", "/sbin", "/cores"]

    /// A node_modules folder may only be suggested when it belongs to a user's project: the folder holding it has a
    /// package.json, and nothing on its path is an app bundle (*.app), a system or library folder (/Applications,
    /// /System, /Library, ~/Library, /usr, /opt, /private, /bin, /sbin, /cores), a hidden folder (~/.vscode, ~/.npm,
    /// ~/.nvm...) or a folder named extensions or resources. Apps and editors ship their own node_modules (VS Code's
    /// built-in extensions): deleting those breaks them.
    static func isProjectNodeModules(_ path0: String) -> Bool {
        var path = PathUtil.trim(path0)
        guard PathUtil.name(path) == "node_modules" else { return false }
        let data = "/System/Volumes/Data"
        if path.hasPrefix(data + "/") { path = String(path.dropFirst(data.count)) }
        let home = PathUtil.trim(PathUtil.home)
        if (systemRoots + [home + "/Library"]).contains(where: { PathUtil.isWithin(path, $0) }) { return false }
        for c in path.split(separator: "/") {
            let l = c.lowercased()
            if c.hasPrefix(".") || l.hasSuffix(".app") || l == "extensions" || l == "resources" { return false }
        }
        return FileManager.default.fileExists(atPath: PathUtil.join(PathUtil.parent(path0), "package.json"))
    }
}
