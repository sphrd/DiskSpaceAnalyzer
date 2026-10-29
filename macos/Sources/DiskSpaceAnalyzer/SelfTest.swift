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

/// Builds the self-test folder: 9 files in 8 folders, two identical files, a node_modules folder.
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
        steps.then {
            let top = self.root
            self.check(top != nil && top!.files == 9 && top!.dirs == 8, "scan: \(top?.files ?? -1) files, \(top?.dirs ?? -1) folders (expected 9, 8)")
            // iCloud items are never downloaded: the process-wide policy is set at start-up.
            self.check(Dataless.policyResult == 0 && Dataless.processPolicy == Dataless.materializeOff,
                       "iopolicy: setiopolicy_np(materialize dataless files, process, off) returned \(Dataless.policyResult) (errno \(Dataless.policyErrno)), policy now \(Dataless.processPolicy)")
            // Protected folders: listed, not entered (D stands in for one).
            let s = Scanner()
            s.alsoBlocked = [self.P("D")]
            let r = s.run(self.dir)
            let d = r.child("D")
            self.check(d != nil && d!.skipped == .needsAccess && d!.files == 0 && d!.displayName.hasSuffix("[needs Full Disk Access]") && r.files == 8 && r.dirs == 8,
                       "protected folder listed but not entered (\(r.files) files, \(r.dirs) folders; expected 8, 8)")
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
            return a.localFiles == 3 && e.files == 1 && r.files == 11
        }
        steps.then {
            let a = self.find("A"), e = self.find("E/deep")
            self.check(a != nil && a!.localFiles == 3, "live: new file in A noticed")
            self.check(e != nil && e!.files == 1 && self.root?.files == 11, "live: new folder E/deep with its file noticed (total \(self.root?.files ?? -1) files)")
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
                self.check(self.root?.files == 4, "totals after deletes: \(self.root?.files ?? -1) files (expected 4)")
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
                w.viewTabs.selectedIndex = w.pages.firstIndex { $0 === w.pSuggest } ?? 4
                w.showSuggestions()
                w.checkAllSuggestions(true)
                self.check(Suggestion.checked(list).count == 3 && w.btnSugDelete.isEnabled, "suggestions view: Select all checks all 3 items")
                w.checkAllSuggestions(false)
                self.check(Suggestion.checked(list).isEmpty && !w.btnSugDelete.isEnabled && !w.btnSugAdd.isEnabled,
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
                w.deleteCheckedSuggestions(trash: false)
                next()
            })
        }
        steps.waitUntil(20) { !w.busy }
        steps.then {
            self.check(!PathUtil.exists(self.P("F/f1.bin")) && PathUtil.exists(self.P("F/f2.bin")) && PathUtil.exists(self.P("P/node_modules")),
                       "suggestions view: Delete permanently deleted only the checked item")
            let fake = w.suggestions?.first { $0.name == "Old test files" }
            self.check(fake != nil && fake!.targets.count == 1 && fake!.size == 5000 && fake!.checkState == 0,
                       "suggestions view: re-measured after the delete (\(fake?.targets.count ?? -1) items, \(fake?.size ?? -1) bytes left)")
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

    func start() {
        let w = app.main!
        steps.wait(1.5)
        steps.then { self.shot("1-contents") }
        steps.then {
            print("panes: tree \(Int(w.treePanel.frame.width)), list \(Int(w.listPanel.frame.width)), chart \(Int(w.chartPanel.frame.width)) x \(Int(w.chartPanel.frame.height)), window \(Int(w.window?.frame.width ?? 0)) x \(Int(w.window?.frame.height ?? 0))")
        }
        for i in 1..<w.pages.count {
            let p = w.pages[i]
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
                steps.waitUntil(90) { w.suggestions != nil }
                steps.then {
                    // One suggestion opened, with one of its items unchecked (a mixed check), to show the check states.
                    if let s = w.shownSuggestions.max(by: { $0.targets.count < $1.targets.count }) {
                        s.expanded = true
                        if s.targets.count > 1 { s.setChecked(s.targets.count - 1, false) }
                    }
                    w.showSuggestions()
                }
            }
            if p === w.pFiles || p === w.pTypes { steps.waitUntil(20) { w.readers == 0 } }
            steps.wait(2)
            steps.then { self.shot("\(i + 1)-" + (p.title.split(separator: " ").first.map { String($0).lowercased() } ?? "view")) }
        }
        steps.then { w.viewTabs.selectedIndex = 0; w.measure.selectedIndex = 0 }
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
        steps.run { exit(0) }
    }
    private var fda: FullDiskAccessWindow?
}
