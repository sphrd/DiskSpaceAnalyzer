import AppKit

/// Something an app left behind in ~/Library or /Library (settings, caches, support files...).
final class AppLeftover {
    var path: String, kind: String
    var size: Int64 = 0                   // -1 = not measured (needs Full Disk Access)
    var note = ""
    var needsAdmin = false
    var isDir = false
    var checked = true
    init(_ path: String, _ kind: String) { self.path = path; self.kind = kind }
}

/// An app bundle in /Applications, /Applications/Utilities or ~/Applications.
final class InstalledApp {
    var name = "", bundleId = "", version = "", path = ""
    var size: Int64 = 0
    var installed: Date?
    var uninstalled = false
    var leftovers: [AppLeftover]?        // nil = not looked for yet
    var expanded = true

    var publisher: String {
        let parts = bundleId.split(separator: ".")
        guard parts.count >= 2, let first = parts[1].first else { return "—" }
        return String(first).uppercased() + parts[1].dropFirst()
    }

    var leftoverSize: Int64 { (leftovers ?? []).reduce(Int64(0)) { $0 + max(0, $1.size) } }
    var shownSize: Int64 { uninstalled ? leftoverSize : size }
    var displayName: String { uninstalled ? name + "  (uninstalled — leftovers)" : name }
    var hasLeftovers: Bool { !(leftovers ?? []).isEmpty }
    var checkedLeftovers: [AppLeftover] { (leftovers ?? []).filter { $0.checked } }

    /// 0 = nothing checked, 1 = every leftover checked, 2 = some (mixed).
    var checkState: Int {
        let all = leftovers ?? []
        let n = all.filter { $0.checked }.count
        return n == 0 ? 0 : n == all.count ? 1 : 2
    }
    func checkAll(_ on: Bool) { for l in leftovers ?? [] { l.checked = on } }

    static var defaultRoots: [String] { ["/Applications", "/Applications/Utilities", PathUtil.home + "/Applications"] }

    /// Reads a bundle's Info.plist; nil when it is not a folder.
    static func read(_ path: String) -> InstalledApp? {
        guard PathUtil.isDir(path) else { return nil }
        let info = (NSDictionary(contentsOfFile: path + "/Contents/Info.plist") as? [String: Any]) ?? [:]
        let a = InstalledApp()
        a.path = PathUtil.trim(path)
        let file = (PathUtil.name(path) as NSString).deletingPathExtension
        func str(_ k: String) -> String? { (info[k] as? String).map { $0.trimmingCharacters(in: .whitespaces) }.flatMap { $0.isEmpty ? nil : $0 } }
        a.name = str("CFBundleDisplayName") ?? str("CFBundleName") ?? file
        a.bundleId = str("CFBundleIdentifier") ?? ""
        a.version = str("CFBundleShortVersionString") ?? ""
        if let at = try? FileManager.default.attributesOfItem(atPath: path) {
            a.installed = (at[.creationDate] as? Date) ?? (at[.modificationDate] as? Date)
        }
        return a
    }

    /// Every .app directly in the roots and one subfolder level below them (e.g. /Applications/Adobe X/Adobe X.app),
    /// without Apple's own apps and anything under /System; largest first.
    static func find(_ roots: [String], _ measure: @escaping (String) -> Int64) -> [InstalledApp] {
        var seen = Set<String>()
        var out: [InstalledApp] = []
        func isApp(_ p: String) -> Bool { p.lowercased().hasSuffix(".app") && PathUtil.isDir(p) }
        func consider(_ p: String) {
            let std = PathUtil.trim((p as NSString).standardizingPath)
            let real = (std as NSString).resolvingSymlinksInPath
            if std == "/System" || std.hasPrefix("/System/") || real.hasPrefix("/System/") { return }
            if !seen.insert(std.lowercased()).inserted { return }
            guard let a = read(std), !a.bundleId.lowercased().hasPrefix("com.apple.") else { return }
            a.size = measure(std)
            out.append(a)
        }
        for r in roots {
            for c in DeleteJob.children(r) {
                if isApp(c) { consider(c) }
                else if PathUtil.isDir(c) { for cc in DeleteJob.children(c) where isApp(cc) { consider(cc) } }
            }
        }
        return out.sorted { $0.size != $1.size ? $0.size > $1.size : $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// A vendor uninstaller: an app with "Uninstall" in its name inside the bundle's Contents/Resources, or next to the
    /// app in its own folder (not loose in /Applications itself).
    static func vendorUninstaller(_ app: InstalledApp, roots: [String]) -> String? {
        func isUninstaller(_ p: String) -> Bool {
            let n = PathUtil.name(p).lowercased()
            return n.hasSuffix(".app") && n.contains("uninstall") && !PathUtil.same(p, app.path)
        }
        if let p = DeleteJob.children(app.path + "/Contents/Resources").first(where: isUninstaller) { return p }
        let parent = PathUtil.parent(app.path)
        if roots.contains(where: { PathUtil.same($0, parent) }) || InstalledApp.defaultRoots.contains(where: { PathUtil.same($0, parent) }) { return nil }
        return DeleteJob.children(parent).first(where: isUninstaller)
    }

    /// Items named after the bundle id B or the app name N in ~/Library and /Library (case-insensitive; names shorter
    /// than 4 characters and com.apple.* never match).
    static func findLeftovers(bundleId: String, name: String, home: String = PathUtil.home, fullDiskAccess: Bool = FullDiskAccess.granted) -> [AppLeftover] {
        let b = bundleId.lowercased(), n = name.lowercased()
        if b.hasPrefix("com.apple.") { return [] }
        let useB = b.count >= 4, useN = n.count >= 4 && !n.hasPrefix("com.apple.")
        if !useB && !useN { return [] }
        var out: [AppLeftover] = []
        var seen = Set<String>()
        let fm = FileManager.default
        func add(_ p: String, _ kind: String, _ admin: Bool, _ measure: Bool) {
            if !seen.insert(p.lowercased()).inserted { return }
            let l = AppLeftover(p, kind)
            l.isDir = PathUtil.isDir(p)
            l.needsAdmin = admin || access(PathUtil.parent(p), W_OK) != 0
            if l.needsAdmin { l.note = "needs administrator"; l.checked = false }
            if !measure {
                l.size = -1
                l.note = "needs Full Disk Access to measure"
            } else if Dataless.isDataless(p) {
                l.size = 0
                l.note = "in iCloud, not downloaded"
            } else {
                l.size = measureItem(p)
            }
            out.append(l)
        }
        func scan(_ dir: String, _ kind: String, admin: Bool = false, measure: Bool = true, _ match: (String) -> Bool) {
            guard let names = try? fm.contentsOfDirectory(atPath: dir) else { return }
            for nm in names.sorted() where match(nm.lowercased()) { add(PathUtil.join(dir, nm), kind, admin, measure) }
        }
        func isB(_ x: String) -> Bool { useB && x == b }
        func isN(_ x: String) -> Bool { useN && x == n }
        /// The bundle id as a whole dot-separated part of a name: "TEAMID.com.foo.app" or "com.foo.app".
        func hasToken(_ x: String) -> Bool {
            var from = x.startIndex
            while let r = x.range(of: b, range: from..<x.endIndex) {
                let before = r.lowerBound == x.startIndex || x[x.index(before: r.lowerBound)] == "."
                let after = r.upperBound == x.endIndex || x[r.upperBound] == "."
                if before && after { return true }
                from = x.index(after: r.lowerBound)
            }
            return false
        }
        for lib in [home + "/Library", "/Library"] {
            scan(lib + "/Application Support", "Support files") { isN($0) || isB($0) }
            scan(lib + "/Caches", "Cache") { isB($0) || isN($0) }
            scan(lib + "/Logs", "Logs") { isN($0) || isB($0) }
            if !useB { continue }
            scan(lib + "/Preferences", "Settings") { $0 == b + ".plist" || ($0.hasPrefix(b + ".") && $0.hasSuffix(".plist")) }
            scan(lib + "/Preferences/ByHost", "Settings") { $0.hasPrefix(b + ".") && $0.hasSuffix(".plist") }
            scan(lib + "/Containers", "Container", measure: fullDiskAccess) { $0 == b }
            scan(lib + "/Group Containers", "Container", measure: fullDiskAccess) { hasToken($0) }
            scan(lib + "/Saved Application State", "Saved state") { $0 == b + ".savedstate" }
            scan(lib + "/HTTPStorages", "Web data") { $0 == b || $0 == b + ".binarycookies" }
            scan(lib + "/WebKit", "Web data") { $0 == b }
            scan(lib + "/Cookies", "Cookies") { $0 == b + ".binarycookies" }
            scan(lib + "/LaunchAgents", "Launch agent") { $0.hasPrefix(b) && $0.hasSuffix(".plist") }
            scan(lib + "/Application Scripts", "Scripts") { $0 == b }
        }
        if useB {
            scan("/Library/LaunchDaemons", "Launch daemon", admin: true) { $0.hasPrefix(b) && $0.hasSuffix(".plist") }
            scan("/Library/PrivilegedHelperTools", "Helper tool", admin: true) { $0.hasPrefix(b) }
        }
        return out
    }

    /// Size of a file or folder as the views count it (file size or size on disk).
    static func measureItem(_ p: String) -> Int64 {
        if PathUtil.isDir(p) { return Scanner().run(p).size }
        var st = stat()
        guard lstat(p, &st) == 0 else { return 0 }
        return DirNode.onDisk ? Int64(st.st_blocks) * 512 : Int64(st.st_size)
    }
}

extension MainWindowController {
    func buildAppsPage() -> Page {
        btnAppsRefresh.tip = "List the installed apps again"
        appSearch.toolTip = "Part of an app's name, publisher or bundle id"
        appSearch.onChange = { [unowned self] in self.showApps() }
        btnUninstall.tip = "Move the selected app to the Trash, then choose which of its leftovers to remove"
        btnRemoveLeftovers.tip = "Move the checked leftovers to the Trash"
        for b in [btnAppsRefresh, btnUninstall, btnRemoveLeftovers] { b.compact = true }
        let bar = ActionBar([btnAppsRefresh, appSearch, btnUninstall, btnRemoveLeftovers], right: [btnUninstall, btnRemoveLeftovers])
        bar.minRowHeight = 0
        let p = Page("Apps", [Column("name", "Name", 230), Column("size", "Size", 80, true), Column("size", "% of apps", 125, true),
                              Column("cat", "Publisher", 110), Column("note", "Version", 90), Column("mod", "Installed", 128),
                              Column("folder", "Location", 330)],
                     barColumn: 2, bar: bar)
        p.chartTitle = "Installed apps"
        p.chartScope = "of apps"
        p.empty = "No apps found."
        // Apps with leftovers (uninstalled, or after Look for leftovers) come first, then the rest in the sort order.
        p.pinFirst = { $0.app?.hasLeftovers ?? false }
        p.fill = { [unowned self] in if self.apps == nil { self.loadApps() } else { self.showApps() } }
        p.table.onClickAt = { [unowned self] r, pt in self.appClick(r, pt) }
        return p
    }

    var appSearchRoots: [String] { appRoots ?? InstalledApp.defaultRoots }
    var appsAuto: Bool { selfTestDir != nil || autoAccept }
    /// Moves to the Trash; the self-test and screenshot runs only use the Trash on CI (deleted for good elsewhere).
    var appsTrash: Bool { appsAuto ? ProcessInfo.processInfo.environment["CI"] != nil : true }

    /// Lists the installed apps in the background (sizes from the scan when the bundle was scanned, measured otherwise).
    func loadApps(_ done: (() -> Void)? = nil) {
        let p = pApps!
        appsGen += 1
        let gen = appsGen
        appsLoading = true
        if apps == nil {
            p.setCaption("Looking for installed apps...")
            p.setRows([], "Looking for installed apps...")
            if activePage === p { rowSlice = [:]; chart.showLogo(p.chartTitle, "", "Looking for installed apps...") }
        } else {
            p.setCaption("Looking for installed apps again...")
        }
        updateButtons()
        let roots = appSearchRoots
        readInBackground({ [unowned self] () -> [InstalledApp] in
            InstalledApp.find(roots) { path in
                if let n = self.findScanned(path, exact: true), !n.insideSkipped { return n.size }
                return Scanner().run(path).size
            }
        }, { [weak self] found in
            guard let self = self, gen == self.appsGen else { return }
            self.appsLoading = false
            // Uninstalled apps with leftovers stay; leftovers found earlier stay with their app.
            let old = self.apps ?? []
            for a in found {
                if let o = old.first(where: { PathUtil.same($0.path, a.path) && !$0.uninstalled }) { a.leftovers = o.leftovers; a.expanded = o.expanded }
            }
            let kept = old.filter { o in o.uninstalled && o.hasLeftovers && !found.contains(where: { PathUtil.same($0.path, o.path) }) }
            self.apps = kept + found
            self.showApps()
            done?()
        })
    }

    /// The apps shown: matching the search box.
    var shownApps: [InstalledApp] {
        let q = appSearch.text.trimmingCharacters(in: .whitespaces).lowercased()
        let all = apps ?? []
        if q.isEmpty { return all }
        return all.filter { $0.name.lowercased().contains(q) || $0.publisher.lowercased().contains(q) || $0.bundleId.lowercased().contains(q) }
    }

    func showApps() {
        let p = pApps!
        guard let all = apps else { return }
        let list = shownApps
        let total = list.reduce(Int64(0)) { $0 + max(0, $1.shownSize) }
        let keep = Set(p.selectedItems.compactMap { appKey($0) })
        var rows: [ItemInfo] = []
        for a in list {
            let it = ItemInfo()
            it.app = a
            it.name = a.displayName
            it.size = a.shownSize
            it.folderIcon = true
            it.path = a.uninstalled ? nil : a.path
            it.category = a.uninstalled ? "" : a.publisher
            it.note = a.uninstalled ? "" : a.version
            it.modified = a.uninstalled ? 0 : Int64(a.installed?.timeIntervalSince1970 ?? 0)
            it.folder = a.path
            it.percent = total > 0 ? 100 * Double(max(0, it.size)) / Double(total) : 0
            it.hideBar = a.uninstalled
            it.cells = [it.name, Fmt.size(it.size), a.uninstalled ? "" : Fmt.pct(it.percent), it.category, it.note, Fmt.date(it.modified), it.folder]
            it.tip = a.name + "\n" + a.path + (a.bundleId.isEmpty ? "" : "\n" + a.bundleId)
            if a.uninstalled { it.color = T.WARN_FG }
            if a.hasLeftovers {
                it.chevron = a.expanded ? 1 : 0
                it.check = a.checkState
            }
            rows.append(it)
            if !a.hasLeftovers || !a.expanded { continue }
            for l in a.leftovers ?? [] {
                let c = ItemInfo()
                c.app = a
                c.leftover = l
                c.groupOf = it
                c.name = l.kind + ": " + PathUtil.name(l.path)
                c.size = max(0, l.size)
                c.folderIcon = l.isDir
                c.path = l.path
                c.category = l.note
                c.folder = l.path
                c.percent = total > 0 ? 100 * Double(c.size) / Double(total) : 0
                c.hideBar = true
                c.cells = [c.name, l.size < 0 ? "—" : Fmt.size(l.size), "", l.note, "", "", l.path]
                c.tip = c.name + "\n" + l.path + (l.note.isEmpty ? "" : "\n" + l.note)
                c.check = l.checked ? 1 : 0
                rows.append(c)
            }
        }
        let filtered = !appSearch.text.trimmingCharacters(in: .whitespaces).isEmpty
        p.setRows(rows, filtered ? "No apps match the search." : "No apps found in " + appSearchRoots.joined(separator: ", ") + ".")
        if !keep.isEmpty {
            var sel = IndexSet()
            for (i, it) in p.items.enumerated() { if let k = appKey(it), keep.contains(k) { sel.insert(i) } }
            p.select(sel)
        }
        let installed = list.filter { !$0.uninstalled }
        let size = installed.reduce(Int64(0)) { $0 + $1.size }
        let n = installed.count
        if all.isEmpty && appsLoading { p.setCaption("Looking for installed apps...") }
        else { p.setCaption("\(Fmt.count(n)) app\(n == 1 ? "" : "s"), \(Fmt.size(size)).  Select an app and press Uninstall: it is moved to the Trash, then you choose which leftovers to remove.") }
        p.chartSub = Fmt.size(total)
        p.chartTotal = 0
        p.empty = filtered ? "No apps match." : "No apps found."
        updateButtons()
        if activePage === p { refreshChart() }
    }

    private func appKey(_ it: ItemInfo) -> String? {
        guard let a = it.app else { return nil }
        if let l = it.leftover { return "l\n" + l.path }
        return "a\n" + a.path + (a.uninstalled ? "\nu" : "")
    }

    /// The one installed app selected (a head row), if exactly one row is selected.
    var selectedApp: InstalledApp? {
        guard pApps != nil else { return nil }
        let sel = pApps.selectedItems
        guard sel.count == 1, sel[0].groupOf == nil, let a = sel[0].app else { return nil }
        return a
    }

    func updateAppButtons() {
        let idle = !busy && !scanning && !appsWorking
        btnAppsRefresh.isEnabled = idle && !appsLoading
        btnUninstall.isEnabled = idle && (selectedApp.map { !$0.uninstalled } ?? false)
        btnRemoveLeftovers.isEnabled = idle && (apps ?? []).contains { !$0.checkedLeftovers.isEmpty }
    }

    // MARK: checks and expanding (as in Suggestions)

    func appChecksChanged() {
        for it in pApps.items {
            if let l = it.leftover { it.check = l.checked ? 1 : 0 }
            else if let a = it.app, a.hasLeftovers { it.check = a.checkState }
        }
        pApps.table.redrawCells()
        updateButtons()
    }

    func toggleAppChecks(_ rows: [ItemInfo]) {
        let rows = rows.filter { $0.check >= 0 }
        if rows.isEmpty { return }
        let on = rows.contains { $0.check != 1 }
        for it in rows {
            if let l = it.leftover { l.checked = on } else { it.app?.checkAll(on) }
        }
        appChecksChanged()
    }

    func expandApps(_ rows: [ItemInfo], _ open: Bool?) {
        var changed = false
        for it in rows {
            guard let a = it.app, a.hasLeftovers else { continue }
            let to = open ?? !a.expanded
            if a.expanded != to { a.expanded = to; changed = true }
        }
        if changed { showApps() }
    }

    func appClick(_ row: Int, _ pt: NSPoint) -> Bool {
        let p = pApps!
        guard row < p.items.count, p.items[row].app != nil,
              let ci = p.table.tableColumns.firstIndex(where: { $0.identifier.rawValue == "c0" }) else { return false }
        let it = p.items[row]
        let x = pt.x - p.table.rect(ofColumn: ci).minX
        if it.chevron >= 0 && x >= ListCell.chevronX - 3 && x < ListCell.chevronX + 17 { expandApps([it], nil); return true }
        let cx = ListCell.checkX(it)
        if it.check >= 0 && x >= cx - 4 && x < cx + 18 { toggleAppChecks([it]); return true }
        return false
    }

    func appsKeyDown(_ e: NSEvent) -> Bool {
        if e.modifierFlags.contains(.command) || e.modifierFlags.contains(.option) || e.modifierFlags.contains(.control) { return false }
        let sel = pApps.selectedItems
        if sel.isEmpty { return false }
        switch e.keyCode {
        case 49: toggleAppChecks(sel); return true
        case 123: expandApps(sel, false); return true
        case 124: expandApps(sel, true); return true
        default: return false
        }
    }

    // MARK: uninstall

    /// Uninstall... : asks, quits the app when it runs, offers its own uninstaller, moves it to the Trash, then lists
    /// its leftovers under it. done(true) when the app was removed.
    func uninstallSelectedApp(_ done: ((Bool) -> Void)? = nil) {
        guard let a = selectedApp, !a.uninstalled, !busy, !scanning, !appsWorking else { done?(false); return }
        uninstallApp(a, done)
    }

    func uninstallApp(_ a: InstalledApp, _ done: ((Bool) -> Void)? = nil) {
        if PathUtil.same(a.path, Bundle.main.bundlePath) {
            alert("Disk Space Analyzer can't uninstall itself. Quit it, then move it to the Trash in Finder.")
            done?(false); return
        }
        let go = { [unowned self] in
            self.quitIfRunning(a) { ok in
                guard ok else { done?(false); return }
                self.offerVendorUninstaller(a) { self.trashApp(a, done) }
            }
        }
        if appsAuto { go(); return }
        let al = NSAlert()
        al.alertStyle = .warning
        al.messageText = "Uninstall \(a.name) (\(Fmt.size(a.size)))?"
        al.informativeText = "The app is moved to the Trash. Then Disk Space Analyzer looks for its leftovers (settings, caches, support files) and lets you choose what to remove."
        al.addButton(withTitle: "Cancel")
        al.addButton(withTitle: "Uninstall")
        al.beginSheetModal(for: window!) { r in if r == .alertSecondButtonReturn { go() } else { done?(false) } }
    }

    /// Asks a running app to quit and waits up to 5 s; false (and a message) when it is still running.
    private func quitIfRunning(_ a: InstalledApp, _ then: @escaping (Bool) -> Void) {
        let me = NSRunningApplication.current
        let running = NSWorkspace.shared.runningApplications.filter { (r: NSRunningApplication) -> Bool in
            if r == me { return false }
            if !a.bundleId.isEmpty && r.bundleIdentifier == a.bundleId { return true }
            guard let u = r.bundleURL else { return false }
            return PathUtil.same(u.path, a.path)
        }
        if running.isEmpty { then(true); return }
        statusLabel.stringValue = "Asking \(a.name) to quit..."
        for r in running { r.terminate() }
        let end = Date().addingTimeInterval(5)
        func poll() {
            if running.allSatisfy({ $0.isTerminated }) { then(true); return }
            if Date() > end {
                statusLabel.stringValue = "\(a.name) is still running."
                if !appsAuto { alert("\(a.name) is still running. Quit it, then press Uninstall again.") }
                then(false)
                return
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { poll() }
        }
        poll()
    }

    /// When the app brings its own uninstaller: Run its uninstaller (then wait for it) or Move to Trash.
    private func offerVendorUninstaller(_ a: InstalledApp, _ then: @escaping () -> Void) {
        guard !appsAuto, let u = InstalledApp.vendorUninstaller(a, roots: appSearchRoots) else { then(); return }
        let al = NSAlert()
        al.messageText = "\(a.name) has its own uninstaller. Run it first?"
        al.informativeText = "Its uninstaller may also remove parts installed elsewhere.\n\(u)"
        al.addButton(withTitle: "Move to Trash")
        al.addButton(withTitle: "Run its uninstaller")
        al.beginSheetModal(for: window!) { [unowned self] r in
            if r == .alertSecondButtonReturn { self.runVendorUninstaller(a, u, then) } else { then() }
        }
    }

    /// Opens the vendor uninstaller and waits until it quits or the app is gone, or the user presses Continue.
    private func runVendorUninstaller(_ a: InstalledApp, _ path: String, _ then: @escaping () -> Void) {
        guard let win = window else { then(); return }
        var launched: NSRunningApplication?
        var finished = false
        let wait = NSAlert()
        wait.messageText = "Waiting for the uninstaller to finish..."
        wait.informativeText = "Press Continue when it is done."
        wait.addButton(withTitle: "Continue")
        appsWorking = true
        updateButtons()
        wait.beginSheetModal(for: win) { [unowned self] _ in
            if finished { return }
            finished = true
            self.appsWorking = false
            then()
        }
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: path), configuration: NSWorkspace.OpenConfiguration()) { r, _ in
            DispatchQueue.main.async { launched = r }
        }
        var started = false
        func poll() {
            if finished { return }
            if let l = launched, !l.isTerminated { started = true }
            if (started && launched?.isTerminated == true) || !PathUtil.exists(a.path) {
                win.endSheet(wait.window)
                return
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { poll() }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { poll() }
    }

    /// Moves the bundle to the Trash (unless its uninstaller removed it), then looks for its leftovers.
    private func trashApp(_ a: InstalledApp, _ done: ((Bool) -> Void)?) {
        let gone = { [unowned self] in
            a.uninstalled = true
            self.findLeftovers(a) { found in
                let size = a.leftoverSize
                if found.isEmpty {
                    self.apps?.removeAll { $0 === a }
                    self.showApps()
                    self.statusLabel.stringValue = "\(a.name) was removed completely."
                } else {
                    self.statusLabel.stringValue = "Moved \(a.name) to the Trash. Found \(Fmt.count(found.count)) leftover\(found.count == 1 ? "" : "s") (\(Fmt.size(size)))."
                }
                done?(true)
            }
        }
        if !PathUtil.exists(a.path) { gone(); return }
        let parent = PathUtil.parent(a.path)
        if access(a.path, W_OK) != 0 || access(parent, W_OK) != 0 { cannotMove(a); done?(false); return }
        let t = DeleteTarget(path: a.path, label: a.name, isDir: true, size: a.size)
        runDelete([t], trash: appsTrash, showErrors: false) { [unowned self] _ in
            if PathUtil.exists(a.path) { self.cannotMove(a); done?(false); return }
            gone()
        }
    }

    private func cannotMove(_ a: InstalledApp) {
        statusLabel.stringValue = "\(a.name) could not be moved to the Trash."
        if appsAuto { return }
        let al = NSAlert()
        al.messageText = "\(a.name) can't be moved to the Trash"
        al.informativeText = "Disk Space Analyzer can't move it (owned by the system). Delete it in Finder (it asks for your password), then press Look for leftovers."
        al.addButton(withTitle: "Show in Finder")
        al.addButton(withTitle: "OK")
        al.beginSheetModal(for: window!) { r in
            if r == .alertFirstButtonReturn { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: a.path)]) }
        }
    }

    /// Looks for an app's leftovers in the background and shows them under its row (checked, except admin-only ones).
    func findLeftovers(_ a: InstalledApp, _ done: (([AppLeftover]) -> Void)? = nil) {
        appsWorking = true
        updateButtons()
        statusLabel.stringValue = "Looking for leftovers of \(a.name)..."
        let id = a.bundleId, name = a.name
        DispatchQueue.global(qos: .userInitiated).async {
            let found = InstalledApp.findLeftovers(bundleId: id, name: name)
            DispatchQueue.main.async {
                self.appsWorking = false
                a.leftovers = found
                a.expanded = true
                self.showApps()
                if let i = self.pApps.items.firstIndex(where: { $0.app === a && $0.groupOf == nil }) {
                    self.pApps.select(IndexSet(integer: i))
                    self.pApps.table.scrollRowToVisible(i)
                }
                done?(found)
            }
        }
    }

    /// Context menu "Look for leftovers": without uninstalling (an app deleted in Finder meanwhile counts as uninstalled).
    func lookForLeftoversOfSelected(_ done: (([AppLeftover]) -> Void)? = nil) {
        guard let a = selectedApp, !busy, !appsWorking else { done?([]); return }
        if !a.uninstalled && !PathUtil.exists(a.path) { a.uninstalled = true }
        findLeftovers(a) { [unowned self] found in
            if found.isEmpty && a.uninstalled {
                self.apps?.removeAll { $0 === a }
                self.showApps()
                self.statusLabel.stringValue = "\(a.name) was removed completely."
            } else {
                self.statusLabel.stringValue = found.isEmpty ? "No leftovers of \(a.name) found."
                    : "Found \(Fmt.count(found.count)) leftover\(found.count == 1 ? "" : "s") of \(a.name) (\(Fmt.size(a.leftoverSize)))."
            }
            done?(found)
        }
    }

    /// Remove leftovers: moves the checked leftovers to the Trash, then measures what is left of them.
    func removeCheckedLeftovers(_ done: (() -> Void)? = nil) {
        if busy || scanning || appsWorking { done?(); return }
        let owners = (apps ?? []).filter { !$0.checkedLeftovers.isEmpty }
        let picked = owners.flatMap { a in a.checkedLeftovers.map { (a, $0) } }
        if picked.isEmpty { done?(); return }
        let total = picked.reduce(Int64(0)) { $0 + max(0, $1.1.size) }
        let n = picked.count, s = n == 1 ? "" : "s"
        let run = { [unowned self] in
            let targets = picked.map { DeleteTarget(path: $0.1.path, label: PathUtil.name($0.1.path), isDir: $0.1.isDir, size: max(0, $0.1.size)) }
            self.runDelete(targets, trash: self.appsTrash) { [unowned self] _ in
                var complete: [InstalledApp] = []
                for a in owners {
                    var keep: [AppLeftover] = []
                    for l in a.leftovers ?? [] {
                        if !PathUtil.exists(l.path) { continue }
                        if picked.contains(where: { $0.1 === l }) {
                            l.size = InstalledApp.measureItem(l.path)
                            l.note = "partly removed"
                        }
                        keep.append(l)
                    }
                    a.leftovers = keep
                    if keep.isEmpty && a.uninstalled { complete.append(a) }
                }
                self.apps?.removeAll { a in complete.contains { $0 === a } }
                self.showApps()
                if complete.count == 1 { self.statusLabel.stringValue = "\(complete[0].name) was removed completely." }
                done?()
            }
        }
        if appsAuto { run(); return }
        let al = NSAlert()
        al.messageText = owners.count == 1 ? "Move \(n) leftover\(s) of \(owners[0].name) (\(Fmt.size(total))) to the Trash?"
                                          : "Move \(n) leftover\(s) of \(owners.count) apps (\(Fmt.size(total))) to the Trash?"
        var list = picked.prefix(12).map { "    " + Fmt.shorten($0.1.path, 90) }.joined(separator: "\n")
        if n > 12 { list += "\n    ... and \(Fmt.count(n - 12)) more" }
        al.informativeText = list + "\n\nYou can put them back from the Trash."
        al.addButton(withTitle: "Cancel")
        al.addButton(withTitle: "Move to Trash")
        al.beginSheetModal(for: window!) { r in if r == .alertSecondButtonReturn { run() } else { done?() } }
    }
}

/// A fake app bundle and its leftovers for the self-test and the screenshots.
enum FakeApp {
    static let name = "DSA Test App"
    static let bundleId = "com.example.dsatestapp"

    static var leftoverPaths: [String] {
        let lib = PathUtil.home + "/Library"
        return [lib + "/Application Support/" + name, lib + "/Caches/" + bundleId, lib + "/Preferences/" + bundleId + ".plist"]
    }

    static func writeBundle(_ path: String, id: String, name: String, version: String, bytes: Int) {
        let fm = FileManager.default
        try? fm.createDirectory(atPath: path + "/Contents/MacOS", withIntermediateDirectories: true)
        let info: NSDictionary = ["CFBundleIdentifier": id, "CFBundleName": name, "CFBundleShortVersionString": version,
                                  "CFBundleExecutable": "DSATestApp", "CFBundlePackageType": "APPL"]
        _ = info.write(toFile: path + "/Contents/Info.plist", atomically: true)
        fm.createFile(atPath: path + "/Contents/MacOS/DSATestApp", contents: Data(count: bytes))
    }

    /// <apps>/DSA Test App.app (50 KB) and its 3 leftovers in ~/Library.
    static func create(in apps: String) -> String {
        let fm = FileManager.default
        let app = apps + "/" + name + ".app"
        writeBundle(app, id: bundleId, name: name, version: "1.0", bytes: 50 * 1024)
        let l = leftoverPaths
        try? fm.createDirectory(atPath: l[0], withIntermediateDirectories: true)
        fm.createFile(atPath: l[0] + "/data.bin", contents: Data(count: 20 * 1024))
        try? fm.createDirectory(atPath: l[1], withIntermediateDirectories: true)
        fm.createFile(atPath: l[1] + "/c.bin", contents: Data(count: 10 * 1024))
        let prefs: NSDictionary = ["selfTest": true]
        _ = prefs.write(toFile: l[2], atomically: true)
        return app
    }

    /// Removes the leftovers and whatever the tests moved to the Trash.
    static func cleanUp() {
        let fm = FileManager.default
        for p in leftoverPaths { try? fm.removeItem(atPath: p) }
        let trash = PathUtil.home + "/.Trash"
        for p in DeleteJob.children(trash) {
            let n = PathUtil.name(p)
            if n.hasPrefix(name) || n.hasPrefix(bundleId) || n.hasPrefix("Apple Fake") { try? fm.removeItem(atPath: p) }
        }
    }
}
