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

    var chartSize: Int64 { chartValue >= 0 ? chartValue : size }
    var canDelete: Bool { !isSummary && !isGroup && (isDir || file != nil || targets != nil) }
    var fullPath: String? { isDir ? dir?.fullPath : (file?.fullPath ?? path) }

    static func forDir(_ d: DirNode, _ parentSize: Int64) -> ItemInfo {
        let it = ItemInfo()
        it.isDir = true; it.dir = d; it.name = d.name; it.size = d.size; it.files = d.files; it.dirs = d.dirs; it.modified = d.modified
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

    override func mouseMoved(with event: NSEvent) { setHot(row(at: convert(event.locationInWindow, from: nil))) }
    override func mouseExited(with event: NSEvent) { setHot(-1) }

    func setHighlight(_ rows: [Int]) {
        let old = linkedRows
        linkedRows = Set(rows)
        for i in old.union(linkedRows) where i < numberOfRows { rowView(atRow: i, makeIfNecessary: false)?.needsDisplay = true }
        if let first = rows.min(), !rows.contains(where: { visibleRect.intersects(rect(ofRow: $0)) }) { scrollRowToVisible(first) }
    }

    func resetHover() { hotRow = -1; linkedRows = [] }

    override func keyDown(with event: NSEvent) { if onKey?(event) != true { super.keyDown(with: event) } }

    override func menu(for event: NSEvent) -> NSMenu? {
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
            if it.groupOf != nil { x += 18 }
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
            if bar.width > 8 && !it.isSummary {
                Theme.fillRound(bar, bh / 2, fill: strong ? T.BG : T.BORDER)
                let w = (bar.width * CGFloat(min(100, max(0, it.percent))) / 100).rounded()
                if w > 0 { Theme.fillRound(NSRect(x: bar.minX, y: bar.minY, width: max(w, bh), height: bh), bh / 2, fill: strong ? T.FG : T.ACCENT) }
            }
            Theme.drawText(text, in: NSRect(x: b.width - textW - 6, y: 0, width: textW, height: b.height), color: fore, align: .right)
        } else {
            Theme.drawText(text, in: b.insetBy(dx: 6, dy: 0), color: fore, align: pg.columns[index].right ? .right : .left)
        }
    }
}

/// A view shown in a tab above the list and the chart: a caption line, an optional bar of filters and actions, and a
/// list. The chart always shows the list of the open page.
final class Page: NSObject, NSTableViewDataSource, NSTableViewDelegate {
    let title: String
    let root = LayoutView()
    let caption = NSTextField(wrappingLabelWithString: "")
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

    init(_ title: String, _ columns: [Column], barColumn: Int, bar: ActionBar?) {
        self.title = title; self.columns = columns; self.barColumn = barColumn; self.bar = bar
        super.init()
        root.fill = T.SURFACE
        caption.font = Theme.font9
        caption.textColor = T.MUTED
        caption.maximumNumberOfLines = 2
        caption.lineBreakMode = .byWordWrapping
        caption.cell?.truncatesLastVisibleLine = true
        caption.drawsBackground = false
        root.addSubview(caption)
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
        root.onLayout = { [weak self] r in self?.layoutPage(r) }
        root.isHidden = true
    }

    private func layoutPage(_ r: NSRect) {
        let w = r.width
        let lineH = ceil(Theme.font9.ascender - Theme.font9.descender + Theme.font9.leading)
        let textH = (caption.stringValue as NSString).boundingRect(with: NSSize(width: max(10, w - 8), height: 1000),
                                                                   options: [.usesLineFragmentOrigin], attributes: [.font: Theme.font9]).height
        let lines: CGFloat = textH > lineH * 1.5 ? 2 : 1
        let capH = lineH * lines + 10
        caption.frame = NSRect(x: 4, y: 5, width: max(10, w - 8), height: lineH * lines + 2)
        var y = capH
        if let b = bar {
            let h = b.arrange(width: w)
            b.frame = NSRect(x: 0, y: y, width: w, height: h)
            y += h
        }
        y += 4
        scroll.frame = NSRect(x: 0, y: y, width: w, height: max(0, r.height - y))
    }

    func setCaption(_ s: String) {
        if caption.stringValue == s { return }
        caption.stringValue = s
        root.needsLayout = true
    }

    /// Replaces the rows (sorted by the current column unless the page keeps its own order).
    func setRows(_ rows: [ItemInfo], _ emptyText: String, keepOrder: Bool = false) {
        items = sortable && !keepOrder ? rows.sorted { ItemInfo.compare($0, $1, sortKey, ascending) } : rows
        table.resetHover()
        table.emptyText = rows.isEmpty ? emptyText : nil
        table.reloadData()
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
        items.sort { ItemInfo.compare($0, $1, sortKey, ascending) }
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
