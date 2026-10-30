import AppKit

/// A chain of asynchronous steps on the main thread: each step calls next() when it is done.
final class Steps {
    private var list: [(@escaping () -> Void) -> Void] = []
    private var index = 0
    private var done: (() -> Void)?

    func add(_ s: @escaping (@escaping () -> Void) -> Void) { list.append(s) }
    func then(_ s: @escaping () -> Void) { add { next in s(); next() } }
    func wait(_ secs: Double) { add { next in DispatchQueue.main.asyncAfter(deadline: .now() + secs, execute: next) } }

    /// Waits until cond() is true or the timeout passes.
    func waitUntil(_ timeout: Double, _ cond: @escaping () -> Bool) {
        add { next in
            let end = Date().addingTimeInterval(timeout)
            func poll() {
                if cond() || Date() > end { next(); return }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { poll() }
            }
            poll()
        }
    }

    func run(_ done: @escaping () -> Void) { self.done = done; index = 0; advance() }

    private func advance() {
        if index >= list.count { done?(); return }
        let s = list[index]
        index += 1
        s { DispatchQueue.main.async { self.advance() } }
    }
}

/// Builds the self-test folder: 10 files in 8 folders, two identical files, a project (package.json) with node_modules.
enum SelfTest {
    static func prepare(_ dir0: String) -> String {
        var dir = (dir0 as NSString).expandingTildeInPath
        if !dir.hasPrefix("/") { dir = FileManager.default.currentDirectoryPath + "/" + dir }
        dir = PathUtil.trim((dir as NSString).standardizingPath)
        let fm = FileManager.default
        try? fm.removeItem(atPath: dir)
        var g = SystemRandomNumberGenerator()
        func random(_ n: Int) -> Data { Data((0..<n).map { _ in UInt8.random(in: 0...255, using: &g) }) }
        func write(_ rel: String, _ data: Data) {
            let p = dir + "/" + rel
            try? fm.createDirectory(atPath: PathUtil.parent(p), withIntermediateDirectories: true)
            fm.createFile(atPath: p, contents: data)
        }
        write("A/a1.txt", random(1000))
        write("A/a2.txt", random(2000))
        write("A/sub/a3.txt", random(3000))
        write("B/b1.bin", random(50000))
        write("B/b2.bin", random(60000))
        let dup = random(200000)
        write("C/dup1.bin", dup)
        write("D/dup2.bin", dup)
        write("C/other.bin", random(200000))
        write("P/package.json", Data("{\"name\": \"p\", \"version\": \"1.0.0\"}".utf8))
        write("P/node_modules/x/index.js", random(4000))
        return dir
    }
}

/// End-to-end self-test of the real window (--cli selftest): live updates, duplicates, suggestions, the delete list with
/// the Trash (on CI) or permanent delete, contents-only delete, file delete, size on disk, export and history. Writes
/// <folder>.log and exits with 0 (passed) or 1.
final class SelfTestRun {
    let w: MainWindowController
    let dir: String
    var log = ""
    var failed = false
    let steps = Steps()

    init(_ w: MainWindowController, _ dir: String) { self.w = w; self.dir = dir }

    func check(_ ok: Bool, _ what: String) {
        log += (ok ? "PASS  " : "FAIL  ") + what + "\n"
        if !ok { failed = true }
    }

    func P(_ rel: String) -> String { dir + "/" + rel }
    func find(_ rel: String) -> DirNode? { w.findScanned(P(rel), exact: true) }
    var root: DirNode? { w.findRoot(dir)?.node }

    func start() {
        let w = self.w
        // Start-up: Contents is the first tab and open, Suggestions second; suggestions are found by themselves.
        steps.then {
            self.check(w.pages.first === w.pContents && w.pages.count > 1 && w.pages[1] === w.pSuggest && w.viewTabs.selectedIndex == 0 && w.activePage === w.pContents && w.viewTabs.titles.first == "Contents",
                       "start: Contents is the first tab and the one open, Suggestions second")
            self.check(w.pages.count > 2 && w.pages[2] === w.pApps && w.viewTabs.titles == w.pages.map { $0.title },
                       "start: Apps is the third tab; tab titles match the views (\(w.viewTabs.titles.joined(separator: ", ")))")
            if let i = w.pages.firstIndex(where: { $0 === w.pSuggest }) { w.viewTabs.selectedIndex = i }
        }
        steps.waitUntil(90) { w.suggestions != nil && !w.suggestFinding }
        steps.then {
            self.check(w.suggestions != nil && !w.pSuggest.headline.text.isEmpty == !w.shownSuggestions.isEmpty,
                       "start: suggestions found when the tab is opened (headline \"\(w.pSuggest.headline.text)\")")
            // Picking a folder in the tree (keyboard: Down) while Suggestions is open shows the Contents of that folder.
            w.window?.makeFirstResponder(w.outline)
            if let r = self.root { w.selectDir(r) }
            let before = w.outline.selectedRow
            let down = String(UnicodeScalar(NSDownArrowFunctionKey)!)
            if let e = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                        windowNumber: w.window?.windowNumber ?? 0, context: nil, characters: down,
                                        charactersIgnoringModifiers: down, isARepeat: false, keyCode: 125) {
                w.outline.keyDown(with: e)
            }
            self.check(w.outline.selectedRow != before && w.activePage === w.pContents,
                       "tree: picking a folder while Suggestions is open switches to Contents (row \(before) -> \(w.outline.selectedRow))")
            // Programmatic tree changes do not switch views.
            if let i = w.pages.firstIndex(where: { $0 === w.pSuggest }) { w.viewTabs.selectedIndex = i }
            if let r = self.root { w.selectDir(r) }
            self.check(w.activePage === w.pSuggest, "tree: a folder selected by the app keeps the Suggestions view open")
        }
        steps.then {
            let top = self.root
            self.check(top != nil && top!.files == 10 && top!.dirs == 8, "scan: \(top?.files ?? -1) files, \(top?.dirs ?? -1) folders (expected 10, 8)")
            // iCloud items are never downloaded: the process-wide policy is set at start-up.
            self.check(Dataless.policyResult == 0 && Dataless.processPolicy == Dataless.materializeOff,
                       "iopolicy: setiopolicy_np(materialize dataless files, process, off) returned \(Dataless.policyResult) (errno \(Dataless.policyErrno)), policy now \(Dataless.processPolicy)")
            // Protected folders: listed, not entered (D stands in for one).
            let s = Scanner()
            s.alsoBlocked = [self.P("D")]
            let r = s.run(self.dir)
            let d = r.child("D")
            self.check(d != nil && d!.skipped == .needsAccess && d!.files == 0 && d!.displayName.hasSuffix("[needs Full Disk Access]") && r.files == 9 && r.dirs == 8,
                       "protected folder listed but not entered (\(r.files) files, \(r.dirs) folders; expected 9, 8)")
            let granted = FullDiskAccess.granted
            let blocked = FullDiskAccess.blocked(for: "/")
            let desktop = PathUtil.home + "/Desktop"
            self.check(granted ? blocked.isEmpty : (blocked.contains(desktop) && !FullDiskAccess.blocked(for: desktop).contains(desktop)),
                       "Full Disk Access \(granted ? "granted" : "not granted"): \(blocked.count) protected folders skipped in a scan of /")
            // Live updates: a new file and a new folder appear without a rescan.
            FileManager.default.createFile(atPath: self.P("A/new.txt"), contents: Data(count: 5000))
            try? FileManager.default.createDirectory(atPath: self.P("E/deep"), withIntermediateDirectories: true)
            FileManager.default.createFile(atPath: self.P("E/deep/e.txt"), contents: Data(count: 7000))
        }
        steps.waitUntil(20) {
            guard let a = self.find("A"), let e = self.find("E/deep"), let r = self.root else { return false }
            return a.localFiles == 3 && e.files == 1 && r.files == 12
        }
        steps.then {
            let a = self.find("A"), e = self.find("E/deep")
            self.check(a != nil && a!.localFiles == 3, "live: new file in A noticed")
            self.check(e != nil && e!.files == 1 && self.root?.files == 12, "live: new folder E/deep with its file noticed (total \(self.root?.files ?? -1) files)")
            // Duplicates: dup1.bin and dup2.bin are identical; other.bin has the same size but other content.
            if let r = self.root { w.selectDir(r) }
            w.dupeMin.selectedIndex = 3
            w.viewTabs.selectedIndex = w.pages.firstIndex { $0 === w.pDupes } ?? 3
            w.findDuplicates()
        }
        steps.waitUntil(60) { w.dupeFinder == nil }
        steps.then {
            let g = w.dupeGroups ?? []
            self.check(g.count == 1 && g[0].count == 2 && g[0].allSatisfy { $0.name.hasPrefix("dup") }, "duplicates: one group (dup1.bin, dup2.bin)")
        }
        steps.add { next in
            // Suggestions: the node_modules folder of project P.
            let nodes = w.roots.compactMap { $0.node }
            w.readInBackground({ w.findSuggestions(nodes) }, { sug in
                let nm = sug.first { $0.name == "node_modules folders" }
                self.check(nm != nil && nm!.targets.count == 1 && PathUtil.same(nm!.targets[0].path, self.P("P/node_modules")), "suggestions: node_modules of P found")
                next()
            })
        }
        steps.then {
            // Safety: only node_modules of the user's own projects are suggested, never those inside an app bundle or an
            // editor's extensions (deleting VS Code's built-in extension once broke it on Windows).
            let nmDir = self.dir + "-nm"
            let fm = FileManager.default
            try? fm.removeItem(atPath: nmDir)
            func project(_ rel: String, packageJson: Bool = true) -> String {
                let p = nmDir + "/" + rel
                try? fm.createDirectory(atPath: p + "/node_modules/pkg", withIntermediateDirectories: true)
                fm.createFile(atPath: p + "/node_modules/pkg/index.js", contents: Data(count: 3000))
                if packageJson { fm.createFile(atPath: p + "/package.json", contents: Data("{\"name\": \"x\"}".utf8)) }
                return p + "/node_modules"
            }
            let own = project("work/my-project")
            let inApp = project("X.app/Contents/Resources/app")
            let inExt = project(".vscode/extensions/foo")
            let noPackage = project("loose", packageJson: false)
            let found = MainWindowController.projectNodeModules(Scanner().run(nmDir)).map { $0.fullPath }
            self.check(found.count == 1 && PathUtil.same(found[0], own) && MainWindowController.isProjectNodeModules(own),
                       "node_modules safety: a project's node_modules (package.json next to it) is suggested (\(found.count) found)")
            self.check(!MainWindowController.isProjectNodeModules(inApp) && !found.contains { PathUtil.same($0, inApp) },
                       "node_modules safety: not inside an app bundle (X.app/Contents/Resources/app)")
            self.check(!MainWindowController.isProjectNodeModules(inExt) && !found.contains { PathUtil.same($0, inExt) },
                       "node_modules safety: not inside an editor's extensions (.vscode/extensions/foo)")
            self.check(!MainWindowController.isProjectNodeModules(noPackage)
                       && !MainWindowController.isProjectNodeModules(PathUtil.home + "/Library/foo/node_modules")
                       && !MainWindowController.isProjectNodeModules("/Applications/Code.app/Contents/Resources/app/node_modules"),
                       "node_modules safety: not without package.json, under ~/Library or /Applications")
            try? fm.removeItem(atPath: nmDir)
        }
        var before: Int64 = 0
        let ci = ProcessInfo.processInfo.environment["CI"] != nil
        steps.add { next in
            // Delete list: folder B to the Trash on CI (permanently here, to keep the user's Trash clean).
            guard let b = self.find("B") else { self.check(false, "delete list: folder B not in the scan"); next(); return }
            w.basket.removeAll()
            let t = DeleteTarget(path: b.fullPath, label: "B", isDir: true, size: b.size)
            t.dir = b
            w.addTarget(t)
            w.addTarget(DeleteTarget(path: self.P("B/b1.bin"), label: "b1.bin", size: 1))   // covered by B: not added
            self.check(w.basket.count == 1, "delete list: a file inside a listed folder is not added twice")
            before = self.root?.files ?? 0
            w.confirmAndDelete(w.basket, trash: ci) { job in
                self.check(job != nil && !PathUtil.exists(self.P("B")) && self.find("B") == nil && self.root?.files == before - 2,
                           (ci ? "Trash" : "permanent delete") + ": folder B gone from disk and scan (\(self.root?.files ?? -1) files left)")
                next()
            }
        }
        steps.add { next in
            // Contents only: A stays, its files and subfolder go.
            guard let a = self.find("A") else { self.check(false, "contents only: A not in the scan"); next(); return }
            let t = DeleteTarget(path: a.fullPath, label: "A", isDir: true)
            t.contentsOnly = true
            t.dir = a
            w.confirmAndDelete([t], trash: false) { _ in
                let n = self.find("A")
                let left = (try? FileManager.default.contentsOfDirectory(atPath: self.P("A")))?.count ?? -1
                self.check(PathUtil.isDir(self.P("A")) && left == 0 && n != nil && n!.files == 0 && n!.dirs == 0, "contents only: A kept and emptied, in the scan too")
                next()
            }
        }
        steps.add { next in
            // A single file from the index.
            guard let c = self.find("C"), let other = Scanner.listFiles(c).first(where: { $0.name == "other.bin" }) else {
                self.check(false, "file delete: other.bin not in the index"); next(); return
            }
            let t = DeleteTarget(path: other.fullPath, label: other.name, size: other.size)
            t.file = other
            w.confirmAndDelete([t], trash: false) { _ in
                let c2 = self.find("C")
                self.check(!PathUtil.exists(self.P("C/other.bin")) && c2 != nil && !(c2!.fileList ?? []).contains { $0.name == "other.bin" } && c2!.localFiles == 1,
                           "file delete: other.bin gone from disk and index")
                self.check(self.root?.files == 5, "totals after deletes: \(self.root?.files ?? -1) files (expected 5)")
                next()
            }
        }
        // Deleting: the themed confirmation, then the delete strip (danger colours) while rows leave the open list.
        steps.then {
            let fm = FileManager.default
            for i in 1...4 {
                try? fm.createDirectory(atPath: self.P("S/s\(i)"), withIntermediateDirectories: true)
                for k in 1...15 { fm.createFile(atPath: self.P("S/s\(i)/f\(k).bin"), contents: Data(count: 2000)) }
            }
        }
        steps.waitUntil(20) { (1...4).allSatisfy { self.find("S/s\($0)")?.files == 15 } }
        steps.add { next in
            guard let s = self.find("S"), s.children.count == 4 else { self.check(false, "delete strip: folder S not noticed"); next(); return }
            if let i = w.pages.firstIndex(where: { $0 === w.pContents }) { w.viewTabs.selectedIndex = i }
            w.selectDir(s)
            let rows = { w.pContents.items.filter { $0.isDir && $0.dir?.parent === s }.count }
            let before = rows()
            let targets = s.children.map { c -> DeleteTarget in
                let t = DeleteTarget(path: c.fullPath, label: c.name, isDir: true, size: c.size)
                t.dir = c
                return t
            }
            let total = targets.reduce(Int64(0)) { $0 + $1.size }
            // The confirmation is the app's own dialog: a details box with a line per item, the red Delete permanently
            // button, Cancel the default (Return cancels).
            let d = w.deleteConfirmDialog(targets, trash: false)
            let lines = d.detailsView?.string.components(separatedBy: "\n") ?? []
            self.check(d.detailLines.count == 4 && lines.count == 4 && d.detailsBox != nil && lines[0].hasSuffix("   " + Fmt.size(targets[0].size))
                       && d.buttonTitles.first == "Delete permanently" && d.buttonKinds.first == .danger && d.icon == .danger
                       && d.heading == "Delete 4 items permanently?" && d.text == "\(Fmt.size(total)) will be deleted. This bypasses the Trash and cannot be undone."
                       && d.window?.backgroundColor == T.BG && d.buttonTitles[d.defaultIndex] == "Cancel" && d.defaultIndex == d.cancelIndex,
                       "prompt: themed delete confirmation \"\(d.heading)\" with \(lines.count) detail lines, buttons \(d.buttonTitles) (\(d.buttonKinds.first == .danger ? "danger" : "not danger") primary, default \(d.buttonTitles[d.defaultIndex]))")
            var answer = -1
            d.show(over: w.window) { answer = $0 }
            d.handleKey(36)
            self.check(answer == d.cancelIndex && PromptDialog.open.isEmpty && (1...4).allSatisfy { PathUtil.exists(self.P("S/s\($0)")) },
                       "prompt: Return in the confirmation cancels (nothing deleted)")
            let errs = (1...25).map { self.P("X/locked\($0).bin") + "  -  Permission denied" }
            let e = w.deleteErrorsDialog(errs, trash: false)
            self.check(e.heading == "25 items could not be deleted" && e.text.hasPrefix("They are in use or access was denied.") && e.icon == .warn
                       && e.detailLines == errs && (e.detailsView?.string.components(separatedBy: "\n").count ?? 0) == 25,
                       "prompt: delete errors \"\(e.heading)\" list all 25 items")
            var sawStrip = false, sawDanger = false, early = -1, status = "", sawBar = false
            var finished = false
            func poll() {
                if finished { return }
                if w.deleteJob != nil {
                    let bar = w.deleteBar
                    if !bar.isHidden && bar.frame.height > 0 { sawStrip = true }
                    if bar.danger && bar.fillColor == T.DANGER_BG && bar.outlineColor == T.DANGER_BORDER && bar.barColor == T.DANGER
                        && bar.spinner.color == T.DANGER && bar.cancelButton.title == "Stop" { sawDanger = true }
                    if bar.status.hasPrefix("Deleting permanently: ") && bar.status.hasSuffix(" freed") && status.isEmpty && bar.fraction > 0 { status = bar.status }
                    if bar.fraction > 0 && bar.fraction < 1 { sawBar = true }
                    let left = rows()
                    if left < before && early < 0 { early = before - left }
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { poll() }
            }
            DeleteJob.throttle = 25_000
            poll()
            w.confirmAndDelete(targets, trash: false) { job in
                finished = true
                DeleteJob.throttle = 0
                self.check(sawStrip && sawDanger && sawBar && !status.isEmpty,
                           "delete strip: shown while deleting, red outline on dark red, red spinner and bar, Stop button (status \"\(status)\")")
                self.check(early > 0, "delete strip: rows left the Contents list while the delete ran (\(early) of \(before) gone before it finished)")
                let stale = w.pContents.items.filter { $0.fullPath.map { !PathUtil.exists($0) } ?? false }
                self.check(job != nil && w.deleteBar.isHidden && stale.isEmpty && rows() == 0 && (1...4).allSatisfy { !PathUtil.exists(self.P("S/s\($0)")) },
                           "delete strip: hidden afterwards; the list holds no deleted rows (\(w.pContents.items.count) rows)")
                next()
            }
        }
        steps.then { w.setMeasure(true) }
        steps.waitUntil(5) { DirNode.onDisk }
        steps.then {
            // Size on disk: every non-empty file takes at least one block.
            if let r = self.root { self.check(r.size == r.alloc && r.alloc >= r.bytes, "size on disk \(r.alloc) >= file size \(r.bytes)") }
            else { self.check(false, "size on disk: nothing scanned") }
            w.setMeasure(false)
        }
        steps.waitUntil(5) { !DirNode.onDisk }
        steps.then {
            guard let r = self.root else { self.check(false, "export: nothing scanned"); return }
            let html = MainWindowController.htmlReport(r, "test", "")
            self.check(html.contains("dup1.bin") && html.contains("<svg"), "export: HTML report lists files and has the chart")
            let csvPath = self.dir + ".csv"
            try? MainWindowController.foldersCsv(r).write(toFile: csvPath, atomically: true, encoding: .utf8)
            let lines = ((try? String(contentsOfFile: csvPath, encoding: .utf8)) ?? "").split(whereSeparator: { $0 == "\n" || $0 == "\r\n" || $0 == "\r" })
            self.check(Int64(lines.count) == r.dirs + 2, "export: CSV has a line per folder (\(lines.count) lines, \(r.dirs) folders)")
            try? FileManager.default.removeItem(atPath: csvPath)
        }
        steps.waitUntil(10) { History.load(self.dir) != nil }
        steps.then { self.check(History.load(self.dir) != nil, "history: scan saved for the Changes view") }
        // Partial delete: a folder on the delete list with a file that cannot be removed (its folder is read-only) stays
        // on the list with the size of what is left. (Wait until the echo of the deletes above is no longer ignored.)
        steps.wait(3.5)
        steps.then {
            let fm = FileManager.default
            try? fm.createDirectory(atPath: self.P("R/sub"), withIntermediateDirectories: true)
            fm.createFile(atPath: self.P("R/free.bin"), contents: Data(count: 6000))
            fm.createFile(atPath: self.P("R/sub/locked.bin"), contents: Data(count: 4000))
        }
        steps.waitUntil(20) { self.find("R")?.files == 2 && self.find("R/sub")?.files == 1 }
        steps.add { next in
            guard let r = self.find("R") else { self.check(false, "partial delete: folder R not noticed"); next(); return }
            chmod(self.P("R/sub"), 0o555)
            w.basket.removeAll()
            let t = DeleteTarget(path: r.fullPath, label: "R", isDir: true, size: r.size)
            t.dir = r
            w.addTarget(t)
            let before = t.size
            w.confirmAndDelete(w.basket, trash: false) { _ in
                let e = w.basket.first
                let ok = w.basket.count == 1 && e != nil && PathUtil.same(e!.path, self.P("R")) && e!.size == 4000 && e!.size < before
                    && e!.size == self.find("R")?.size && e!.note.hasPrefix("partly deleted -- 1 item in use or denied")
                self.check(ok, "partial delete: R stays on the delete list with what is left (\(e?.size ?? -1) of \(before) bytes, note \"\(e?.note ?? "")\")")
                chmod(self.P("R/sub"), 0o755)
                w.basket.removeAll()
                w.basketChanged()
                next()
            }
        }
        // Suggestions view: check boxes per item, Select all / Deselect all, a mixed head, delete only the checked items.
        steps.add { next in
            let fm = FileManager.default
            try? fm.createDirectory(atPath: self.P("F"), withIntermediateDirectories: true)
            fm.createFile(atPath: self.P("F/f1.bin"), contents: Data(count: 3000))
            fm.createFile(atPath: self.P("F/f2.bin"), contents: Data(count: 5000))
            let nodes = w.roots.compactMap { $0.node }
            w.readInBackground({ w.findSuggestions(nodes) }, { found in
                guard let nm = found.first(where: { $0.name == "node_modules folders" }) else {
                    self.check(false, "suggestions view: node_modules of P missing"); next(); return
                }
                let fake = Suggestion("Old test files", safe: false, note: "self-test")
                fake.targets = [DeleteTarget(path: self.P("F/f1.bin"), label: "f1.bin", size: 3000), DeleteTarget(path: self.P("F/f2.bin"), label: "f2.bin", size: 5000)]
                fake.size = 8000
                fake.location = self.P("F")
                let list = [nm, fake]
                Suggestion.resetChecks(list)
                self.check(nm.checkState == 1 && fake.checkState == 0, "suggestions view: safe items start checked, review-first items unchecked")
                w.suggestions = list
                w.suggestDirty = false
                w.viewTabs.selectedIndex = w.pages.firstIndex { $0 === w.pSuggest } ?? 4
                w.showSuggestions()
                w.checkAllSuggestions(true)
                let total = list.reduce(Int64(0)) { $0 + $1.size }
                self.check(w.pSuggest.headline.text == Fmt.size(total) + " can be freed",
                           "suggestions view: headline \"\(w.pSuggest.headline.text)\"")
                self.check(w.pSuggest.caption.text.hasPrefix("Checked: 3 items, ") && !w.pSuggest.caption.text.contains("can be freed"),
                           "suggestions view: caption \"\(w.pSuggest.caption.text)\"")
                self.check(Suggestion.checked(list).count == 3 && w.btnSugDelete.isEnabled, "suggestions view: Select all checks all 3 items")
                w.checkAllSuggestions(false)
                self.check(Suggestion.checked(list).isEmpty && !w.btnSugDelete.isEnabled,
                           "suggestions view: Deselect all unchecks everything; the delete buttons are disabled")
                fake.expanded = true
                w.showSuggestions()
                let pg = w.pSuggest!
                var clicked = false
                if let row = pg.items.firstIndex(where: { $0.groupOf != nil && $0.name == "f1.bin" }),
                   let ci = pg.table.tableColumns.firstIndex(where: { $0.identifier.rawValue == "c0" }) {
                    let it = pg.items[row]
                    let pt = NSPoint(x: pg.table.rect(ofColumn: ci).minX + ListCell.checkX(it) + 7, y: pg.table.rect(ofRow: row).midY)
                    clicked = w.suggestClick(row, pt)
                }
                let head = pg.items.first { $0.suggestion === fake && $0.groupOf == nil }
                self.check(clicked && fake.checkState == 2 && head?.check == 2 && Suggestion.checked(list).count == 1 && w.btnSugDelete.isEnabled,
                           "suggestions view: clicking a child's checkbox checks it; its head shows mixed")
                w.deleteCheckedSuggestions(trash: false) { _ in
                    // Checked when the delete is done, before the suggestions are found again in the background.
                    self.check(!PathUtil.exists(self.P("F/f1.bin")) && PathUtil.exists(self.P("F/f2.bin")) && PathUtil.exists(self.P("P/node_modules")),
                               "suggestions view: Delete permanently deleted only the checked item")
                    let fake = w.suggestions?.first { $0.name == "Old test files" }
                    self.check(fake != nil && fake!.targets.count == 1 && fake!.size == 5000 && fake!.checkState == 0,
                               "suggestions view: re-measured after the delete (\(fake?.targets.count ?? -1) items, \(fake?.size ?? -1) bytes left)")
                    self.check(w.suggestFinding, "suggestions: marked out of date by the delete and found again (open view)")
                    next()
                }
            })
        }
        // Found again after the delete: the user's choices carry over (node_modules was unchecked by Deselect all).
        steps.waitUntil(90) { !w.suggestFinding }
        steps.then {
            let nm = w.suggestions?.first { $0.name == "node_modules folders" }
            self.check(nm != nil && nm!.targets.count == 1 && nm!.checkState == 0 && w.suggestions?.contains(where: { $0.name == "Old test files" }) == false,
                       "suggestions: found again after the delete, the unchecked node_modules stays unchecked")
        }
        // Deleted outside the app: live updates drop the folder and the open Suggestions view finds them again.
        steps.wait(3.5)
        steps.then { try? FileManager.default.removeItem(atPath: self.P("P/node_modules")) }
        steps.waitUntil(20) { self.find("P/node_modules") == nil }
        steps.waitUntil(60) {
            !w.suggestDirty && !w.suggestFinding && w.suggestions?.contains(where: { $0.name == "node_modules folders" }) == false
        }
        steps.then {
            let stillListed = w.pSuggest.items.contains { $0.suggestion?.name == "node_modules folders" }
            self.check(self.find("P/node_modules") == nil && w.suggestions != nil
                       && w.suggestions?.contains(where: { $0.name == "node_modules folders" }) == false && !stillListed,
                       "suggestions: refreshed after an outside delete (node_modules of P gone)")
        }
        // Tooltips: the full name, then the full path (a folder's own path, a file's folder).
        steps.then {
            guard let c = self.find("C"), let f = Scanner.listFiles(c).first(where: { $0.name == "dup1.bin" }) else {
                self.check(false, "tooltip: C or dup1.bin not in the scan"); return
            }
            let ft = ItemInfo.forFile(f, 0).toolTip.components(separatedBy: "\n")
            let dt = ItemInfo.forDir(c, 0).toolTip.components(separatedBy: "\n")
            let tt = (w.treeToolTip(c) ?? "").components(separatedBy: "\n")
            self.check(ft.count == 2 && ft[0] == "dup1.bin" && PathUtil.same(ft[1], self.P("C")),
                       "tooltip: file row shows name and folder (\(ft.joined(separator: " | ")))")
            self.check(dt.count == 2 && dt[0] == "C" && PathUtil.same(dt[1], self.P("C")) && tt == dt,
                       "tooltip: folder row and tree node show name and path (\(dt.joined(separator: " | ")))")
            // A suggestion with several locations lists every path (at most 8, then how many more).
            func multi(_ n: Int) -> [String] {
                let s = Suggestion("Multi", safe: true, note: "")
                s.targets = (1...n).map { DeleteTarget(path: self.P("M/m\($0)"), label: "m\($0)", isDir: true, size: 1) }
                s.size = Int64(n)
                let it = ItemInfo()
                it.suggestion = s
                return it.toolTip.components(separatedBy: "\n")
            }
            let m3 = multi(3), m10 = multi(10)
            self.check(m3 == ["Multi", self.P("M/m1"), self.P("M/m2"), self.P("M/m3")],
                       "tooltip: a suggestion in 3 places lists the 3 paths (\(m3.joined(separator: " | ")))")
            self.check(m10.count == 10 && m10[1] == self.P("M/m1") && m10[8] == self.P("M/m8") && m10[9] == "... and 2 more",
                       "tooltip: a suggestion in 10 places lists 8 paths and \"... and 2 more\" (\(m10.count) lines, last \"\(m10.last ?? "")\")")
            if let i = w.pages.firstIndex(where: { $0 === w.pContents }) { w.viewTabs.selectedIndex = i }
            if let r = self.root { w.selectDir(r) }
            self.check(w.pContents.table.tipFor != nil && w.pContents.table.view(atColumn: 0, row: 0, makeIfNecessary: true)?.toolTip == nil,
                       "tooltip: list cells have no system tooltip of their own (the hover tip shows instead)")
        }
        // The hover tip: shown about 0.45 s after the mouse rests on a row, next to the cursor, replaced on another row.
        var hoverPoint = NSPoint.zero
        steps.then {
            HoverTip.shared.reset()
            w.pContents.table.simulateMouseMove(row: 0)
            hoverPoint = HoverTip.shared.point
        }
        steps.wait(0.15)
        steps.then { self.check(!HoverTip.shared.isShown, "hover: no tip right away") }
        steps.wait(0.6)
        steps.then {
            let pc = w.pContents!
            let f = HoverTip.shared.tipWindow?.frame ?? .zero
            self.check(pc.items.count >= 2 && HoverTip.shared.shownText == pc.items[0].toolTip,
                       "hover: tip of row 0 shown (\(HoverTip.shared.shownText?.replacingOccurrences(of: "\n", with: " | ") ?? "none"))")
            self.check(abs(f.minX - (hoverPoint.x + 16)) < 1 && abs(f.maxY - (hoverPoint.y - 20)) < 1,
                       "hover: tip placed 16 px right of and 20 px below the cursor (tip \(f), cursor \(hoverPoint))")
            pc.table.simulateMouseMove(row: 1)
        }
        steps.wait(0.15)
        steps.then { self.check(!HoverTip.shared.isShown, "hover: the old tip goes away on another row") }
        steps.wait(0.6)
        steps.then {
            let pc = w.pContents!
            self.check(pc.items.count >= 2 && HoverTip.shared.shownText == pc.items[1].toolTip,
                       "hover: tip follows to row 1 (\(HoverTip.shared.shownText?.replacingOccurrences(of: "\n", with: " | ") ?? "none"))")
            if let e = NSEvent.enterExitEvent(with: .mouseExited, location: .zero, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                              windowNumber: w.window?.windowNumber ?? 0, context: nil, eventNumber: 0, trackingNumber: 0, userData: nil) {
                pc.table.mouseExited(with: e)
            }
        }
        steps.then {
            self.check(!HoverTip.shared.isShown, "hover: tip hidden when the mouse leaves the list")
            let row = w.outline.row(forItem: self.find("C"))
            w.outline.simulateMouseMove(row: row)
        }
        steps.wait(0.75)
        steps.then {
            let row = w.outline.row(forItem: self.find("C"))
            let want = row >= 0 ? w.outline.item(atRow: row).flatMap { w.treeToolTip($0) } : nil
            self.check(want != nil && HoverTip.shared.shownText == want,
                       "hover: tree node tip shown (\(HoverTip.shared.shownText?.replacingOccurrences(of: "\n", with: " | ") ?? "none"))")
            HoverTip.shared.reset()
        }
        // Apps: a fake app in <scratch>-apps/Applications is listed (a com.apple.* one is not); opening it lists the bundle
        // and its 3 leftovers; one Uninstall moves all 4.
        let appsDir = dir + "-apps/Applications"
        var fakeApp = ""
        steps.then {
            FakeApp.cleanUp()
            try? FileManager.default.removeItem(atPath: self.dir + "-apps")
            fakeApp = FakeApp.create(in: appsDir)
            FakeApp.writeBundle(appsDir + "/Apple Fake.app", id: "com.apple.dsafake", name: "Apple Fake", version: "1.0", bytes: 1000)
            w.appRoots = [appsDir]
            w.apps = nil
            w.pApps.stale = true
            w.viewTabs.selectedIndex = w.pages.firstIndex { $0 === w.pApps } ?? 2
        }
        steps.waitUntil(60) { w.apps != nil && !w.appsLoading }
        steps.then {
            let list = w.apps ?? []
            let a = list.first { $0.bundleId == FakeApp.bundleId }
            let measured = Scanner().run(fakeApp).size
            self.check(list.count == 1 && a != nil && a!.name == FakeApp.name && a!.version == "1.0" && a!.publisher == "Example"
                       && a!.size == measured && a!.size >= 50 * 1024 && PathUtil.same(a!.path, fakeApp),
                       "apps: \(FakeApp.name) listed (\(list.count) apps; \"\(a?.name ?? "")\" \(a?.version ?? "?") by \(a?.publisher ?? "?"), \(a?.size ?? -1) bytes, measured \(measured))")
            self.check(!list.contains { $0.bundleId.lowercased().hasPrefix("com.apple.") }, "apps: the com.apple.* bundle is not listed")
            self.check(w.pApps.caption.text.hasPrefix("1 app, ") && w.pApps.items.count == 1, "apps: caption \"\(w.pApps.caption.text)\"")
            let tip = w.pApps.items.first?.toolTip.components(separatedBy: "\n") ?? []
            self.check(tip == [FakeApp.name, fakeApp, FakeApp.bundleId], "apps: hover tip \(tip.joined(separator: " | "))")
            w.pApps.select(IndexSet())
            self.check(!w.btnUninstall.isEnabled && !w.btnRemoveLeftovers.isEnabled, "apps: Uninstall and Remove leftovers disabled without a selection or checks")
            w.pApps.select(IndexSet(integer: 0))
            self.check(w.btnUninstall.isEnabled && w.selectedApp === a, "apps: Uninstall enabled for the selected app")
        }
        steps.waitUntil(20) { w.pApps.items.first?.icon != nil }
        steps.then {
            let head = w.pApps.items.first { $0.app?.bundleId == FakeApp.bundleId && $0.groupOf == nil }
            let s = head?.icon?.size ?? .zero
            self.check(head?.icon != nil && head?.icon === head?.app?.icon && s == InstalledApp.iconSize,
                       "apps: the app row shows the app's own icon (\(Int(s.width))x\(Int(s.height)) pt), not the folder icon")
        }
        var expected: Int64 = 0
        steps.then {
            // Expanding the app lists the bundle and its 3 leftovers; nothing changes on disk.
            if let head = w.pApps.items.first(where: { $0.app != nil && $0.groupOf == nil }) { w.expandApps([head], true) }
        }
        steps.waitUntil(30) { w.apps?.first?.leftovers != nil }
        steps.then {
            let a = w.apps?.first { $0.bundleId == FakeApp.bundleId }
            let items = a?.leftovers ?? []
            let names = items.map { $0.kind + ": " + PathUtil.name($0.path) }
            let paths = items.map { $0.path.lowercased() }
            self.check(items.count == 4 && items[0].kind == "App" && PathUtil.same(items[0].path, fakeApp)
                       && Set(paths.dropFirst()) == Set(FakeApp.leftoverPaths.map { $0.lowercased() })
                       && Set(items.dropFirst().map { $0.kind }) == ["Support files", "Cache", "Settings"],
                       "apps: expanding lists 4 items (\(names.joined(separator: ", ")))")
            self.check(PathUtil.exists(fakeApp) && FakeApp.leftoverPaths.allSatisfy { PathUtil.exists($0) } && a?.uninstalled == false,
                       "apps: expanding changes nothing on disk")
            let kids = w.pApps.items.filter { $0.app === a && $0.groupOf != nil }
            let head = w.pApps.items.first { $0.app === a && $0.groupOf == nil }
            expected = items.reduce(Int64(0)) { $0 + max(0, $1.size) }
            self.check(kids.count == 4 && kids.allSatisfy { $0.check == -1 && $0.cells[2].isEmpty } && head?.check == -1 && head?.size == a?.size
                       && (head?.toolTip.hasSuffix("\nAll its files: " + Fmt.size(expected)) ?? false),
                       "apps: plain child rows without checkboxes; parent keeps the bundle size; tip \"\(head?.toolTip.components(separatedBy: "\n").last ?? "")\"")
            if let h = head, let i = w.pApps.items.firstIndex(where: { $0 === h }) { w.pApps.select(IndexSet(integer: i)) }
        }
        steps.add { next in
            w.uninstallSelectedApp { ok in
                let left = ([fakeApp] + FakeApp.leftoverPaths).filter { PathUtil.exists($0) }
                self.check(ok && left.isEmpty, (ci ? "Trash" : "permanent delete") + ": one Uninstall removed the bundle and the 3 leftovers (\(left.count) left)")
                let want = "\(FakeApp.name) and all its files were moved to the Trash (\(Fmt.size(expected)))."
                self.check(w.apps?.contains { $0.bundleId == FakeApp.bundleId } == false && w.pApps.items.isEmpty && w.statusLabel.stringValue == want,
                           "apps: row gone; status \"\(w.statusLabel.stringValue)\"")
                next()
            }
        }
        steps.then {
            FakeApp.cleanUp()
            try? FileManager.default.removeItem(atPath: self.dir + "-apps")
            w.appRoots = nil
            w.apps = nil
            self.check(!PathUtil.exists(self.dir + "-apps") && FakeApp.leftoverPaths.allSatisfy { !PathUtil.exists($0) }, "apps: test files cleaned up")
        }
        steps.run {
            try? self.log.write(toFile: self.dir + ".log", atomically: true, encoding: .utf8)
            print(self.log, terminator: "")
            fflush(stdout)
            w.selfTestFailed = self.failed
            exit(self.failed ? 1 : 0)
        }
    }
}

/// Screenshot mode (--cli screenshot): after the first scan every view is opened and saved as a PNG, then size on disk,
/// the chart hover, About and the update window; then the app quits. Views render themselves into bitmaps, so no
/// screen-recording permission is needed.
final class ShotRun {
    let app: AppDelegate
    let dir: String
    let steps = Steps()
    init(_ app: AppDelegate, _ dir: String) { self.app = app; self.dir = dir }

    func save(_ view: NSView, _ name: String) {
        let path = dir + "/" + name + ".png"
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
        print("saved \(path)")
    }

    func shot(_ name: String) {
        guard let v = app.main.window?.contentView else { return }
        save(v, name)
        // The chart's slices in drawing order (clockwise from 12 o'clock), so CI logs show what each picture contains.
        for s in app.main.chart.slices {
            let colour = s.color == T.CHART_OTHER ? "grey" : s.empty ? "empty" : MainWindowController.hex(s.color)
            print("  slice \(colour)  \(Fmt.size(s.size))  \(s.rows.count) rows  \(s.label)")
        }
    }

    /// The window with the hover tip drawn where it shows on screen (the tip is a window of its own).
    func shotWithTip(_ name: String) {
        guard let win = app.main.window, let v = win.contentView, let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) else { return }
        v.cacheDisplay(in: v.bounds, to: rep)
        let size = v.bounds.size
        guard let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: rep.pixelsWide, pixelsHigh: rep.pixelsHigh, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
              let ctx = NSGraphicsContext(bitmapImageRep: out) else { return }
        out.size = size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        _ = rep.draw(in: NSRect(origin: .zero, size: size))
        if HoverTip.shared.isShown, let tw = HoverTip.shared.tipWindow, let tv = tw.contentView,
           let trep = tv.bitmapImageRepForCachingDisplay(in: tv.bounds) {
            tv.cacheDisplay(in: tv.bounds, to: trep)
            let cr = win.contentRect(forFrameRect: win.frame), tf = tw.frame
            _ = trep.draw(in: NSRect(x: tf.minX - cr.minX, y: tf.minY - cr.minY, width: tf.width, height: tf.height), from: .zero,
                          operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        }
        NSGraphicsContext.restoreGraphicsState()
        let path = dir + "/" + name + ".png"
        try? out.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
        print("saved \(path)  hover tip: " + (HoverTip.shared.shownText?.replacingOccurrences(of: "\n", with: " | ") ?? "NOT SHOWN"))
    }

    /// The mouse comes to rest on a row (a synthetic mouse move; the tip appears after its delay).
    func hover(_ t: NSTableView, _ row: Int) {
        HoverTip.shared.reset()
        t.simulateMouseMove(row: row)
    }

    func start() {
        let w = app.main!
        let contentsIndex = w.pages.firstIndex { $0 === w.pContents } ?? 1
        // The app opens on Contents; the first shots show Suggestions.
        let suggestIndex = w.pages.firstIndex { $0 === w.pSuggest } ?? 1
        steps.waitUntil(90) { w.suggestions != nil && !w.suggestFinding }
        steps.then { w.viewTabs.selectedIndex = suggestIndex }
        steps.wait(1.5)
        steps.then { self.shot("0-suggestions-start") }
        var saved: [Suggestion]?
        steps.then {
            // Hover a suggestion with several locations: its tip lists every path.
            var row = w.pSuggest.items.firstIndex { $0.groupOf == nil && ($0.suggestion?.targets.count ?? 0) > 1 }
            if row == nil {
                // Nothing on this Mac has several locations: a sample row to show how such a tip looks.
                saved = w.suggestions ?? []
                let home = PathUtil.home
                let s = Suggestion("Sample: caches in several places", safe: true, note: "Screenshot sample")
                s.targets = ["/Library/Caches", home + "/Library/Caches", home + "/Library/Logs", "/private/var/log"].map {
                    DeleteTarget(path: $0, label: PathUtil.name($0), isDir: true, size: 1 << 20)
                }
                s.size = 4 << 20
                s.location = "4 locations"
                s.checkAll(true)
                w.suggestions = (w.suggestions ?? []) + [s]
                w.showSuggestions()
                row = w.pSuggest.items.firstIndex { $0.suggestion === s }
            }
            if let r = row { self.hover(w.pSuggest.table, r) }
        }
        steps.wait(0.9)
        steps.then {
            self.shotWithTip("0b-hover-suggestion")
            HoverTip.shared.reset()
            if let s = saved { w.suggestions = s; w.showSuggestions() }
        }
        steps.then { w.viewTabs.selectedIndex = contentsIndex }
        steps.wait(1.5)
        steps.then { self.shot("1-contents") }
        steps.then {
            print("panes: tree \(Int(w.treePanel.frame.width)), list \(Int(w.listPanel.frame.width)), chart \(Int(w.chartPanel.frame.width)) x \(Int(w.chartPanel.frame.height)), window \(Int(w.window?.frame.width ?? 0)) x \(Int(w.window?.frame.height ?? 0))")
            // Hover a tree node under the selected one.
            self.hover(w.outline, min(w.outline.numberOfRows - 1, max(0, w.outline.selectedRow + 1)))
        }
        steps.wait(0.9)
        steps.then { self.shotWithTip("1b-hover-tree"); HoverTip.shared.reset() }
        var n = 2
        for (i, p) in w.pages.enumerated() where p !== w.pContents {
            let num = n
            n += 1
            if p === w.pBasket {
                steps.then {
                    for it in w.pFiles.items.filter({ $0.canDelete }).prefix(4) { for t in DeleteTarget.from(it) { w.addTarget(t) } }
                    w.basketChanged()
                }
            }
            if p === w.pChanges {
                // A second scan, so the Changes view has an earlier scan to compare with.
                steps.then { if let r = w.active, r.previous == nil { w.scanPath(r.path) } }
                steps.waitUntil(90) { !w.scanning && (w.active?.previous != nil) }
            }
            if p === w.pDupes { steps.then { w.dupeMin.selectedIndex = 2 } }
            steps.then { w.viewTabs.selectedIndex = i }
            if p === w.pDupes {
                steps.then { w.findDuplicates() }
                steps.waitUntil(100) { w.dupeFinder == nil }
            }
            if p === w.pSuggest {
                steps.waitUntil(90) { w.suggestions != nil && !w.suggestFinding }
                steps.then {
                    // One suggestion opened (the largest of those with the most items), with one of its items unchecked
                    // (a mixed check) when it has several, and the smallest suggestion unchecked, to show the check states.
                    let shown = w.shownSuggestions.sorted { $0.size > $1.size }
                    let most = shown.map { $0.targets.count }.max() ?? 0
                    if let s = shown.first(where: { $0.targets.count == most }) {
                        s.expanded = true
                        if s.targets.count > 1 { s.setChecked(s.targets.count - 1, false) }
                    }
                    if shown.count > 2, let last = shown.last { last.checkAll(false) }
                    w.showSuggestions()
                }
            }
            if p === w.pFiles || p === w.pTypes { steps.waitUntil(20) { w.readers == 0 } }
            if p === w.pApps {
                // The largest app opened, showing all its files.
                steps.waitUntil(180) { w.apps != nil && !w.appsLoading }
                steps.then { if let h = w.pApps.items.first(where: { $0.app != nil && $0.groupOf == nil }) { w.expandApps([h], true) } }
                steps.waitUntil(60) { w.apps?.first.map { $0.leftovers != nil } ?? true }
            }
            steps.wait(2)
            steps.then { self.shot(p === w.pApps ? "apps" : "\(num)-" + (p.title.split(separator: " ").first.map { String($0).lowercased() } ?? "view")) }
            if p === w.pApps {
                // The fake app opened before uninstalling: the bundle and its leftovers (cleaned up afterwards).
                let fakeRoot = dir + "/fake-apps/Applications"
                steps.add { next in
                    FakeApp.cleanUp()
                    try? FileManager.default.removeItem(atPath: self.dir + "/fake-apps")
                    _ = FakeApp.create(in: fakeRoot)
                    w.appRoots = InstalledApp.defaultRoots + [fakeRoot]
                    w.loadApps {
                        guard let a = w.apps?.first(where: { $0.bundleId == FakeApp.bundleId }),
                              let h = w.pApps.items.first(where: { $0.app === a && $0.groupOf == nil }) else { print("apps: fake app not listed"); next(); return }
                        w.expandApps([h], true)
                        w.loadAppItems(a) {
                            w.selectApp(a)
                            print("apps: fake app expanded, \(a.leftovers?.count ?? -1) items")
                            next()
                        }
                    }
                }
                steps.wait(1.5)
                steps.then { self.shot("apps-expanded") }
                steps.then {
                    FakeApp.cleanUp()
                    try? FileManager.default.removeItem(atPath: self.dir + "/fake-apps")
                    w.appRoots = nil
                    w.apps?.removeAll { $0.bundleId == FakeApp.bundleId }
                    w.showApps()
                }
            }
            if p === w.pFiles {
                // Hover a file: its tip shows the full name and the folder.
                steps.then { self.hover(w.pFiles.table, w.pFiles.items.firstIndex { $0.file != nil } ?? 0) }
                steps.wait(0.9)
                steps.then { self.shotWithTip("\(num)b-hover-file"); HoverTip.shared.reset() }
            }
        }
        steps.then { w.viewTabs.selectedIndex = contentsIndex; w.measure.selectedIndex = 0 }
        steps.wait(1.5)
        steps.then { self.shot("\(w.pages.count + 1)-file-size"); w.measure.selectedIndex = 1 }
        steps.then {
            // Hover the first chart slice to show the linked highlight in the list.
            if !w.chart.slices.isEmpty { w.chart.onHover?(0); w.chart.setHover(0) }
        }
        steps.wait(0.4)
        steps.then { self.shot("\(w.pages.count + 2)-hover"); self.app.showAbout(nil) }
        steps.wait(0.6)
        steps.then {
            if let v = self.app.about?.window?.contentView { self.save(v, "\(w.pages.count + 3)-about") }
            self.app.about?.close()
            w.updates.check(manual: true)
        }
        steps.wait(6)
        steps.then {
            if let win = NSApp.windows.first(where: { $0.title == "Disk Space Analyzer - updates" }), let v = win.contentView { self.save(v, "\(w.pages.count + 4)-update") }
            let d = FullDiskAccessWindow(onDone: {})
            self.fda = d
            d.present(over: w.window)
        }
        steps.wait(0.6)
        steps.then {
            if let v = self.fda?.window?.contentView { self.save(v, "\(w.pages.count + 5)-full-disk-access") }
            self.fda?.close()
        }
        // Deleting: the themed confirmation, the delete strip mid-delete (rows leaving the Delete list), the error report.
        let demo = dir + "/delete-demo"
        steps.then {
            let fm = FileManager.default
            try? fm.removeItem(atPath: demo)
            w.basket.removeAll()
            for i in 1...6 {
                let f = demo + "/project-\(i)/build-cache"
                try? fm.createDirectory(atPath: f, withIntermediateDirectories: true)
                for k in 1...40 { fm.createFile(atPath: f + "/chunk-\(k).bin", contents: Data(count: 4096 * i)) }
                w.addTarget(DeleteTarget(path: f, label: "project-\(i)/build-cache", isDir: true, size: Scanner().run(f).size))
            }
            w.basketChanged()
            w.viewTabs.selectedIndex = w.pages.firstIndex { $0 === w.pBasket } ?? 0
        }
        steps.wait(1)
        steps.then { DeleteJob.throttle = 20_000; w.deleteBasket(trash: false) }
        steps.wait(1)
        steps.then {
            if let d = PromptDialog.open.last, let v = d.window?.contentView {
                self.save(v, "delete-confirm")
                print("delete-confirm: \"\(d.heading)\" \(d.detailLines.count) lines, buttons \(d.buttonTitles)")
                d.finish(0)
            } else { print("delete-confirm: no prompt shown") }
        }
        steps.wait(2.5)
        steps.then {
            self.shot("deleting")
            print("deleting: \(w.deleteBar.status) | \(w.deleteBar.detail) | bar \(w.deleteBar.fraction) | \(w.pBasket.items.count) rows left")
        }
        steps.waitUntil(60) { w.deleteJob == nil && !w.busy }
        steps.then {
            DeleteJob.throttle = 0
            // A folder whose files cannot be deleted (read-only subfolders), for the error report.
            let fm = FileManager.default
            let locked = demo + "/locked"
            for s in ["logs", "data"] {
                try? fm.createDirectory(atPath: locked + "/" + s, withIntermediateDirectories: true)
                for k in 1...4 { fm.createFile(atPath: locked + "/\(s)/file-\(k).bin", contents: Data(count: 2048)) }
                chmod(locked + "/" + s, 0o555)
            }
            w.basket.removeAll()
            w.addTarget(DeleteTarget(path: locked, label: "locked", isDir: true, size: Scanner().run(locked).size))
            w.basketChanged()
            w.deleteBasket(trash: false)
        }
        steps.wait(1)
        steps.then { PromptDialog.open.last?.finish(0) }
        steps.waitUntil(30) { w.deleteJob == nil && !PromptDialog.open.isEmpty }
        steps.wait(1)
        steps.then {
            if let d = PromptDialog.open.last, let v = d.window?.contentView {
                self.save(v, "delete-errors")
                print("delete-errors: \"\(d.heading)\" \(d.detailLines.count) lines")
                d.finish(0)
            } else { print("delete-errors: no prompt shown") }
            for s in ["logs", "data"] { chmod(demo + "/locked/" + s, 0o755) }
            try? FileManager.default.removeItem(atPath: demo)
        }
        steps.run { exit(0) }
    }
    private var fda: FullDiskAccessWindow?
}
