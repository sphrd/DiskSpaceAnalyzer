import AppKit

/// One row in a view's list: a folder (dir), a file (file), a cleanup target (targets: a suggestion or a delete-list
/// entry), a group head (duplicates) or a plain row (file types, changes, "N smaller files" summaries).
final class ItemInfo {
    var isDir = false, isSummary = false, isGroup = false, folderIcon = false
    var dir: DirNode?
    var file: FileEntry?
    var targets: [DeleteTarget]?
    var groupOf: ItemInfo?             // member of a group: shares the group's chart slice
    var name = "", folder = "", category = "", note = ""
    var path: String?
    var ext: String?
    var size: Int64 = 0, files: Int64 = 0, dirs: Int64 = 0, modified: Int64 = 0
    var change: Int64 = Int64.min      // compared with the previous scan; min = unknown
    var chartValue: Int64 = -1         // size used by the chart when it differs from size
    var percent: Double = 0
    var cells: [String] = []
    var color: NSColor?
    var check = -1                     // checkbox: -1 none, 0 unchecked, 1 checked, 2 mixed
    var chevron = -1                   // expand chevron: -1 none, 0 collapsed, 1 expanded
    var suggestion: Suggestion?        // suggestions: the suggestion of the row (head or one of its targets)
    var app: InstalledApp?             // apps: the app of the row (head or one of its leftovers)
    var leftover: AppLeftover?         // apps: the leftover of a member row
    var hideBar = false                // no percentage bar (the bar column's cell stays empty)
    var chartSize: Int64 { chartValue >= 0 ? chartValue : size }
    var canDelete: Bool { !isSummary && !isGroup && (isDir || file != nil || targets != nil) }
    var fullPath: String? { isDir ? dir?.fullPath : (file?.fullPath ?? path) }
    var tip: String?                   // tooltip set by the view (file types, summaries, group heads, removed folders)

    /// Shown when hovering the row: the full name on the first line, the full path on the second (a folder's own path,
    /// a file's folder; a suggestion the path of each of its locations, at most maxTipPaths; the Trash "Trash").
    static let maxTipPaths = 8
    var toolTip: String {
        if let t = tip { return t }
        if let s = suggestion, groupOf == nil {
            if s.targets.contains(where: { $0.emptyTrash }) { return s.name + "\n" + "Trash" }
            if s.targets.isEmpty { return s.name + "\n" + s.location }
            let paths = s.targets.map { PathUtil.trim($0.path) }
            let shown = paths.prefix(ItemInfo.maxTipPaths).joined(separator: "\n")
            let more = paths.count - ItemInfo.maxTipPaths
            return s.name + "\n" + shown + (more > 0 ? "\n... and \(Fmt.count(more)) more" : "")
        }
        if let ts = targets, ts.count == 1 {
            let t = ts[0]
            if t.emptyTrash { return t.label + "\n" + "Trash" }
            return ItemInfo.nameOf(t.path) + "\n" + (t.isDir ? PathUtil.trim(t.path) : PathUtil.parent(t.path))
        }
        if let f = file { return f.name + "\n" + f.dir.fullPath }
        if let d = dir { let p = d.fullPath; return ItemInfo.nameOf(p) + "\n" + p }
        if let p = path { return ItemInfo.nameOf(p) + "\n" + (isDir || folderIcon ? PathUtil.trim(p) : PathUtil.parent(p)) }
        return folder.isEmpty ? name : name + "\n" + folder
    }

    static func nameOf(_ p: String) -> String { let n = PathUtil.name(p); return n.isEmpty ? p : n }

    static func forDir(_ d: DirNode, _ parentSize: Int64) -> ItemInfo {
        let it = ItemInfo()
        it.isDir = true; it.dir = d; it.name = d.displayName; it.size = d.size; it.files = d.files; it.dirs = d.dirs; it.modified = d.modified
        it.percent = parentSize > 0 ? 100 * Double(d.size) / Double(parentSize) : 0
        return it
    }

    static func forFile(_ f: FileEntry, _ parentSize: Int64) -> ItemInfo {
        let it = ItemInfo()
        it.file = f; it.name = f.dataless ? f.name + "  (not downloaded)" : f.name; it.size = f.size; it.files = 1; it.modified = f.modified
        it.percent = parentSize > 0 ? 100 * Double(f.size) / Double(parentSize) : 0
        return it
    }

    /// Sort order of the lists (same keys as the Windows app).
    static func compare(_ a: ItemInfo, _ b: ItemInfo, _ key: String, _ ascending: Bool) -> Bool {
        if a.isSummary != b.isSummary { return b.isSummary }
        var r: ComparisonResult
        func cmp<V: Comparable>(_ x: V, _ y: V) -> ComparisonResult { x == y ? .orderedSame : (x < y ? .orderedAscending : .orderedDescending) }
        switch key {
        case "name": r = a.name.localizedStandardCompare(b.name)
        case "folder": r = a.folder.localizedStandardCompare(b.folder)
        case "files": r = cmp(a.files, b.files)
        case "dirs": r = cmp(a.dirs, b.dirs)
        case "mod": r = cmp(a.modified, b.modified)
        case "change": r = cmp(a.change, b.change)
        case "cat": r = a.category.localizedStandardCompare(b.category)
        case "note": r = a.note.localizedStandardCompare(b.note)
        default: r = cmp(a.size, b.size)
        }
        if r == .orderedSame && key != "size" { r = cmp(b.size, a.size) }
        return ascending ? r == .orderedAscending : r == .orderedDescending
    }
}

struct Column {
    let key: String, title: String, width: CGFloat, right: Bool
    init(_ key: String, _ title: String, _ width: CGFloat, _ right: Bool = false) { self.key = key; self.title = title; self.width = width; self.right = right }
}

/// Muted caption text above a list: one line, or two when it needs them (then the last line ends in an ellipsis).
final class CaptionView: NSView {
    var text = "" { didSet { if text != oldValue { needsDisplay = true } } }
    override var isFlipped: Bool { true }
    static let lineH = ceil(Theme.font9.ascender - Theme.font9.descender + Theme.font9.leading)

    private var attributes: [NSAttributedString.Key: Any] {
        let ps = NSMutableParagraphStyle()
        ps.lineBreakMode = .byWordWrapping
        return [.font: Theme.font9, .foregroundColor: T.MUTED, .paragraphStyle: ps]
    }

    func lines(width: CGFloat) -> CGFloat {
        let h = (text as NSString).boundingRect(with: NSSize(width: width, height: 1000), options: [.usesLineFragmentOrigin], attributes: attributes).height
        return h > CaptionView.lineH * 1.5 ? 2 : 1
    }

    override func draw(_ dirtyRect: NSRect) {
        (text as NSString).draw(with: bounds, options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], attributes: attributes)
    }
}

/// Large bold line above a page's caption (the Suggestions total).
final class HeadlineView: NSView {
    var text = "" { didSet { if text != oldValue { needsDisplay = true } } }
    var color: NSColor = T.FG
    override var isFlipped: Bool { true }
    static let font = Theme.font15Bold
    static let height = Theme.lineHeight(HeadlineView.font)
    override func draw(_ dirtyRect: NSRect) { Theme.drawText(text, in: bounds, font: HeadlineView.font, color: color) }
}

/// Flipped view that lays out its subviews through a closure.
final class LayoutView: FlatView {
    var onLayout: ((NSRect) -> Void)?
    override func layout() { super.layout(); onLayout?(bounds) }
    override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); needsLayout = true }
}

/// Table with hover tracking (reported to the chart) and a set of rows linked from the chart.
final class HoverTable: NSTableView {
    var hotRow = -1
    var linkedRows = Set<Int>()
    var onHot: ((Int) -> Void)?
    var onKey: ((NSEvent) -> Bool)?
    var emptyText: String? { didSet { needsDisplay = true } }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self))
    }

    private func setHot(_ r: Int) {
        if r == hotRow { return }
        let old = hotRow
        hotRow = r
        for i in [old, r] where i >= 0 && i < numberOfRows { rowView(atRow: i, makeIfNecessary: false)?.needsDisplay = true }
        onHot?(r)
    }

    override func mouseMoved(with event: NSEvent) {
        let r = row(at: convert(event.locationInWindow, from: nil))
        setHot(r)
        HoverTip.shared.track(self, key: r, text: r >= 0 ? tipFor?(r) : nil, event: event)
    }
    override func mouseExited(with event: NSEvent) { setHot(-1); HoverTip.shared.reset(owner: self) }
    override func scrollWheel(with event: NSEvent) { HoverTip.shared.reset(owner: self); super.scrollWheel(with: event) }

    /// The hover tip of a row.
    var tipFor: ((Int) -> String?)?

    func setHighlight(_ rows: [Int]) {
        let old = linkedRows
        linkedRows = Set(rows)
        for i in old.union(linkedRows) where i < numberOfRows { rowView(atRow: i, makeIfNecessary: false)?.needsDisplay = true }
        if let first = rows.min(), !rows.contains(where: { visibleRect.intersects(rect(ofRow: $0)) }) { scrollRowToVisible(first) }
    }

    func resetHover() { hotRow = -1; linkedRows = []; HoverTip.shared.reset(owner: self) }

    override func keyDown(with event: NSEvent) { HoverTip.shared.hide(); if onKey?(event) != true { super.keyDown(with: event) } }

    /// Clicks on a row that the page handles itself (checkboxes, chevrons): true = consumed, the selection stays.
    var onClickAt: ((Int, NSPoint) -> Bool)?

    override func mouseDown(with event: NSEvent) {
        HoverTip.shared.hide()
        let p = convert(event.locationInWindow, from: nil)
        let r = row(at: p)
        if r >= 0, let f = onClickAt, f(r, p) { return }
        super.mouseDown(with: event)
    }

    /// Redraws the visible cells (their items changed in place).
    func redrawCells() {
        enumerateAvailableRowViews { rv, _ in
            rv.needsDisplay = true
            for v in rv.subviews { v.needsDisplay = true }
        }
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        HoverTip.shared.hide()
        let r = row(at: convert(event.locationInWindow, from: nil))
        if r >= 0 && !selectedRowIndexes.contains(r) { selectRowIndexes(IndexSet(integer: r), byExtendingSelection: false) }
        window?.makeFirstResponder(self)
        return super.menu(for: event)
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        if numberOfRows == 0, let t = emptyText {
            let ps = NSMutableParagraphStyle(); ps.alignment = .center
            let vis = enclosingScrollView?.documentVisibleRect ?? visibleRect
            (t as NSString).draw(in: NSRect(x: vis.minX + 16, y: 30, width: vis.width - 32, height: 80),
                                 withAttributes: [.font: Theme.font, .foregroundColor: T.MUTED, .paragraphStyle: ps])
        }
    }
}

final class ListRowView: NSTableRowView {
    weak var table: HoverTable?
    var index = -1
    override var isFlipped: Bool { true }
    override func drawBackground(in dirtyRect: NSRect) {
        T.SURFACE.setFill(); bounds.fill()
        guard let t = table else { return }
        let r = bounds.insetBy(dx: 2, dy: 1)
        if !isSelected {
            if t.linkedRows.contains(index) { Theme.fillRound(r, 6, fill: T.BORDER, outline: T.ACCENT) }
            else if t.hotRow == index { Theme.fillRound(r, 6, fill: T.BORDER) }
        }
    }
    override func drawSelection(in dirtyRect: NSRect) {
        let linked = table?.linkedRows.contains(index) ?? false
        let fill = isEmphasized ? T.ACCENT : T.BORDER
        Theme.fillRound(bounds.insetBy(dx: 2, dy: 1), 6, fill: fill, outline: linked && !isEmphasized ? T.ACCENT : fill)
    }
    override var interiorBackgroundStyle: NSView.BackgroundStyle { isSelected && isEmphasized ? .emphasized : .normal }
}

/// Draws one cell of a view's list: the name column has the icon (indented for group members, bold for group heads),
/// the bar column a percentage pill, the others plain text.
final class ListCell: NSTableCellView {
    var item: ItemInfo?
    var index = 0
    weak var page: Page?
    override var isFlipped: Bool { true }
    override var backgroundStyle: NSView.BackgroundStyle { didSet { needsDisplay = true } }

    override func draw(_ dirtyRect: NSRect) {
        guard let it = item, let pg = page, index < pg.columns.count else { return }
        let strong = backgroundStyle == .emphasized
        let fore = strong ? T.BG : (it.color ?? (it.isSummary ? T.MUTED : T.FG))
        let text = index < it.cells.count ? it.cells[index] : ""
        let b = bounds
        if index == 0 {
            var x: CGFloat = 6
            if it.chevron >= 0 {
                let cx = ListCell.chevronX + 7, cy = (b.height / 2).rounded()
                let tri = NSBezierPath()
                if it.chevron == 1 {
                    tri.move(to: NSPoint(x: cx - 4.5, y: cy - 2.5)); tri.line(to: NSPoint(x: cx + 4.5, y: cy - 2.5)); tri.line(to: NSPoint(x: cx, y: cy + 3))
                } else {
                    tri.move(to: NSPoint(x: cx - 2, y: cy - 4.5)); tri.line(to: NSPoint(x: cx + 3.5, y: cy)); tri.line(to: NSPoint(x: cx - 2, y: cy + 4.5))
                }
                tri.close()
                (strong ? T.BG : T.MUTED).setFill()
                tri.fill()
            }
            if it.check >= 0 {
                let cx = ListCell.checkX(it)
                ListCell.drawCheck(NSRect(x: cx, y: ((b.height - 14) / 2).rounded(), width: 14, height: 14), it.check, strong: strong)
                x = cx + 14 + 6
            } else if it.chevron >= 0 { x = ListCell.checkX(it) }          // after the chevron
            else if let g = it.groupOf, g.chevron >= 0 { x = 42 }            // under an opened head with a chevron
            else if it.groupOf != nil { x += 18 }
            if !it.isSummary && !it.isGroup {
                let ir = NSRect(x: x, y: (b.height - 16) / 2, width: 16, height: 16)
                if it.isDir || it.folderIcon { Theme.drawFolder(ir, strong ? T.BG : T.MUTED) } else { Theme.drawFile(ir, strong ? T.BG : T.MUTED) }
                x += 22
            }
            Theme.drawText(text, in: NSRect(x: x, y: 0, width: max(0, b.width - x - 4), height: b.height), font: it.isGroup ? Theme.fontBold : Theme.font, color: fore)
        } else if index == pg.barColumn {
            let textW = Theme.textWidth("100.0 %", Theme.font)
            let bh: CGFloat = 8
            let bar = NSRect(x: 6, y: (b.height - bh) / 2, width: max(0, b.width - textW - 12), height: bh)
            if bar.width > 8 && !it.isSummary && !it.hideBar {
                Theme.fillRound(bar, bh / 2, fill: strong ? T.BG : T.BORDER)
                let w = (bar.width * CGFloat(min(100, max(0, it.percent))) / 100).rounded()
                if w > 0 { Theme.fillRound(NSRect(x: bar.minX, y: bar.minY, width: max(w, bh), height: bh), bh / 2, fill: strong ? T.FG : T.ACCENT) }
            }
            Theme.drawText(text, in: NSRect(x: b.width - textW - 6, y: 0, width: textW, height: b.height), color: fore, align: .right)
        } else {
            Theme.drawText(text, in: b.insetBy(dx: 6, dy: 0), color: fore, align: pg.columns[index].right ? .right : .left)
        }
    }

    static let chevronX: CGFloat = 4
    /// x of the checkbox in the name column: a head next to its chevron, its members indented by 18 px.
    static func checkX(_ it: ItemInfo) -> CGFloat { it.groupOf != nil ? 42 : 24 }

    /// 14 px rounded checkbox: unchecked = muted outline, checked = accent with a check mark, mixed = accent with a dash.
    /// On a selected (accent) row it is drawn in bg / fg so it stays visible.
    static func drawCheck(_ r: NSRect, _ state: Int, strong: Bool) {
        if state == 0 {
            let p = NSBezierPath(roundedRect: r.insetBy(dx: 0.75, dy: 0.75), xRadius: 3, yRadius: 3)
            p.lineWidth = 1.5
            (strong ? T.BG : T.MUTED).setStroke()
            p.stroke()
            return
        }
        (strong ? T.BG : T.ACCENT).setFill()
        NSBezierPath(roundedRect: r, xRadius: 3, yRadius: 3).fill()
        let m = NSBezierPath()
        if state == 1 {
            m.move(to: NSPoint(x: r.minX + 3.5, y: r.midY))
            m.line(to: NSPoint(x: r.minX + 6, y: r.maxY - 3.5))
            m.line(to: NSPoint(x: r.maxX - 3, y: r.minY + 3.5))
        } else {
            m.move(to: NSPoint(x: r.minX + 3.5, y: r.midY))
            m.line(to: NSPoint(x: r.maxX - 3.5, y: r.midY))
        }
        m.lineWidth = 2
        m.lineCapStyle = .round
        m.lineJoinStyle = .round
        (strong ? T.FG : T.BG).setStroke()
        m.stroke()
    }
}

/// A view shown in a tab above the list and the chart: a caption line, an optional bar of filters and actions, and a
/// list. The chart always shows the list of the open page.
final class Page: NSObject, NSTableViewDataSource, NSTableViewDelegate {
    let title: String
    let root = LayoutView()
    let headline = HeadlineView()
    let caption = CaptionView()
    let bar: ActionBar?
    let table = HoverTable()
    let scroll = NSScrollView()
    let columns: [Column]
    let barColumn: Int
    var items: [ItemInfo] = []
    var sortKey = "size", ascending = false, sortable = true
    var stale = true, followsFolder = false
    var fill: () -> Void = {}
    var chartTitle = "", chartSub = "", chartScope = "of the list"
    var chartTotal: Int64 = 0
    var empty = ""
    var onSorted: ((Page) -> Void)?
    var onSelection: ((Page) -> Void)?
    var pinFirst: ((ItemInfo) -> Bool)?    // heads listed before the others whatever the sort order
    init(_ title: String, _ columns: [Column], barColumn: Int, bar: ActionBar?) {
        self.title = title; self.columns = columns; self.barColumn = barColumn; self.bar = bar
        super.init()
        root.fill = T.SURFACE
        if let b = bar {
            root.addSubview(b)
            b.onHeightChange = { [weak self] in self?.root.needsLayout = true }
        }
        for (i, c) in columns.enumerated() {
            let col = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("c\(i)"))
            let h = DarkHeaderCell(textCell: c.title)
            h.right = c.right
            h.active = i == 1
            col.headerCell = h
            col.width = c.width
            col.minWidth = 40
            table.addTableColumn(col)
        }
        table.headerView = DarkHeaderView(frame: NSRect(x: 0, y: 0, width: 100, height: 28))
        table.cornerView = nil
        table.backgroundColor = T.SURFACE
        table.rowHeight = 26
        table.intercellSpacing = NSSize(width: 0, height: 0)
        if #available(macOS 11.0, *) { table.style = .plain }
        table.allowsMultipleSelection = true
        table.allowsColumnReordering = true
        table.dataSource = self
        table.delegate = self
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.autohidesScrollers = true
        scroll.scrollerStyle = .overlay
        scroll.scrollerKnobStyle = .light
        scroll.drawsBackground = true
        scroll.backgroundColor = T.SURFACE
        scroll.borderType = .noBorder
        root.addSubview(scroll)
        root.addSubview(caption)
        headline.isHidden = true
        root.addSubview(headline)
        table.tipFor = { [weak self] r in
            guard let s = self, r >= 0, r < s.items.count else { return nil }
            return s.items[r].toolTip
        }
        root.onLayout = { [weak self] r in self?.layoutPage(r) }
        root.isHidden = true
    }

    private func layoutPage(_ r: NSRect) {
        let w = r.width
        let lineH = CaptionView.lineH
        var top: CGFloat = 0
        headline.isHidden = headline.text.isEmpty
        if !headline.isHidden {
            headline.frame = NSRect(x: 4, y: 4, width: max(10, w - 8), height: HeadlineView.height)
            top = 4 + HeadlineView.height - 3
        }
        let lines = caption.lines(width: max(10, w - 8))
        let capH = lineH * lines + 10
        caption.frame = NSRect(x: 4, y: top + 5, width: max(10, w - 8), height: lineH * lines + 3)
        var y = top + capH
        if let b = bar {
            let h = b.arrange(width: w)
            b.frame = NSRect(x: 0, y: y, width: w, height: h)
            y += h
        }
        y += 4
        scroll.frame = NSRect(x: 0, y: y, width: w, height: max(0, r.height - y))
    }

    func setCaption(_ s: String) {
        if caption.text == s { return }
        caption.text = s
        root.needsLayout = true
    }

    func setHeadline(_ s: String) {
        if headline.text == s { return }
        headline.text = s
        root.needsLayout = true
    }

    /// Replaces the rows (sorted by the current column unless the page keeps its own order).
    func setRows(_ rows: [ItemInfo], _ emptyText: String, keepOrder: Bool = false) {
        items = sortable && !keepOrder ? sorted(rows) : rows
        table.resetHover()
        table.emptyText = rows.isEmpty ? emptyText : nil
        table.reloadData()
    }

    /// Rows in the current sort order; members of a group (rows with groupOf) stay right under their head, sorted
    /// among themselves.
    func sorted(_ rows: [ItemInfo]) -> [ItemInfo] {
        let key = sortKey, asc = ascending
        let pin = pinFirst
        let less: (ItemInfo, ItemInfo) -> Bool = { a, b in
            if let p = pin { let pa = p(a), pb = p(b); if pa != pb { return pa } }
            return ItemInfo.compare(a, b, key, asc)
        }
        if !rows.contains(where: { $0.groupOf != nil }) { return rows.sorted(by: less) }
        var heads: [ItemInfo] = []
        var members: [ObjectIdentifier: [ItemInfo]] = [:]
        for r in rows {
            if let g = r.groupOf { members[ObjectIdentifier(g), default: []].append(r) } else { heads.append(r) }
        }
        return heads.sorted(by: less).flatMap { [$0] + (members[ObjectIdentifier($0)] ?? []).sorted(by: less) }
    }

    var selectedItems: [ItemInfo] { table.selectedRowIndexes.compactMap { $0 < items.count ? items[$0] : nil } }

    func select(_ rows: IndexSet) { table.selectRowIndexes(rows, byExtendingSelection: false) }

    // MARK: data source

    func numberOfRows(in tableView: NSTableView) -> Int { items.count }

    func tableView(_ tableView: NSTableView, rowViewForRow row: Int) -> NSTableRowView? {
        let v = ListRowView()
        v.table = table
        v.index = row
        return v
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard let col = tableColumn, row < items.count else { return nil }
        let id = NSUserInterfaceItemIdentifier("cell-" + col.identifier.rawValue)
        let c = (tableView.makeView(withIdentifier: id, owner: nil) as? ListCell) ?? { let n = ListCell(); n.identifier = id; return n }()
        c.index = Int(col.identifier.rawValue.dropFirst()) ?? 0
        c.page = self
        c.item = items[row]
        c.needsDisplay = true
        return c
    }

    func tableView(_ tableView: NSTableView, didClick tableColumn: NSTableColumn) {
        guard sortable, let i = Int(tableColumn.identifier.rawValue.dropFirst()), i < columns.count else { return }
        let key = columns[i].key
        if sortKey == key { ascending.toggle() } else { sortKey = key; ascending = key == "name" || key == "folder" || key == "cat" || key == "note" }
        items = sorted(items)
        table.resetHover()
        table.reloadData()
        for c in tableView.tableColumns {
            (c.headerCell as? DarkHeaderCell)?.active = c === tableColumn
            let base = c.headerCell.stringValue.replacingOccurrences(of: "  ▲", with: "").replacingOccurrences(of: "  ▼", with: "")
            c.headerCell.stringValue = c === tableColumn ? base + (ascending ? "  ▲" : "  ▼") : base
        }
        tableView.headerView?.needsDisplay = true
        onSorted?(self)
    }

    func tableViewSelectionDidChange(_ notification: Notification) { onSelection?(self) }
}
