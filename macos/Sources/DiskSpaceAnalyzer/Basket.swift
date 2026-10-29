import AppKit

/// Delete list: items collected from any view, reviewed together, then moved to the Trash or deleted permanently. Also
/// the shared deletion flow (confirmation, progress, updating the scan afterwards).
extension MainWindowController {
    func buildBasketPage() -> Page {
        btnBasketRemove.tip = "Take the selected items off the list (they stay on the disk)"
        btnBasketClear.tip = "Empty the delete list (nothing is deleted)"
        btnBasketTrash.tip = "Move everything on the list to the Trash: restorable; the space is freed when the Trash is emptied"
        btnBasketDelete.tip = "Delete everything on the list now, bypassing the Trash"
        let p = Page("Delete list", [Column("name", "Name", 200), Column("size", "Size", 80, true), Column("size", "% of list", 125, true),
                                     Column("folder", "Location", 330), Column("note", "Note", 200)],
                     barColumn: 2, bar: ActionBar([btnBasketRemove, btnBasketClear, btnBasketTrash, btnBasketDelete], right: [btnBasketTrash, btnBasketDelete]))
        p.chartTitle = "Delete list"
        p.empty = "Nothing on the delete list."
        p.fill = { [unowned self] in self.fillBasket() }
        return p
    }

    func fillBasket() {
        let p = pBasket!
        let total = basket.reduce(Int64(0)) { $0 + $1.size }
        var rows: [ItemInfo] = []
        for t in basket {
            let it = ItemInfo()
            it.targets = [t]; it.name = t.label; it.size = t.size; it.folderIcon = t.isDir
            it.path = t.emptyTrash ? nil : t.path
            it.note = t.note
            it.percent = total > 0 ? 100 * Double(t.size) / Double(total) : 0
            it.folder = t.contentsOnly ? t.path : PathUtil.parent(t.path)
            it.cells = [it.name, Fmt.size(it.size), Fmt.pct(it.percent), it.folder, it.note]
            rows.append(it)
        }
        p.setRows(rows, "The delete list is empty.\nSelect files or folders in any view and press Add to delete list (⌘D), then review and delete them here.")
        p.setCaption(basket.isEmpty ? "Collect items here from any view, review them, then delete them together."
            : "\(Fmt.count(basket.count)) item\(basket.count == 1 ? "" : "s"), \(Fmt.size(total)).  Move to Trash can be undone (the space is freed when the Trash is emptied); Delete permanently frees it now.")
        p.chartSub = basket.isEmpty ? "" : Fmt.size(total) + " to free"
        p.chartTotal = 0
        updateButtons()
        if activePage === p { refreshChart() }
    }

    func updateBasketTitle() {
        guard let i = pages.firstIndex(where: { $0 === pBasket }) else { return }
        viewTabs.setTitle(i, basket.isEmpty ? "Delete list" : "Delete list (\(Fmt.count(basket.count)))")
    }

    func basketChanged() {
        updateBasketTitle()
        setStale(pBasket)
        updateButtons()
    }

    func addSelectionToBasket() {
        if busy || lastView === pBasket.table { return }
        let sel = selection().filter { $0.canDelete }
        if sel.isEmpty { return }
        var added = 0
        for it in sel { for t in DeleteTarget.from(it) where addTarget(t) { added += 1 } }
        basketChanged()
        let total = basket.reduce(Int64(0)) { $0 + $1.size }
        statusLabel.stringValue = added == 0 ? "Already on the delete list."
            : "Added \(Fmt.count(added)) item\(added == 1 ? "" : "s") to the delete list: now \(Fmt.count(basket.count)) item\(basket.count == 1 ? "" : "s"), \(Fmt.size(total)). Open the Delete list tab to review and delete them."
    }

    /// Adds a target unless it is already covered; drops entries that the new folder covers.
    @discardableResult
    func addTarget(_ t: DeleteTarget) -> Bool {
        if t.emptyTrash {
            if basket.contains(where: { $0.emptyTrash }) { return false }
            basket.append(t)
            return true
        }
        for b in basket where !b.emptyTrash {
            if PathUtil.same(b.path, t.path) && b.contentsOnly == t.contentsOnly { return false }
            if b.isDir && !PathUtil.same(b.path, t.path) && PathUtil.isWithin(t.path, b.path) { return false }   // inside a folder already listed
            if b.isDir && !b.contentsOnly && PathUtil.same(b.path, t.path) { return false }                       // the whole folder is listed
        }
        if t.isDir { basket.removeAll { !$0.emptyTrash && !PathUtil.same($0.path, t.path) && PathUtil.isWithin($0.path, t.path) } }
        if t.isDir && !t.contentsOnly { basket.removeAll { !$0.emptyTrash && PathUtil.same($0.path, t.path) && $0.contentsOnly } }
        basket.append(t)
        return true
    }

    func removeFromBasket() {
        let sel = pBasket.selectedItems.compactMap { $0.targets }.flatMap { $0 }
        if sel.isEmpty { return }
        basket.removeAll { b in sel.contains { $0 === b } }
        basketChanged()
    }

    func deleteBasket(trash: Bool) {
        if busy || scanning || basket.isEmpty { return }
        confirmAndDelete(basket, trash: trash) { [weak self] job in
            guard let self = self, job != nil else { return }
            self.basketChanged()
        }
    }

    func deleteSelected() { deleteSelection(trash: false) }
    func trashSelected() { deleteSelection(trash: true) }

    func deleteSelection(trash: Bool) {
        if busy || scanning { return }
        let sel = selection().filter { $0.canDelete }
        if sel.isEmpty { return }
        let targets = sel.flatMap { DeleteTarget.from($0) }
        confirmAndDelete(targets, trash: trash) { [weak self] job in
            guard let self = self, job != nil else { return }
            self.basketChanged()
        }
    }

    /// After a delete or a move to the Trash: every target still on the delete list and in the suggestions gets the
    /// size of what is left (from the updated index, or measured just now); targets that are gone, or folders whose
    /// contents were to go and are now empty, are dropped.
    func remeasureAfterDelete(_ done: [DeleteTarget], _ job: DeleteJob, _ measured: [String: Int64]) {
        let failed = job.failedPaths
        basket.removeAll { !remeasure($0, done, failed, measured) }
        if let list = suggestions {
            for s in list {
                var keep: [DeleteTarget] = [], checks: [Bool] = []
                for (i, t) in s.targets.enumerated() where remeasure(t, done, failed, measured) { keep.append(t); checks.append(s.isChecked(i)) }
                if keep.count != s.targets.count && !keep.isEmpty {
                    s.location = keep.count == 1 ? keep[0].path : "\(Fmt.count(keep.count)) locations"
                }
                s.targets = keep
                s.checked = checks
                s.size = keep.reduce(Int64(0)) { $0 + $1.size }
            }
            suggestions = list.filter { !$0.targets.isEmpty && $0.size > 0 }
        }
        updateBasketTitle()
    }

    /// Updates a target's size (and note) to what is left of it; false when nothing is left.
    func remeasure(_ t: DeleteTarget, _ done: [DeleteTarget], _ failed: [String], _ measured: [String: Int64]) -> Bool {
        if !PathUtil.exists(t.path) { return false }
        let touched = done.contains { PathUtil.isWithin(t.path, $0.path) || PathUtil.isWithin($0.path, t.path) }
        let left = failed.filter { PathUtil.isWithin($0, t.path) }.count
        if t.contentsOnly || t.emptyTrash {
            if let names = try? FileManager.default.contentsOfDirectory(atPath: t.path) {
                if !names.contains(where: { !t.exclude.contains($0) }) { return false }
            } else if touched && left == 0 { return false }
        }
        if t.isDir || t.emptyTrash {
            if let n = findScanned(t.path, exact: true), !n.insideSkipped { t.size = MainWindowController.sizeExcluding(n, t.exclude) }
            else if let m = measured[PathUtil.trim(t.path)] { t.size = m }
        } else {
            var st = stat()
            if lstat(t.path, &st) == 0 { t.size = DirNode.onDisk ? Int64(st.st_blocks) * 512 : Int64(st.st_size) }
        }
        if touched && left > 0 { t.note = "partly deleted -- \(Fmt.count(Int64(left))) item\(left == 1 ? "" : "s") in use or denied" }
        return true
    }

    /// Size of a folder without the named items directly inside it (kept when only the contents are deleted).
    static func sizeExcluding(_ n: DirNode, _ exclude: Set<String>) -> Int64 {
        var size = n.size
        for name in exclude { if let c = n.child(name) { size -= c.size } }
        return max(0, size)
    }

    static let riskyPaths = ["/System", "/Library", "/Applications", "/usr", "/bin", "/sbin", "/private", "/opt"]

    /// Asks, deletes (or moves to the Trash) with the busy overlay, then updates the scan. done(nil) when cancelled.
    func confirmAndDelete(_ targets: [DeleteTarget], trash: Bool, _ done: @escaping (DeleteJob?) -> Void) {
        let scannedRoots = roots.compactMap { $0.node?.fullPath }
        if targets.contains(where: { t in !t.emptyTrash && !t.contentsOnly && scannedRoots.contains { PathUtil.same($0, t.path) } }) {
            alert("A scanned drive or folder itself cannot be deleted here.\nSelect items inside it instead.")
            done(nil)
            return
        }
        let total = targets.reduce(Int64(0)) { $0 + $1.size }
        var list = targets.prefix(12).map { t -> String in
            "    " + (t.emptyTrash ? "Empty the Trash" : Fmt.shorten(t.path, 90) + (t.contentsOnly ? "  (contents)" : "")) + "   (" + Fmt.size(t.size) + ")"
        }.joined(separator: "\n")
        if targets.count > 12 { list += "\n    ... and \(Fmt.count(targets.count - 12)) more" }
        let n = targets.count, s = n == 1 ? "" : "s"
        let head = trash ? "Move \(n) item\(s) (\(Fmt.size(total))) to the Trash?" : "Permanently delete \(n) item\(s) (\(Fmt.size(total)))?"
        var msg = list + (trash ? "\n\nYou can put them back from the Trash. The space is freed when the Trash is emptied."
                                : "\n\nThis bypasses the Trash and CANNOT be undone.")
        if trash && targets.contains(where: { $0.emptyTrash }) { msg += "\n\nThe Trash itself is emptied (its items are deleted for good)." }
        if targets.contains(where: { t in !t.contentsOnly && !t.emptyTrash && MainWindowController.riskyPaths.contains { PathUtil.isWithin(t.path, $0) } }) {
            msg += "\n\nWARNING: this includes macOS system, library or application content. Deleting it can break macOS or installed apps."
        }
        if selfTestDir != nil { runDelete(targets, trash: trash, done); return }
        let a = NSAlert()
        a.alertStyle = trash ? .informational : .warning
        a.messageText = head
        a.informativeText = msg
        a.addButton(withTitle: "Cancel")
        a.addButton(withTitle: trash ? "Move to Trash" : "Delete permanently")
        if #available(macOS 11.0, *) { a.buttons[1].hasDestructiveAction = !trash }
        a.beginSheetModal(for: window!) { resp in
            if resp == .alertSecondButtonReturn { self.runDelete(targets, trash: trash, done) } else { done(nil) }
        }
    }

    private func attachedDir(_ t: DeleteTarget) -> DirNode? {
        if let d = t.dir, isAttached(d) { return d }
        return t.isDir ? findScanned(t.path, exact: true) : nil
    }

    private func runDelete(_ targets: [DeleteTarget], trash: Bool, _ done: @escaping (DeleteJob?) -> Void) {
        var target = current
        for t in targets where !t.contentsOnly && !t.emptyTrash {
            if let dn = attachedDir(t), let c = current, c.isUnder(dn) { target = dn.parent }
        }
        let dirTargets: [(DeleteTarget, DirNode?)] = targets.filter { $0.isDir }.map { ($0, attachedDir($0)) }
        let fileDirs: [(DeleteTarget, DirNode?)] = targets.filter { !$0.isDir }.map { t -> (DeleteTarget, DirNode?) in
            if let f = t.file, isAttached(f.dir) { return (t, f.dir as DirNode?) }
            return (t, findScanned(PathUtil.parent(t.path), exact: true))
        }
        let before = roots.compactMap { $0.node }.reduce(Int64(0)) { $0 + $1.size }
        let job = DeleteJob()
        deleteJob = job
        setBusy(true)
        DispatchQueue.global(qos: .userInitiated).async {
            job.run(targets, trash: trash)
            job.current = "Updating the scan..."
            var fresh: [(DirNode, DirNode)] = []
            var measured: [String: Int64] = [:]       // what is left of folders that were not deleted completely
            for (t, dn) in dirTargets where PathUtil.exists(t.path) {
                let n = Scanner().run(t.path)
                if let dn = dn { fresh.append((dn, n)) }
                measured[PathUtil.trim(t.path)] = MainWindowController.sizeExcluding(n, t.exclude)
            }
            DispatchQueue.main.async {
                self.whenNoReaders {
                    for (t, d) in fileDirs {
                        if let d = d, self.isAttached(d), !PathUtil.exists(t.path) { d.removeFileRec(PathUtil.name(t.path)) }
                    }
                    for (t, dn) in dirTargets {
                        if let dn = dn, self.isAttached(dn), dn.parent != nil, !PathUtil.exists(t.path) { self.removeNode(dn) }
                    }
                    for (old, new) in fresh where self.isAttached(old) {
                        self.replaceNode(old, new)
                        if target === old { target = new }
                    }
                    self.deleteJob = nil
                    self.setBusy(false)
                    self.ignoreOwnChanges()
                    if let t = target, !self.isAttached(t) { target = nil }
                    self.remeasureAfterDelete(targets, job, measured)
                    self.current = nil
                    self.refreshTree(target ?? self.active?.node)
                    self.viewsChanged()
                    self.updateDriveLabel()
                    if let v = self.lastView, !v.isHiddenOrHasHiddenAncestor { self.window?.makeFirstResponder(v) }
                    let after = self.roots.compactMap { $0.node }.reduce(Int64(0)) { $0 + $1.size }
                    let freed = max(0, before - after)
                    let skipped = job.skipped > 0 ? ", \(Fmt.count(job.skipped)) in use were skipped" : ""
                    let errors = job.errors
                    if trash {
                        let moved = job.recycled
                        self.statusLabel.stringValue = "Moved \(Fmt.count(moved)) item\(moved == 1 ? "" : "s") (\(Fmt.size(freed))) to the Trash\(skipped)"
                            + (errors.isEmpty ? "." : ", \(Fmt.count(errors.count)) could not be moved.")
                    } else {
                        self.statusLabel.stringValue = "Freed \(Fmt.size(freed)). \(Fmt.count(job.filesDeleted)) files deleted\(skipped)"
                            + (errors.isEmpty ? "." : ", \(Fmt.count(errors.count)) items could not be deleted.")
                    }
                    if !errors.isEmpty && self.selfTestDir == nil {
                        var err = errors.prefix(20).map { Fmt.shorten($0, 160) }.joined(separator: "\n")
                        if errors.count > 20 { err += "\n... and \(Fmt.count(errors.count - 20)) more" }
                        self.alert("Some items could not be \(trash ? "moved" : "deleted") (in use, protected, or no permission -- Full Disk Access may help):\n\n" + err)
                    }
                    done(job)
                }
            }
        }
    }
}
