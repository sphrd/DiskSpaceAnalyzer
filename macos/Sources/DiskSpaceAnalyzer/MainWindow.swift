import AppKit

/// A top-level entry of the tree: a drive (scanned or not) or a folder the user scanned.
final class ScanRoot {
    var path: String
    var isDrive = false, isSystem = false
    var label = ""
    var total: Int64 = 0, free: Int64 = 0
    var node: DirNode?
    var top: [FileEntry] = []
    var scanning = false
    init(path: String) { self.path = path }
    var displayName: String { isDrive && !label.isEmpty ? label : path }
    var used: Int64 { max(0, total - free) }
}

/// One row of the contents list or the largest-files list.
struct Row {
    var name: String
    var isDir: Bool
    var dir: DirNode?
    var file: FileEntry?
    var size: Int64
    var files: Int64 = 0, dirs: Int64 = 0
    var modified: Date?
    var percent: Double = 0
    var folder = ""
    var isSummary = false
    var denied = false
    var fullPath: String { isDir ? dir!.fullPath : (file?.fullPath ?? "") }
}

// MARK: - Table and outline subclasses

/// Table with hover tracking (reported to the chart) and a set of rows linkedRows from the chart.
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
            (t as NSString).draw(in: NSRect(x: vis.minX + 16, y: 30, width: vis.width - 32, height: 60),
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

final class TreeRowView: NSTableRowView {
    override var isFlipped: Bool { true }
    override func drawBackground(in dirtyRect: NSRect) { T.SURFACE.setFill(); bounds.fill() }
    override func drawSelection(in dirtyRect: NSRect) {
        let fill = isEmphasized ? T.ACCENT : T.BORDER
        Theme.fillRound(bounds.insetBy(dx: 2, dy: 1), 6, fill: fill)
    }
    override var interiorBackgroundStyle: NSView.BackgroundStyle { isSelected && isEmphasized ? .emphasized : .normal }
}

final class KeyOutline: NSOutlineView {
    var onKey: ((NSEvent) -> Bool)?
    override func keyDown(with event: NSEvent) { if onKey?(event) != true { super.keyDown(with: event) } }
    override func menu(for event: NSEvent) -> NSMenu? {
        let r = row(at: convert(event.locationInWindow, from: nil))
        if r >= 0 { selectRowIndexes(IndexSet(integer: r), byExtendingSelection: false) }
        window?.makeFirstResponder(self)
        return super.menu(for: event)
    }
}

final class DarkHeaderCell: NSTableHeaderCell {
    var active = false
    var right = false
    override func draw(withFrame cellFrame: NSRect, in controlView: NSView) {
        T.SURFACE.setFill(); cellFrame.fill()
        T.BORDER.setFill()
        NSRect(x: cellFrame.minX, y: cellFrame.maxY - 1, width: cellFrame.width, height: 1).fill()
        NSRect(x: cellFrame.maxX - 1, y: cellFrame.minY + 6, width: 1, height: cellFrame.height - 12).fill()
        Theme.drawText(stringValue, in: cellFrame.insetBy(dx: 7, dy: 0), font: Theme.font, color: active ? T.ACCENT : T.MUTED, align: right ? .right : .left)
    }
}

final class DarkHeaderView: NSTableHeaderView {
    override func draw(_ dirtyRect: NSRect) {
        T.SURFACE.setFill(); bounds.fill()
        T.BORDER.setFill(); NSRect(x: 0, y: bounds.maxY - 1, width: bounds.width, height: 1).fill()
        super.draw(dirtyRect)
    }
}

/// Draws one cell of the contents / largest-files lists.
final class ListCell: NSTableCellView {
    var row: Row?
    var column = ""
    override var isFlipped: Bool { true }
    override var backgroundStyle: NSView.BackgroundStyle { didSet { needsDisplay = true } }

    override func draw(_ dirtyRect: NSRect) {
        guard let r = row else { return }
        let strong = backgroundStyle == .emphasized
        let fore = strong ? T.BG : (r.isSummary || r.denied ? T.MUTED : T.FG)
        let b = bounds
        switch column {
        case "name":
            if !r.isSummary {
                let ir = NSRect(x: 6, y: (b.height - 16) / 2, width: 16, height: 16)
                if r.isDir { Theme.drawFolder(ir, strong ? T.BG : T.MUTED) } else { Theme.drawFile(ir, strong ? T.BG : T.MUTED) }
            }
            Theme.drawText(r.name, in: NSRect(x: 28, y: 0, width: b.width - 32, height: b.height), color: fore)
        case "size":
            Theme.drawText(Fmt.size(r.size), in: b.insetBy(dx: 6, dy: 0), color: fore, align: .right)
        case "pct":
            let textW = Theme.textWidth("100.0 %", Theme.font)
            let bh: CGFloat = 8
            let bar = NSRect(x: 6, y: (b.height - bh) / 2, width: max(0, b.width - textW - 12), height: bh)
            if bar.width > 8 && !r.isSummary {
                Theme.fillRound(bar, bh / 2, fill: strong ? T.BG : T.BORDER)
                let w = (bar.width * CGFloat(min(100, max(0, r.percent))) / 100).rounded()
                if w > 0 { Theme.fillRound(NSRect(x: bar.minX, y: bar.minY, width: max(w, bh), height: bh), bh / 2, fill: strong ? T.FG : T.ACCENT) }
            }
            Theme.drawText(Fmt.pct(r.percent), in: NSRect(x: b.width - textW - 6, y: 0, width: textW, height: b.height), color: fore, align: .right)
        case "files":
            Theme.drawText(Fmt.count(r.files), in: b.insetBy(dx: 6, dy: 0), color: fore, align: .right)
        case "dirs":
            Theme.drawText(r.isDir ? Fmt.count(r.dirs) : "", in: b.insetBy(dx: 6, dy: 0), color: fore, align: .right)
        case "mod":
            Theme.drawText(r.modified.map(Fmt.date) ?? "", in: b.insetBy(dx: 6, dy: 0), color: fore)
        case "folder":
            Theme.drawText(r.folder, in: b.insetBy(dx: 6, dy: 0), color: fore)
        default: break
        }
    }
}

/// Tree cell: folder glyph, name, then right-aligned Size and % of parent (with a pill bar) columns.
final class TreeCell: NSTableCellView {
    var name = "", size = "", pct = ""
    var bar: Double = -1
    var dim = false
    override var isFlipped: Bool { true }
    override var backgroundStyle: NSView.BackgroundStyle { didSet { needsDisplay = true } }

    static let sizeW = Theme.textWidth("999.9 MB", Theme.font)
    static let pctW = Theme.textWidth("100.0 %", Theme.font)
    static let barW: CGFloat = 44
    static let gap: CGFloat = 14

    /// x of the size column and of the bar+pct column, measured from the right edge of `width`.
    static func columns(_ width: CGFloat) -> (size: CGFloat, pct: CGFloat) {
        let pctX = width - 8 - pctW - barW - 8
        let sizeX = pctX - gap - sizeW
        return (sizeX, pctX)
    }

    override func draw(_ dirtyRect: NSRect) {
        let strong = backgroundStyle == .emphasized
        let b = bounds
        let cols = TreeCell.columns(b.width)
        Theme.drawFolder(NSRect(x: 2, y: (b.height - 16) / 2, width: 16, height: 16), strong ? T.BG : T.MUTED)
        Theme.drawText(name, in: NSRect(x: 24, y: 0, width: max(0, cols.size - 24 - TreeCell.gap), height: b.height), color: strong ? T.BG : (dim ? T.MUTED : T.FG))
        Theme.drawText(size, in: NSRect(x: cols.size, y: 0, width: TreeCell.sizeW, height: b.height), color: strong ? T.BG : T.FG, align: .right)
        if bar >= 0 {
            let bh: CGFloat = 6
            let br = NSRect(x: cols.pct, y: (b.height - bh) / 2, width: TreeCell.barW, height: bh)
            Theme.fillRound(br, bh / 2, fill: strong ? T.BG : T.BORDER)
            let w = (TreeCell.barW * CGFloat(min(100, bar)) / 100).rounded()
            if w > 0 { Theme.fillRound(NSRect(x: br.minX, y: br.minY, width: max(w, bh), height: bh), bh / 2, fill: strong ? T.FG : T.ACCENT) }
        }
        Theme.drawText(pct, in: NSRect(x: b.width - 8 - TreeCell.pctW, y: 0, width: TreeCell.pctW, height: b.height), color: strong ? T.BG : T.MUTED, align: .right)
    }
}

/// "Name / Size / % of parent" header above the tree, aligned with the tree cells' columns.
final class TreeHeader: NSView {
    weak var outline: NSOutlineView?
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: NSRect) {
        T.SURFACE.setFill(); bounds.fill()
        T.BORDER.setFill(); NSRect(x: 0, y: bounds.maxY - 1, width: bounds.width, height: 1).fill()
        Theme.drawText("Name", in: NSRect(x: 8, y: 0, width: 100, height: bounds.height - 1), color: T.MUTED)
        let cols = TreeCell.columns(bounds.width)
        Theme.drawText("Size", in: NSRect(x: cols.size, y: 0, width: TreeCell.sizeW, height: bounds.height - 1), color: T.MUTED, align: .right)
        let w = Theme.textWidth("% of parent", Theme.font)
        Theme.drawText("% of parent", in: NSRect(x: bounds.width - 8 - w, y: 0, width: w, height: bounds.height - 1), color: T.MUTED, align: .right)
    }
}

/// Rounded panel that lays out its content in the inner rectangle.
final class ContainerPanel: PanelView {
    var onLayout: ((NSRect) -> Void)?
    override func layout() { super.layout(); onLayout?(bounds.insetBy(dx: 8, dy: 8)) }
    override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); needsLayout = true }
}

// MARK: - Main window

final class MainWindowController: NSWindowController, NSOutlineViewDataSource, NSOutlineViewDelegate, NSTableViewDataSource,
    NSTableViewDelegate, NSMenuDelegate, NSSplitViewDelegate, NSTextFieldDelegate, NSWindowDelegate {

    static let topCount = 1000
    static let maxFilesShown = 2000

    // Model
    private(set) var roots: [ScanRoot] = []
    private var active: ScanRoot?
    private var current: DirNode?
    private var rows: [Row] = []
    private var largestRows: [Row] = []
    private var sortKey = "size", sortAscending = false
    private var largestSortKey = "size", largestAscending = false
    private var rowSlice: [Int: Int] = [:]
    private var scanner: Scanner?
    private var scanRoot: ScanRoot?
    private var deleteJob: DeleteJob?
    private(set) var scanning = false
    private var busy = false
    private var drivesChanged = false
    private var lastView: NSView?
    private var progressTimer: Timer?
    var onScanFinished: (() -> Void)?

    // Views
    private let root = FlatView()
    private let pathLabel = NSTextField(labelWithString: "Location:")
    private let pathPanel = ContainerPanel()
    private let pathField = NSTextField()
    private let drivesButton = ChevronButton()
    private lazy var btnBrowse = RoundButton("Choose...", .secondary) { [unowned self] in self.browse() }
    private lazy var btnScan = RoundButton("Scan", .primary) { [unowned self] in self.startScan() }
    private lazy var btnAccess = RoundButton("Full Disk Access...", .secondary) { [unowned self] in self.openFullDiskAccess() }
    private lazy var btnUp = RoundButton("Up", .secondary) { [unowned self] in self.goUp() }
    private lazy var btnRescan = RoundButton("Rescan folder", .secondary) { [unowned self] in self.rescanSelected() }
    private lazy var btnFinder = RoundButton("Show in Finder", .secondary) { [unowned self] in self.showInFinder() }
    private lazy var btnDelete = RoundButton("Delete permanently", .warn) { [unowned self] in self.deleteSelected() }
    let banner = Banner()
    private let scanBar = ScanBar()
    private let split = NSSplitView()
    private let treePanel = ContainerPanel()
    private let treeHeader = TreeHeader()
    private let outline = KeyOutline()
    private let treeScroll = NSScrollView()
    private let listPanel = ContainerPanel()
    private let tabs = TabStrip(["Folder contents", "Largest files (top 1000)"])
    private let contents = HoverTable()
    private let contentsScroll = NSScrollView()
    private let largest = HoverTable()
    private let largestScroll = NSScrollView()
    private let chartPanel = ContainerPanel()
    let chart = PieChart()
    private let statusLabel = NSTextField(labelWithString: "Choose a drive or folder and press Scan.")
    private let driveLabel = NSTextField(labelWithString: "")
    private lazy var versionLink = LinkLabel("v" + Updater.currentString) { [unowned self] in self.updates.check(manual: true) }
    private let overlay = BusyOverlay()
    private let contextMenu = NSMenu()
    lazy var updates = UpdateController(banner: banner)
    private var splitPositioned = false

    convenience init() {
        let vf = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let size = NSSize(width: min(1280, vf.width * 0.85), height: min(760, vf.height * 0.85))
        let w = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        w.title = "Disk Space Analyzer"
        w.appearance = NSAppearance(named: .darkAqua)
        w.backgroundColor = T.BG
        w.minSize = NSSize(width: min(900, vf.width), height: min(520, vf.height))
        w.setFrameOrigin(NSPoint(x: vf.midX - w.frame.width / 2, y: vf.midY - w.frame.height / 2))
        self.init(window: w)
        w.delegate = self
        build()
    }

    // MARK: Build

    private func styleScroll(_ s: NSScrollView, _ doc: NSView) {
        s.documentView = doc
        s.hasVerticalScroller = true
        s.hasHorizontalScroller = true
        s.autohidesScrollers = true
        s.scrollerStyle = .overlay
        s.scrollerKnobStyle = .light
        s.drawsBackground = true
        s.backgroundColor = T.SURFACE
        s.borderType = .noBorder
    }

    private func build() {
        guard let w = window else { return }
        root.fill = T.BG
        w.contentView = root

        pathLabel.font = Theme.font; pathLabel.textColor = T.FG
        pathField.isBordered = false; pathField.drawsBackground = true; pathField.backgroundColor = T.SURFACE
        pathField.textColor = T.FG; pathField.font = Theme.font; pathField.focusRingType = .none
        pathField.delegate = self
        pathField.placeholderString = "Drive or folder"
        pathField.cell?.isScrollable = true
        pathPanel.radius = 8
        pathPanel.addSubview(pathField); pathPanel.addSubview(drivesButton)
        pathPanel.onLayout = { [unowned self] _ in
            let b = self.pathPanel.bounds
            self.drivesButton.frame = NSRect(x: b.width - 30, y: 1, width: 26, height: b.height - 2)
            let fh: CGFloat = 18
            self.pathField.frame = NSRect(x: 10, y: (b.height - fh) / 2, width: b.width - 44, height: fh)
        }
        drivesButton.onPress = { [unowned self] in self.showDrivesMenu() }
        btnScan.tip = "Analyze the drive or folder"
        btnAccess.tip = "Let the app read every folder (System Settings > Privacy & Security > Full Disk Access)"
        btnUp.tip = "Go to the parent folder (⌘↑)"
        btnRescan.tip = "Rescan the selected folder (⌘R)"
        btnDelete.tip = "Permanently delete the selected items, bypassing the Trash (⌘⌫)"
        for v: NSView in [pathLabel, pathPanel, btnBrowse, btnScan, btnAccess, btnUp, btnRescan, btnFinder, btnDelete, banner, scanBar, split, statusLabel, driveLabel, versionLink] {
            root.addSubview(v)
        }
        for l in [statusLabel, driveLabel] { l.font = Theme.font9; l.textColor = T.MUTED; l.lineBreakMode = .byTruncatingTail }
        driveLabel.alignment = .right

        banner.onChange = { [unowned self] in self.layoutRoot() }
        scanBar.cancelButton.onClick = { [unowned self] in self.scanner?.cancel(); self.scanBar.cancelButton.isEnabled = false }
        updates.canRestart = { [unowned self] in !self.busy }

        // Tree
        let col = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("tree"))
        col.resizingMask = .autoresizingMask
        outline.addTableColumn(col)
        outline.outlineTableColumn = col
        outline.headerView = nil
        outline.backgroundColor = T.SURFACE
        outline.rowHeight = 26
        outline.intercellSpacing = NSSize(width: 0, height: 0)
        outline.indentationPerLevel = 16
        if #available(macOS 11.0, *) { outline.style = .plain }
        outline.columnAutoresizingStyle = .uniformColumnAutoresizingStyle
        outline.dataSource = self; outline.delegate = self
        outline.target = self; outline.doubleAction = #selector(treeDoubleClick)
        outline.menu = contextMenu
        outline.onKey = { [unowned self] e in self.handleKey(e, in: self.outline) }
        styleScroll(treeScroll, outline)
        treeScroll.hasHorizontalScroller = false
        treeHeader.outline = outline
        treePanel.addSubview(treeHeader); treePanel.addSubview(treeScroll)
        treePanel.onLayout = { [unowned self] r in
            self.treeHeader.frame = NSRect(x: r.minX, y: r.minY, width: r.width, height: 28)
            self.treeScroll.frame = NSRect(x: r.minX, y: r.minY + 32, width: r.width, height: r.height - 32)
            self.treeHeader.needsDisplay = true
        }

        // Lists
        setUpTable(contents, [("name", "Name", 230, false), ("size", "Size", 84, true), ("pct", "% of folder", 150, true),
                              ("files", "Files", 74, true), ("dirs", "Folders", 66, true), ("mod", "Modified", 128, false)])
        setUpTable(largest, [("name", "Name", 240, false), ("size", "Size", 90, true), ("folder", "Folder", 420, false), ("mod", "Modified", 130, false)])
        contents.onHot = { [unowned self] r in self.chart.setHover(self.rowSlice[r] ?? -1) }
        styleScroll(contentsScroll, contents)
        styleScroll(largestScroll, largest)
        largestScroll.isHidden = true
        tabs.onChange = { [unowned self] i in self.contentsScroll.isHidden = i != 0; self.largestScroll.isHidden = i != 1 }
        listPanel.addSubview(tabs); listPanel.addSubview(contentsScroll); listPanel.addSubview(largestScroll)
        listPanel.onLayout = { [unowned self] r in
            self.tabs.frame = NSRect(x: r.minX, y: r.minY, width: r.width, height: 30)
            let lr = NSRect(x: r.minX, y: r.minY + 36, width: r.width, height: r.height - 36)
            self.contentsScroll.frame = lr; self.largestScroll.frame = lr
        }

        // Chart
        chartPanel.addSubview(chart)
        chartPanel.onLayout = { [unowned self] r in self.chart.frame = r }
        chart.onHover = { [unowned self] i in
            self.contents.setHighlight(i >= 0 && i < self.chart.slices.count ? self.chart.slices[i].rows : [])
        }
        chart.onClick = { [unowned self] s in if let d = s.dir, !self.busy { self.selectDir(d) } }

        split.isVertical = true
        split.dividerStyle = .thin
        split.delegate = self
        split.addArrangedSubview(treePanel); split.addArrangedSubview(listPanel); split.addArrangedSubview(chartPanel)
        split.setHoldingPriority(NSLayoutConstraint.Priority(260), forSubviewAt: 0)
        split.setHoldingPriority(NSLayoutConstraint.Priority(250), forSubviewAt: 1)
        split.setHoldingPriority(NSLayoutConstraint.Priority(270), forSubviewAt: 2)

        // Context menu
        contextMenu.delegate = self
        contextMenu.appearance = NSAppearance(named: .darkAqua)

        root.addSubview(overlay)
        overlay.isHidden = true

        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(volumesChanged), name: NSWorkspace.didMountNotification, object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(volumesChanged), name: NSWorkspace.didUnmountNotification, object: nil)

        root.postsFrameChangedNotifications = true
        NotificationCenter.default.addObserver(self, selector: #selector(layoutRoot), name: NSView.frameDidChangeNotification, object: root)
        layoutRoot()
        updateButtons()
    }

    private func setUpTable(_ t: HoverTable, _ cols: [(String, String, CGFloat, Bool)]) {
        for (id, title, width, right) in cols {
            let c = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            let h = DarkHeaderCell(textCell: title)
            h.right = right
            h.active = id == "size"
            c.headerCell = h
            c.width = width
            c.minWidth = 40
            t.addTableColumn(c)
        }
        t.headerView = DarkHeaderView(frame: NSRect(x: 0, y: 0, width: 100, height: 28))
        t.cornerView = nil
        t.backgroundColor = T.SURFACE
        t.rowHeight = 26
        t.intercellSpacing = NSSize(width: 0, height: 0)
        if #available(macOS 11.0, *) { t.style = .plain }
        t.allowsMultipleSelection = true
        t.allowsColumnReordering = true
        t.dataSource = self; t.delegate = self
        t.target = self; t.doubleAction = #selector(listDoubleClick)
        t.menu = contextMenu
        t.onKey = { [unowned self] e in self.handleKey(e, in: t) }
    }

    @objc private func layoutRoot() {
        let b = root.bounds
        let m: CGFloat = 15, gap: CGFloat = 6
        var y: CGFloat = 12
        let h: CGFloat = 32
        let ap = btnAccess.preferredSize, sp = btnScan.preferredSize, bp = btnBrowse.preferredSize
        btnAccess.frame = NSRect(x: b.width - m - ap.width, y: y, width: ap.width, height: h)
        btnScan.frame = NSRect(x: btnAccess.frame.minX - 16 - sp.width, y: y, width: sp.width, height: h)
        btnBrowse.frame = NSRect(x: btnScan.frame.minX - gap - bp.width, y: y, width: bp.width, height: h)
        pathLabel.sizeToFit()
        pathLabel.frame.origin = NSPoint(x: m, y: y + (h - pathLabel.frame.height) / 2)
        let px = pathLabel.frame.maxX + gap
        pathPanel.frame = NSRect(x: px, y: y, width: max(120, btnBrowse.frame.minX - gap - px), height: h)
        pathPanel.needsLayout = true
        y += h + 8
        var x = m
        for btn in [btnUp, btnRescan, btnFinder, btnDelete] {
            if btn === btnDelete { x += 10 }
            let p = btn.preferredSize
            btn.frame = NSRect(x: x, y: y, width: p.width, height: h)
            x += p.width + gap
        }
        y += h + 10
        if !banner.isHidden { banner.frame = NSRect(x: m, y: y, width: b.width - 2 * m, height: Banner.height); y += Banner.height }
        if !scanBar.isHidden { scanBar.frame = NSRect(x: m, y: y, width: b.width - 2 * m, height: ScanBar.height); y += ScanBar.height }
        let statusH: CGFloat = 28
        split.frame = NSRect(x: m, y: y, width: b.width - 2 * m, height: max(100, b.height - y - statusH))
        let sy = b.height - statusH + 6
        let vw = Theme.textWidth(versionLink.textValue, Theme.font9) + 4
        versionLink.frame = NSRect(x: b.width - m - vw, y: sy, width: vw, height: 16)
        driveLabel.frame = NSRect(x: b.width / 2, y: sy, width: versionLink.frame.minX - 20 - b.width / 2, height: 16)
        statusLabel.frame = NSRect(x: m, y: sy, width: b.width / 2 - m - 10, height: 16)
        overlay.frame = b
        if !splitPositioned && split.frame.width > 300 {
            splitPositioned = true
            let w = split.frame.width
            let treeW = min(430, w * 0.30), chartW = min(330, (w - treeW) * 0.38)
            split.setPosition(treeW, ofDividerAt: 0)
            split.setPosition(w - chartW - split.dividerThickness, ofDividerAt: 1)
        }
    }

    func splitView(_ splitView: NSSplitView, constrainMinCoordinate proposedMinimumPosition: CGFloat, ofSubviewAt dividerIndex: Int) -> CGFloat {
        dividerIndex == 0 ? 180 : splitView.subviews[0].frame.maxX + 200
    }
    func splitView(_ splitView: NSSplitView, constrainMaxCoordinate proposedMaximumPosition: CGFloat, ofSubviewAt dividerIndex: Int) -> CGFloat {
        dividerIndex == 0 ? splitView.subviews[1].frame.maxX - 200 : splitView.bounds.width - 180
    }
    func windowDidResize(_ notification: Notification) { layoutRoot() }

    // MARK: Start-up

    func start(initialPath: String?) {
        loadDrives()
        refreshTree(nil)
        if let p = initialPath { scanPath(p) } else { scanPath("/") }
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in self?.updates.check(manual: false) }
    }

    // MARK: Drives

    private static func same(_ a: String, _ b: String) -> Bool {
        let x = a.count > 1 && a.hasSuffix("/") ? String(a.dropLast()) : a
        let y = b.count > 1 && b.hasSuffix("/") ? String(b.dropLast()) : b
        return x == y
    }

    private func findRoot(_ path: String) -> ScanRoot? { roots.first { MainWindowController.same($0.path, path) } }
    private func rootOf(_ top: DirNode?) -> ScanRoot? { guard let t = top else { return nil }; return roots.first { $0.node === t } }

    private func sortRoots() {
        roots.sort { a, b in
            if a.isSystem != b.isSystem { return a.isSystem }
            if a.isDrive != b.isDrive { return a.isDrive }
            return a.path.localizedStandardCompare(b.path) == .orderedAscending
        }
    }

    static func capacity(_ path: String) -> (Int64, Int64) {
        let url = URL(fileURLWithPath: path)
        guard let v = try? url.resourceValues(forKeys: [.volumeTotalCapacityKey, .volumeAvailableCapacityForImportantUsageKey, .volumeAvailableCapacityKey]) else { return (0, 0) }
        let total = Int64(v.volumeTotalCapacity ?? 0)
        let free = v.volumeAvailableCapacityForImportantUsage ?? Int64(v.volumeAvailableCapacity ?? 0)
        return (total, free)
    }

    private func refreshCapacity(_ r: ScanRoot) { let c = MainWindowController.capacity(r.path); r.total = c.0; r.free = c.1 }

    private func loadDrives() {
        let keys: [URLResourceKey] = [.volumeNameKey, .volumeIsLocalKey, .volumeIsInternalKey, .volumeIsRemovableKey, .volumeIsEjectableKey, .volumeIsBrowsableKey]
        let urls = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: keys, options: [.skipHiddenVolumes]) ?? []
        var ready: [(String, URLResourceValues)] = []
        for u in urls {
            guard let v = try? u.resourceValues(forKeys: Set(keys)) else { continue }
            if v.volumeIsLocal == false || v.volumeIsBrowsable == false { continue }   // network shares are not listed
            ready.append((u.path, v))
        }
        roots.removeAll { r in r.isDrive && !ready.contains { MainWindowController.same($0.0, r.path) } && !r.scanning }
        for (path, v) in ready {
            let r = findRoot(path) ?? { let n = ScanRoot(path: path); roots.append(n); return n }()
            r.isDrive = true
            r.isSystem = path == "/"
            let name = v.volumeName ?? path
            let external = v.volumeIsInternal == false || v.volumeIsRemovable == true || v.volumeIsEjectable == true
            r.label = name + (external ? " - external" : "")
            refreshCapacity(r)
        }
        if let a = active, !roots.contains(where: { $0 === a }) { active = nil; current = nil }
        sortRoots()
    }

    @objc private func volumesChanged() {
        if busy || scanning { drivesChanged = true; return }
        drivesChanged = false
        loadDrives()
        refreshTree(current)
    }

    private func showDrivesMenu() {
        let m = NSMenu()
        m.appearance = NSAppearance(named: .darkAqua)
        for r in roots where r.isDrive {
            let item = NSMenuItem(title: r.displayName + "   " + r.path, action: #selector(chooseDrive(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = r.path
            m.addItem(item)
        }
        m.popUp(positioning: nil, at: NSPoint(x: 0, y: pathPanel.bounds.height + 4), in: pathPanel)
    }

    @objc private func chooseDrive(_ sender: NSMenuItem) { if let p = sender.representedObject as? String { scanPath(p) } }

    private func openFullDiskAccess() {
        if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") { NSWorkspace.shared.open(u) }
    }

    // MARK: Scanning

    func controlTextDidEndEditing(_ obj: Notification) {
        if (obj.userInfo?["NSTextMovement"] as? Int) == NSTextMovement.return.rawValue { startScan() }
    }

    func scanPath(_ path: String) {
        if busy || scanning { return }
        pathField.stringValue = path
        startScan()
    }

    func startScan() {
        if busy || scanning { return }
        var path = (pathField.stringValue as NSString).expandingTildeInPath.trimmingCharacters(in: .whitespaces)
        if path.count > 1 && path.hasSuffix("/") { path.removeLast() }
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &isDir), isDir.boolValue else {
            alert("Folder not found:\n" + path); return
        }
        pathField.stringValue = path
        pathField.currentEditor()?.selectedRange = NSRange(location: path.count, length: 0)
        window?.makeFirstResponder(outline)
        var r = findRoot(path)
        let isNew = r == nil
        if r == nil { let n = ScanRoot(path: path); refreshCapacity(n); roots.append(n); sortRoots(); r = n }
        let target = r!
        let s = Scanner(topCount: MainWindowController.topCount)
        scanner = s; scanRoot = target
        target.scanning = true
        let oldNode = target.node
        setScanning(true, "Scanning " + target.path + "...")
        refreshTree(current)
        if current == nil && oldNode == nil { selectRoot(target) }
        let started = Date()
        DispatchQueue.global(qos: .userInitiated).async {
            let result = s.run(path)
            DispatchQueue.main.async {
                target.scanning = false
                self.scanner = nil; self.scanRoot = nil
                self.setScanning(false, nil)
                target.node = result
                target.top = s.topFiles
                self.refreshCapacity(target)
                var goTo: DirNode?
                if let c = self.current, let old = oldNode, c.top === old { goTo = MainWindowController.findByPath(result, c.fullPath) ?? result }
                else if let a = self.active, a !== target { goTo = self.current }
                else { goTo = result }
                if self.active === target { self.current = nil }
                _ = isNew
                self.refreshTree(goTo, fallback: target)
                self.fillLargest()
                self.updateDriveLabel()
                self.statusLabel.stringValue = "\(result.name): \(Fmt.size(result.size)) in \(Fmt.count(result.files)) files and \(Fmt.count(result.dirs)) folders - scanned in \(String(format: "%.2f", Date().timeIntervalSince(started))) s" + (s.cancelled ? "  (stopped - partial results)" : "")
                self.onScanFinished?()
            }
        }
    }

    static func findByPath(_ root: DirNode, _ full: String) -> DirNode? {
        let rp = root.fullPath
        guard full.hasPrefix(rp) else { return nil }
        var node = root
        for part in full.dropFirst(rp.count).split(separator: "/") {
            guard let next = node.children.first(where: { $0.name == part }) else { break }
            node = next
        }
        return node
    }

    private func setScanning(_ on: Bool, _ text: String?) {
        scanning = on
        if on { scanBar.start(text ?? "Starting..."); startProgressTimer() }
        else { scanBar.stop(); if !busy { stopProgressTimer() } }
        layoutRoot()
        updateButtons()
        if !on && drivesChanged { DispatchQueue.main.async { self.volumesChanged() } }
    }

    private func setBusy(_ on: Bool) {
        busy = on
        if on { overlay.show("Starting..."); startProgressTimer() }
        else { overlay.hide(); if !scanning { stopProgressTimer() } }
        updateButtons()
        if !on && drivesChanged { DispatchQueue.main.async { self.volumesChanged() } }
    }

    private func startProgressTimer() {
        if progressTimer != nil { return }
        progressTimer = Timer.scheduledTimer(withTimeInterval: 0.15, repeats: true) { [weak self] _ in self?.updateProgress() }
    }
    private func stopProgressTimer() { progressTimer?.invalidate(); progressTimer = nil }

    private func updateProgress() {
        if let s = scanner {
            let bytes = s.bytesSeen
            let detail = "\(Fmt.count(s.filesSeen)) files, \(Fmt.count(s.dirsSeen)) folders  --  \(Fmt.shorten(s.current, 90))"
            if s.cancelled { scanBar.set("Cancelling... (finishing current folder)", detail, -1) }
            else if let r = scanRoot, r.isDrive, r.used > 0, MainWindowController.same(r.path, s.rootPath) {
                let f = min(0.99, Double(bytes) / Double(r.used))
                scanBar.set("Scanning \(r.path): \(Fmt.size(bytes)) of \(Fmt.size(r.used)) done (\(Int(f * 100)) %)", detail, f)
            } else { scanBar.set("Scanning \(Fmt.shorten(s.rootPath, 60)): \(Fmt.size(bytes)) so far", detail, -1) }
        }
        if let j = deleteJob { overlay.set("Deleting: \(Fmt.count(j.filesDeleted)) files removed", Fmt.shorten(j.current, 80)) }
    }

    private func updateButtons() {
        let idle = !busy && !scanning
        btnScan.isEnabled = idle
        btnBrowse.isEnabled = idle
        pathField.isEnabled = !busy
        btnUp.isEnabled = !busy && current?.parent != nil
        btnRescan.isEnabled = idle && current != nil
        btnFinder.isEnabled = !busy && (current != nil || active != nil)
        btnDelete.isEnabled = idle && current != nil
    }

    private func updateDriveLabel() {
        guard let r = active, r.total > 0 else { driveLabel.stringValue = ""; return }
        refreshCapacity(r)
        driveLabel.stringValue = "\(r.displayName)  \(Fmt.size(r.free)) free of \(Fmt.size(r.total)) (\(Int((100 * Double(r.free) / Double(max(1, r.total))).rounded()))% free)"
    }

    private func browse() {
        let p = NSOpenPanel()
        p.canChooseDirectories = true; p.canChooseFiles = false; p.allowsMultipleSelection = false
        p.message = "Choose a folder to analyze"
        p.beginSheetModal(for: window!) { resp in
            if resp == .OK, let u = p.url { self.scanPath(u.path) }
        }
    }

    // MARK: Tree

    func outlineView(_ outlineView: NSOutlineView, numberOfChildrenOfItem item: Any?) -> Int {
        if item == nil { return roots.count }
        if let r = item as? ScanRoot { return r.node?.children.count ?? 0 }
        if let d = item as? DirNode { return d.children.count }
        return 0
    }

    func outlineView(_ outlineView: NSOutlineView, child index: Int, ofItem item: Any?) -> Any {
        if item == nil { return roots[index] }
        if let r = item as? ScanRoot { return r.node!.children[index] }
        return (item as! DirNode).children[index]
    }

    func outlineView(_ outlineView: NSOutlineView, isItemExpandable item: Any) -> Bool {
        if let r = item as? ScanRoot { return !(r.node?.children.isEmpty ?? true) }
        if let d = item as? DirNode { return !d.children.isEmpty }
        return false
    }

    func outlineView(_ outlineView: NSOutlineView, rowViewForItem item: Any) -> NSTableRowView? { TreeRowView() }

    func outlineView(_ outlineView: NSOutlineView, viewFor tableColumn: NSTableColumn?, item: Any) -> NSView? {
        let c = (outlineView.makeView(withIdentifier: NSUserInterfaceItemIdentifier("treecell"), owner: nil) as? TreeCell) ?? {
            let n = TreeCell(); n.identifier = NSUserInterfaceItemIdentifier("treecell"); return n
        }()
        if let r = item as? ScanRoot {
            c.name = r.displayName + (r.node == nil ? (r.scanning ? "  (scanning...)" : "  (not scanned)") : (r.scanning ? "  (rescanning...)" : ""))
            c.dim = r.node == nil
            if let n = r.node {
                c.size = Fmt.size(n.size)
                c.bar = r.isDrive && r.total > 0 ? 100 * Double(n.size) / Double(r.total) : -1
            } else {
                c.size = r.total > 0 ? Fmt.size(r.used) : ""
                c.bar = r.total > 0 ? 100 * Double(r.used) / Double(r.total) : -1
            }
            c.pct = c.bar >= 0 ? Fmt.pct(c.bar) : ""
        } else if let d = item as? DirNode {
            c.name = d.denied ? d.name + "  [access denied]" : d.name
            c.dim = d.denied
            c.size = Fmt.size(d.size)
            let parentSize = d.parent?.size ?? 0
            c.bar = parentSize > 0 ? 100 * Double(d.size) / Double(parentSize) : 0
            c.pct = Fmt.pct(c.bar)
        }
        c.needsDisplay = true
        return c
    }

    func outlineViewSelectionDidChange(_ notification: Notification) {
        lastView = outline
        let item = outline.item(atRow: outline.selectedRow)
        if let r = item as? ScanRoot {
            if let n = r.node { showContents(n) } else { showUnscanned(r) }
        } else if let d = item as? DirNode { showContents(d) }
    }

    @objc private func treeDoubleClick() {
        if let r = outline.item(atRow: outline.clickedRow) as? ScanRoot, r.node == nil { scanPath(r.path) }
    }

    private func refreshTree(_ target: DirNode?, fallback: ScanRoot? = nil) {
        var expanded: [Any] = []
        for i in 0..<outline.numberOfRows { if let it = outline.item(atRow: i), outline.isItemExpanded(it) { expanded.append(it) } }
        outline.reloadData()
        for it in expanded {
            if let r = it as? ScanRoot, roots.contains(where: { $0 === r }) { outline.expandItem(r) }
            else if let d = it as? DirNode, rootOf(d.top) != nil { expandPath(to: d) }
        }
        if let t = target, rootOf(t.top) != nil { selectDir(t) }
        else if let f = fallback ?? active, roots.contains(where: { $0 === f }) { selectRoot(f) }
        else if let first = roots.first { selectRoot(first) }
    }

    private func expandPath(to d: DirNode) {
        var chain: [DirNode] = []
        var n: DirNode? = d
        while let c = n { chain.insert(c, at: 0); n = c.parent }
        guard let r = rootOf(chain.first) else { return }
        outline.expandItem(r)
        for c in chain.dropFirst() { outline.expandItem(c) }
    }

    private func selectRoot(_ r: ScanRoot) {
        let row = outline.row(forItem: r)
        if row < 0 { return }
        if outline.selectedRow == row { if let n = r.node { showContents(n) } else { showUnscanned(r) } }
        else { outline.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false) }
    }

    func selectDir(_ d: DirNode) {
        guard let r = rootOf(d.top) else { return }
        outline.expandItem(r)
        var chain: [DirNode] = []
        var n: DirNode? = d
        while let c = n, c.parent != nil { chain.insert(c, at: 0); n = c.parent }
        for c in chain.dropLast() { outline.expandItem(c) }
        let item: Any = d.parent == nil ? r : d
        let row = outline.row(forItem: item)
        if row < 0 { return }
        if outline.selectedRow == row { showContents(d) }
        else { outline.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false) }
        outline.scrollRowToVisible(row)
    }

    // MARK: Contents

    private func showUnscanned(_ r: ScanRoot) {
        current = nil
        active = r
        refreshCapacity(r)
        if !busy && !scanning { pathField.stringValue = r.path }
        contents.resetHover()
        rows = []
        contents.emptyText = r.scanning
            ? "Scanning \(r.displayName)...\nThe results appear here when the scan finishes. You can browse other drives meanwhile."
            : "\(r.displayName) has not been scanned yet.\nPress Scan, or double-click the drive, to analyze it."
        contents.reloadData()
        fillLargest()
        tabs.setTitle(0, "Folder contents: \(r.path)  (\(r.scanning ? "scanning..." : "not scanned"))")
        rowSlice = [:]
        let sub = r.total > 0 ? "\(Fmt.size(r.used)) used of \(Fmt.size(r.total))  -  \(Fmt.size(r.free)) free" : r.path
        chart.showLogo(r.displayName, sub, r.scanning ? "Scanning... the chart appears when the scan is done." : "Not scanned yet. Press Scan, or double-click the drive.")
        updateDriveLabel()
        updateButtons()
    }

    private func showContents(_ d: DirNode) {
        current = d
        if let r = rootOf(d.top), r !== active {
            active = r
            fillLargest()
            updateDriveLabel()
            if !busy && !scanning { pathField.stringValue = r.path }
        }
        contents.emptyText = nil
        var items: [Row] = d.children.map { c in
            Row(name: c.name, isDir: true, dir: c, size: c.size, files: c.files, dirs: c.dirs, percent: d.size > 0 ? 100 * Double(c.size) / Double(d.size) : 0, denied: c.denied)
        }
        var files = Scanner.listFiles(d)
        files.sort { $0.size > $1.size }
        for f in files.prefix(MainWindowController.maxFilesShown) {
            items.append(Row(name: f.name, isDir: false, file: f, size: f.size, files: 1, modified: f.modified, percent: d.size > 0 ? 100 * Double(f.size) / Double(d.size) : 0))
        }
        if files.count > MainWindowController.maxFilesShown {
            let rest = files.dropFirst(MainWindowController.maxFilesShown).reduce(Int64(0)) { $0 + $1.size }
            items.append(Row(name: "[\(Fmt.count(Int64(files.count - MainWindowController.maxFilesShown))) smaller files not listed]", isDir: false,
                             size: rest, files: Int64(files.count - MainWindowController.maxFilesShown), percent: d.size > 0 ? 100 * Double(rest) / Double(d.size) : 0, isSummary: true))
        }
        rows = sortRows(items, sortKey, sortAscending)
        contents.resetHover()
        contents.reloadData()
        tabs.setTitle(0, "Folder contents: \(Fmt.shorten(d.name, 40))  (\(Fmt.size(d.size)))")
        updateChart(d)
        updateButtons()
    }

    private func sortRows(_ items: [Row], _ key: String, _ asc: Bool) -> [Row] {
        items.sorted { a, b in
            if a.isSummary != b.isSummary { return b.isSummary }
            var r: ComparisonResult
            switch key {
            case "name": r = a.name.localizedStandardCompare(b.name)
            case "folder": r = a.folder.localizedStandardCompare(b.folder)
            case "files": r = a.files == b.files ? .orderedSame : (a.files < b.files ? .orderedAscending : .orderedDescending)
            case "dirs": r = a.dirs == b.dirs ? .orderedSame : (a.dirs < b.dirs ? .orderedAscending : .orderedDescending)
            case "mod":
                let x = a.modified ?? .distantPast, y = b.modified ?? .distantPast
                r = x == y ? .orderedSame : (x < y ? .orderedAscending : .orderedDescending)
            default: r = a.size == b.size ? .orderedSame : (a.size < b.size ? .orderedAscending : .orderedDescending)
            }
            if r == .orderedSame && key != "size" { return a.size > b.size }
            return asc ? r == .orderedAscending : r == .orderedDescending
        }
    }

    private func fillLargest() {
        let list = active?.top ?? []
        largestRows = sortRows(list.map { f in Row(name: f.name, isDir: false, file: f, size: f.size, files: 1, modified: f.modified, folder: f.dir.fullPath) }, largestSortKey, largestAscending)
        largest.reloadData()
    }

    // MARK: Chart

    private func updateChart(_ d: DirNode) {
        rowSlice = [:]
        let r = rootOf(d.top)
        if let r = r { refreshCapacity(r) }
        let folder = max(Int64(1), d.size)
        let ownIdx = Set(rows.indices.filter { rows[$0].size > 0 && Double(rows[$0].size) >= 0.015 * Double(folder) }
            .sorted { rows[$0].size > rows[$1].size }.prefix(12))
        var slices: [ChartSlice] = []
        var run: ChartSlice?
        var runs: [ChartSlice] = []
        var colour = 0
        for (i, row) in rows.enumerated() {
            if ownIdx.contains(i) {
                run = nil
                let s = ChartSlice(row.name, row.size, T.CHART[colour % T.CHART.count])
                colour += 1
                s.detail = Fmt.pct(100 * Double(row.size) / Double(folder)) + " of folder"
                s.dir = row.isDir ? row.dir : nil
                s.rows = [i]
                slices.append(s)
            } else {
                if run == nil { let n = ChartSlice("", 0, T.CHART_OTHER); slices.append(n); runs.append(n); run = n }
                run!.rows.append(i); run!.size += row.size
            }
            rowSlice[i] = slices.count - 1
        }
        for s in runs {
            s.label = s.rows.count == 1 ? rows[s.rows[0]].name : "\(s.rows.count) smaller items"
            s.detail = Fmt.pct(100 * Double(s.size) / Double(folder)) + " of folder"
        }
        var title = d.name, sub = d.fullPath
        if d.parent == nil, let r = r {
            title = r.displayName
            if r.isDrive && r.total > 0 {
                if r.used - d.size > 0 {
                    let s = ChartSlice("System / not readable", r.used - d.size, T.PILL)
                    s.detail = Fmt.pct(100 * Double(r.used - d.size) / Double(r.total)) + " of drive"
                    slices.append(s)
                }
                let f = ChartSlice("Free space", r.free, T.CHART_FREE)
                f.empty = true
                f.detail = Fmt.pct(100 * Double(r.free) / Double(r.total)) + " of drive"
                slices.append(f)
                sub = "\(Fmt.size(r.used)) used of \(Fmt.size(r.total))  -  \(Fmt.size(r.free)) free"
            }
        }
        chart.setData(title, sub, slices)
    }

    // MARK: Lists data source

    func numberOfRows(in tableView: NSTableView) -> Int { tableView === contents ? rows.count : largestRows.count }

    func tableView(_ tableView: NSTableView, rowViewForRow row: Int) -> NSTableRowView? {
        let v = ListRowView()
        v.table = tableView as? HoverTable
        v.index = row
        return v
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard let col = tableColumn else { return nil }
        let id = NSUserInterfaceItemIdentifier("cell-" + col.identifier.rawValue)
        let c = (tableView.makeView(withIdentifier: id, owner: nil) as? ListCell) ?? { let n = ListCell(); n.identifier = id; return n }()
        c.column = col.identifier.rawValue
        c.row = tableView === contents ? rows[row] : largestRows[row]
        c.needsDisplay = true
        return c
    }

    func tableView(_ tableView: NSTableView, didClick tableColumn: NSTableColumn) {
        let key = tableColumn.identifier.rawValue == "pct" ? "size" : tableColumn.identifier.rawValue
        if tableView === contents {
            if sortKey == key { sortAscending.toggle() } else { sortKey = key; sortAscending = key == "name" || key == "folder" }
            rows = sortRows(rows, sortKey, sortAscending)
            contents.resetHover()
            contents.reloadData()
            if let d = current { updateChart(d) }
        } else {
            if largestSortKey == key { largestAscending.toggle() } else { largestSortKey = key; largestAscending = key == "name" || key == "folder" }
            largestRows = sortRows(largestRows, largestSortKey, largestAscending)
            largest.reloadData()
        }
        for c in tableView.tableColumns {
            (c.headerCell as? DarkHeaderCell)?.active = c === tableColumn
            let base = (c.headerCell.stringValue as NSString).replacingOccurrences(of: "  ▲", with: "").replacingOccurrences(of: "  ▼", with: "")
            let asc = tableView === contents ? sortAscending : largestAscending
            c.headerCell.stringValue = c === tableColumn ? base + (asc ? "  ▲" : "  ▼") : base
        }
        tableView.headerView?.needsDisplay = true
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        if let t = notification.object as? NSTableView { lastView = t }
        updateButtons()
    }

    // MARK: Selection and actions

    private func selection() -> [Row] {
        if lastView === outline || lastView == nil {
            if let d = current { return [Row(name: d.name, isDir: true, dir: d, size: d.size)] }
            return []
        }
        if lastView === contents { return contents.selectedRowIndexes.compactMap { $0 < rows.count && !rows[$0].isSummary ? rows[$0] : nil } }
        if lastView === largest { return largest.selectedRowIndexes.compactMap { $0 < largestRows.count ? largestRows[$0] : nil } }
        return []
    }

    @objc private func listDoubleClick() { openSelected() }

    func openSelected() {
        let sel = selection()
        guard sel.count == 1, !busy else { return }
        let it = sel[0]
        if it.isDir, let d = it.dir { selectDir(d); return }
        if lastView === largest, let f = it.file, rootOf(f.dir.top) != nil {
            selectDir(f.dir)
            tabs.selectedIndex = 0
            if let i = rows.firstIndex(where: { !$0.isDir && $0.name == f.name }) {
                contents.selectRowIndexes(IndexSet(integer: i), byExtendingSelection: false)
                contents.scrollRowToVisible(i)
                window?.makeFirstResponder(contents)
            }
            return
        }
        showInFinder()
    }

    func goUp() { if !busy, let p = current?.parent { selectDir(p) } }

    func showInFinder() {
        let sel = selection()
        let paths = sel.isEmpty ? [current?.fullPath ?? active?.path].compactMap { $0 } : sel.map { $0.fullPath }
        NSWorkspace.shared.activateFileViewerSelecting(paths.map { URL(fileURLWithPath: $0) })
    }

    func copyPath() {
        let sel = selection()
        guard !sel.isEmpty else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(sel.map { $0.fullPath }.joined(separator: "\n"), forType: .string)
    }

    private func handleKey(_ e: NSEvent, in view: NSView) -> Bool {
        let cmd = e.modifierFlags.contains(.command)
        switch e.keyCode {
        case 36, 76:                                                     // Return / Enter
            if view === outline, let r = outline.item(atRow: outline.selectedRow) as? ScanRoot, r.node == nil { scanPath(r.path); return true }
            if view !== outline { openSelected(); return true }
        case 51 where cmd: deleteSelected(); return true                 // Cmd+Delete
        case 126 where cmd: goUp(); return true                         // Cmd+Up
        default: break
        }
        return false
    }

    // MARK: Context menu

    func menuNeedsUpdate(_ m: NSMenu) {
        m.removeAllItems()
        if let v = window?.firstResponder as? NSView, v === outline || v === contents || v === largest { lastView = v }
        let sel = selection()
        let any = !sel.isEmpty && !busy
        func add(_ title: String, _ key: String, _ enabled: Bool, _ action: Selector) {
            let it = NSMenuItem(title: title, action: action, keyEquivalent: key)
            if !key.isEmpty { it.keyEquivalentModifierMask = .command }
            it.target = self; it.isEnabled = enabled
            m.addItem(it)
        }
        m.autoenablesItems = false
        add("Open", "", any && sel.count == 1, #selector(menuOpen))
        add("Show in Finder", "", any, #selector(menuFinder))
        add("Copy path", "", any, #selector(menuCopy))
        add("Rescan folder", "r", any && !scanning && sel.count == 1 && sel[0].isDir, #selector(menuRescan))
        m.addItem(.separator())
        add("Delete permanently...", "\u{8}", any && !scanning, #selector(menuDelete))
    }

    @objc private func menuOpen() { openSelected() }
    @objc private func menuFinder() { showInFinder() }
    @objc private func menuCopy() { copyPath() }
    @objc private func menuRescan() { rescanSelected() }
    @objc private func menuDelete() { deleteSelected() }

    // MARK: Rescan and delete

    private func topList(_ d: DirNode) -> [FileEntry] { rootOf(d.top)?.top ?? [] }
    private func setTopList(_ d: DirNode, _ list: [FileEntry]) { rootOf(d.top)?.top = list }

    private func removeNode(_ dn: DirNode) {
        setTopList(dn, topList(dn).filter { !$0.dir.isUnder(dn) })
        guard let p = dn.parent else { return }
        p.children.removeAll { $0 === dn }
        var a: DirNode? = p
        while let x = a { x.size -= dn.size; x.files -= dn.files; x.dirs -= dn.dirs + 1; a = x.parent }
        var b: DirNode? = p
        while let x = b { x.children.sort { $0.size > $1.size }; b = x.parent }
        dn.parent = nil
    }

    private func replaceNode(_ old: DirNode, _ fresh: DirNode, _ freshTop: [FileEntry]) {
        var list = topList(old).filter { !$0.dir.isUnder(old) }
        if let p = old.parent {
            fresh.name = old.name
            fresh.parent = p
            if let i = p.children.firstIndex(where: { $0 === old }) { p.children[i] = fresh }
            let dS = fresh.size - old.size, dF = fresh.files - old.files, dD = fresh.dirs - old.dirs
            var a: DirNode? = p
            while let x = a { x.size += dS; x.files += dF; x.dirs += dD; x.children.sort { $0.size > $1.size }; a = x.parent }
            old.parent = nil
        } else if let r = rootOf(old) { r.node = fresh }
        list.append(contentsOf: freshTop)
        list.sort { $0.size > $1.size }
        setTopList(fresh, Array(list.prefix(MainWindowController.topCount)))
    }

    private func removeFile(_ f: FileEntry) {
        let d = f.dir
        guard rootOf(d.top) != nil else { return }
        d.localSize -= f.size; d.localFiles -= 1
        var a: DirNode? = d
        while let x = a { x.size -= f.size; x.files -= 1; x.children.sort { $0.size > $1.size }; a = x.parent }
        setTopList(d, topList(d).filter { !($0.dir === d && $0.name == f.name) })
    }

    func rescanSelected() {
        if busy || scanning { return }
        let sel = selection()
        guard let dir = sel.count == 1 && sel[0].isDir ? sel[0].dir : current else { return }
        if dir.parent == nil { scanPath(rootOf(dir)?.path ?? dir.fullPath); return }
        let path = dir.fullPath
        let s = Scanner(topCount: MainWindowController.topCount)
        scanner = s; scanRoot = rootOf(dir.top)
        setScanning(true, "Rescanning " + Fmt.shorten(path, 80) + "...")
        let started = Date()
        DispatchQueue.global(qos: .userInitiated).async {
            let fresh = s.run(path)
            DispatchQueue.main.async {
                self.scanner = nil; self.scanRoot = nil
                self.setScanning(false, nil)
                let inside = self.current.map { $0.isUnder(dir) } ?? false
                let here = inside ? self.current!.fullPath : nil
                var target = self.current
                if !FileManager.default.fileExists(atPath: path) { if inside { target = dir.parent }; self.removeNode(dir) }
                else { self.replaceNode(dir, fresh, s.topFiles); if inside { target = MainWindowController.findByPath(fresh, here!) ?? fresh } }
                if target == nil || self.rootOf(target!.top) == nil { target = dir.parent }
                self.current = nil
                self.refreshTree(target)
                self.fillLargest()
                self.updateDriveLabel()
                self.statusLabel.stringValue = "Rescanned \(path): \(Fmt.size(fresh.size)) in \(String(format: "%.2f", Date().timeIntervalSince(started))) s"
            }
        }
    }

    func deleteSelected() {
        if busy || scanning { return }
        let sel = selection()
        guard !sel.isEmpty else { return }
        if sel.contains(where: { $0.isDir && $0.dir?.parent == nil }) {
            alert("The scanned drive or folder itself cannot be deleted here.\nSelect items inside it instead.")
            return
        }
        let total = sel.reduce(Int64(0)) { $0 + $1.size }
        var list = sel.prefix(12).map { "    " + Fmt.shorten($0.fullPath, 80) + "   (" + Fmt.size($0.size) + ")" }.joined(separator: "\n")
        if sel.count > 12 { list += "\n    ... and \(sel.count - 12) more" }
        var msg = "\(list)\n\nThis bypasses the Trash and CANNOT be undone."
        let risky = ["/System", "/Library", "/Applications", "/usr", "/bin", "/sbin", "/private"]
        if sel.contains(where: { r in risky.contains { r.fullPath == $0 || r.fullPath.hasPrefix($0 + "/") } }) {
            msg += "\n\nWARNING: the selection includes macOS system, library or application content. Deleting it can break macOS or installed apps."
        }
        let a = NSAlert()
        a.alertStyle = .warning
        a.messageText = "Permanently delete \(sel.count) item\(sel.count == 1 ? "" : "s") (\(Fmt.size(total)))?"
        a.informativeText = msg
        a.addButton(withTitle: "Cancel")
        a.addButton(withTitle: "Delete permanently")
        a.buttons[1].hasDestructiveAction = true
        a.beginSheetModal(for: window!) { resp in
            if resp == .alertSecondButtonReturn { self.runDelete(sel) }
        }
    }

    private func runDelete(_ sel: [Row]) {
        var target = current
        for it in sel where it.isDir { if let d = it.dir, let c = current, c.isUnder(d) { target = d.parent } }
        let before = active?.node?.size ?? 0
        let job = DeleteJob()
        deleteJob = job
        setBusy(true)
        let paths = sel.map { $0.fullPath }
        DispatchQueue.global(qos: .userInitiated).async {
            for p in paths { job.delete(p) }
            var rescans: [(DirNode, DirNode, [FileEntry])] = []
            for (i, it) in sel.enumerated() where it.isDir && FileManager.default.fileExists(atPath: paths[i]) {
                let s = Scanner(topCount: MainWindowController.topCount)
                rescans.append((it.dir!, s.run(paths[i]), s.topFiles))
            }
            DispatchQueue.main.async {
                self.deleteJob = nil
                self.setBusy(false)
                for (i, it) in sel.enumerated() where !it.isDir && !FileManager.default.fileExists(atPath: paths[i]) { if let f = it.file { self.removeFile(f) } }
                for (i, it) in sel.enumerated() where it.isDir && !FileManager.default.fileExists(atPath: paths[i]) { if let d = it.dir, self.rootOf(d.top) != nil { self.removeNode(d) } }
                for (old, fresh, top) in rescans where self.rootOf(old.top) != nil {
                    self.replaceNode(old, fresh, top)
                    if target === old { target = fresh }
                }
                self.current = nil
                self.refreshTree(target.flatMap { self.rootOf($0.top) != nil ? $0 : nil } ?? self.active?.node)
                self.fillLargest()
                self.updateDriveLabel()
                let after = self.active?.node?.size ?? 0
                self.statusLabel.stringValue = "Freed \(Fmt.size(before - after)). \(Fmt.count(job.filesDeleted)) files deleted" + (job.errors.isEmpty ? "." : ", \(job.errors.count) items could not be deleted.")
                if !job.errors.isEmpty {
                    var err = job.errors.prefix(20).joined(separator: "\n")
                    if job.errors.count > 20 { err += "\n... and \(job.errors.count - 20) more" }
                    self.alert("Some items could not be deleted (in use, protected, or no permission -- Full Disk Access may help):\n\n" + err)
                }
            }
        }
    }

    private func alert(_ text: String) {
        let a = NSAlert()
        a.messageText = "Disk Space Analyzer"
        a.informativeText = text
        if let w = window { a.beginSheetModal(for: w) } else { a.runModal() }
    }

    func windowWillClose(_ notification: Notification) { scanner?.cancel(); NSApp.terminate(nil) }

    // MARK: Menu bar actions (validated)

    @objc func menuBarRescan(_ sender: Any?) { rescanSelected() }
    @objc func menuBarDelete(_ sender: Any?) { deleteSelected() }
    @objc func menuBarUp(_ sender: Any?) { goUp() }
    @objc func menuBarFinder(_ sender: Any?) { showInFinder() }
    @objc func menuBarScan(_ sender: Any?) { startScan() }
}

/// Chevron that opens the drive list next to the path field.
final class ChevronButton: NSView {
    var onPress: (() -> Void)?
    private var hover = false
    override var isFlipped: Bool { true }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self))
    }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .pointingHand) }
    override func mouseEntered(with event: NSEvent) { hover = true; needsDisplay = true }
    override func mouseExited(with event: NSEvent) { hover = false; needsDisplay = true }
    override func mouseDown(with event: NSEvent) { onPress?() }
    override func draw(_ dirtyRect: NSRect) {
        T.SURFACE.setFill(); bounds.fill()
        Theme.drawChevron(NSRect(x: (bounds.width - 14) / 2, y: (bounds.height - 14) / 2, width: 14, height: 14), hover ? T.ACCENT : T.MUTED, down: true)
    }
}

/// Full-window overlay for work that must not be interrupted (permanent delete).
final class BusyOverlay: FlatView {
    private let spinner = Spinner()
    private let status = NSTextField(labelWithString: "")
    private let detail = NSTextField(labelWithString: "")

    override init(frame: NSRect) {
        super.init(frame: frame)
        fill = T.BG
        spinner.background = T.BG
        status.font = Theme.font11; status.textColor = T.FG; status.alignment = .center
        detail.font = Theme.font9; detail.textColor = T.MUTED; detail.alignment = .center; detail.lineBreakMode = .byTruncatingMiddle
        addSubview(spinner); addSubview(status); addSubview(detail)
    }
    required init?(coder: NSCoder) { fatalError() }

    func show(_ text: String) { status.stringValue = text; detail.stringValue = ""; isHidden = false; spinner.spinning = true; needsLayout = true }
    func hide() { spinner.spinning = false; isHidden = true }
    func set(_ text: String, _ d: String) { status.stringValue = text; detail.stringValue = d }

    override func layout() {
        super.layout()
        let cx = bounds.midX, top = bounds.midY - 60
        spinner.frame = NSRect(x: cx - 32, y: top, width: 64, height: 64)
        status.frame = NSRect(x: cx - 220, y: top + 78, width: 440, height: 20)
        detail.frame = NSRect(x: cx - 220, y: top + 100, width: 440, height: 16)
    }

    override func hitTest(_ point: NSPoint) -> NSView? { isHidden ? nil : super.hitTest(point) ?? self }
    override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); needsLayout = true }
}
