import AppKit

/// A top-level entry of the tree: a drive (scanned or not) or a folder the user scanned.
final class ScanRoot {
    var path: String
    var isDrive = false, isSystem = false
    var label = ""
    var total: Int64 = 0, free: Int64 = 0
    var node: DirNode?
    var scanning = false
    var previous: PrevNode?            // the scan before this one (folder sizes only), for the Changes view
    var previousTime = Date.distantPast
    init(path: String) { self.path = path }
    var displayName: String { isDrive && !label.isEmpty ? label : path }
    var used: Int64 { max(0, total - free) }
}

// MARK: - Tree and header views

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

final class MainWindowController: NSWindowController, NSOutlineViewDataSource, NSOutlineViewDelegate, NSMenuDelegate,
    NSSplitViewDelegate, NSTextFieldDelegate, NSWindowDelegate {

    static let topCount = 1000
    static let maxFilesShown = 2000

    // Model
    var roots: [ScanRoot] = []
    var active: ScanRoot?
    var current: DirNode?
    var rowSlice: [Int: Int] = [:]
    var scanner: Scanner?
    var scanRoot: ScanRoot?
    var deleteJob: DeleteJob?
    var scanning = false
    var busy = false
    var drivesChanged = false
    weak var lastView: NSView?
    var progressTimer: Timer?
    var onScanFinished: (() -> Void)?
    var readers = 0                       // background tasks reading the scanned trees (the trees must not change meanwhile)
    var suppressTreeSelection = false

    // Toolbar and panes
    let root = FlatView()
    let pathLabel = NSTextField(labelWithString: "Location:")
    let pathPanel = ContainerPanel()
    let pathField = NSTextField()
    let drivesButton = ChevronButton()
    lazy var btnBrowse = RoundButton("Choose...", .secondary) { [unowned self] in self.browse() }
    lazy var btnScan = RoundButton("Scan", .primary) { [unowned self] in self.startScan() }
    lazy var btnAccess = RoundButton("Full Disk Access...", .secondary) { [unowned self] in self.openFullDiskAccess() }
    lazy var btnUp = RoundButton("Up", .secondary) { [unowned self] in self.goUp() }
    lazy var btnRescan = RoundButton("Rescan folder", .secondary) { [unowned self] in self.rescanSelected() }
    lazy var btnFinder = RoundButton("Show in Finder", .secondary) { [unowned self] in self.showInFinder() }
    lazy var btnAddList = RoundButton("Add to delete list", .secondary) { [unowned self] in self.addSelectionToBasket() }
    lazy var btnDelete = RoundButton("Delete permanently", .warn) { [unowned self] in self.deleteSelected() }
    lazy var btnExport = RoundButton("Export...", .secondary) { [unowned self] in self.exportScan() }
    let measure = Segmented(["Size", "Size on disk"])
    let scanBar = ScanBar()
    let split = NSSplitView()
    let split2 = NSSplitView()
    let rightPane = LayoutView()
    let treePanel = ContainerPanel()
    let treeHeader = TreeHeader()
    let outline = KeyOutline()
    let treeScroll = NSScrollView()
    let listPanel = ContainerPanel()
    let viewTabs = TabStrip(["Contents", "Files", "File types", "Duplicates", "Suggestions", "Changes", "Delete list"])
    let chartPanel = ContainerPanel()
    let chart = PieChart()
    let statusLabel = NSTextField(labelWithString: "Choose a drive or folder and press Scan.")
    let liveLabel = NSTextField(labelWithString: "")
    let driveLabel = NSTextField(labelWithString: "")
    lazy var versionLink = LinkLabel("v" + Updater.currentString) { [unowned self] in self.updates.check(manual: true) }
    let overlay = BusyOverlay()
    let contextMenu = NSMenu()
    lazy var updates = UpdateController(parent: window)
    var splitPositioned = false, split2Positioned = false

    // Views (tabs)
    var pages: [Page] = []
    var pContents: Page!, pFiles: Page!, pTypes: Page!, pDupes: Page!, pSuggest: Page!, pChanges: Page!, pBasket: Page!
    var contents: HoverTable { pContents.table }

    // Files view
    let fileSearch = SearchBox("Search names  (e.g. *.iso, invoice)")
    let fileType = ChoiceBox("", ["Any type"] + FileTypes.categories)
    let fileMinSize = ChoiceBox("", ["Any size", "1 MB or more", "10 MB or more", "100 MB or more", "1 GB or more"])
    let fileAge = ChoiceBox("", ["Any date", "Older than 3 months", "Older than 6 months", "Older than 1 year", "Older than 2 years", "Older than 5 years"])
    var filesGen = 0
    // File types view
    let typeGroup = ChoiceBox("Group by: ", ["Extension", "Category"])
    var typesGen = 0
    // Duplicates view
    lazy var btnFindDupes = RoundButton("Find duplicates", .primary) { [unowned self] in self.findDuplicates() }
    let dupeMin = ChoiceBox("", ["Files of 1 MB or more", "Files of 10 MB or more", "Files of 100 MB or more", "All files"])
    lazy var btnSelectExtra = RoundButton("Select extra copies", .secondary) { [unowned self] in self.selectExtraCopies() }
    var dupeFinder: DuplicateFinder?
    var dupeGroups: [[FileEntry]]?
    var dupeScope = ""
    var dupeTimer: Timer?
    var onDupesFinished: (() -> Void)?
    // Suggestions view
    lazy var btnLookAgain = RoundButton("Look again", .secondary) { [unowned self] in self.suggestions = nil; self.fillSuggestions() }
    var suggestGen = 0
    var suggestions: [Suggestion]?
    // Delete list
    var basket: [DeleteTarget] = []
    lazy var btnBasketRemove = RoundButton("Remove from list", .secondary) { [unowned self] in self.removeFromBasket() }
    lazy var btnBasketClear = RoundButton("Clear list", .secondary) { [unowned self] in self.basket.removeAll(); self.basketChanged() }
    lazy var btnBasketTrash = RoundButton("Move to Trash", .secondary) { [unowned self] in self.deleteBasket(trash: true) }
    lazy var btnBasketDelete = RoundButton("Delete permanently", .warn) { [unowned self] in self.deleteBasket(trash: false) }
    // Live updates
    var streams: [LiveStream] = []
    var fsPending: [String] = []
    var missed: [ScanRoot] = []
    var liveTimer: Timer?
    var ignoreUntil = Date.distantPast
    var lastLiveRefresh = Date.distantPast
    var livePending = false
    // Self-test (--cli selftest)
    var selfTestDir: String?
    var selfTestFailed = false

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

    func styleScroll(_ s: NSScrollView, _ doc: NSView) {
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
        btnAddList.tip = "Collect the selected items in the delete list; review them there, then delete them together (⇧⌘D)"
        btnDelete.tip = "Permanently delete the selected items, bypassing the Trash (⌘⌫)"
        btnExport.tip = "Save the scan as an HTML report or a CSV file"
        measure.tip = "Size: file lengths, as Finder shows them. Size on disk: the space really used (blocks, files not downloaded from iCloud)"
        measure.selectedIndex = DirNode.onDisk ? 1 : 0
        measure.onChange = { [unowned self] i in self.setMeasure(i == 1) }
        for v: NSView in [pathLabel, pathPanel, btnBrowse, btnScan, btnAccess, btnUp, btnRescan, btnFinder, btnAddList, btnDelete, btnExport, measure,
                          scanBar, split, statusLabel, liveLabel, driveLabel, versionLink] {
            root.addSubview(v)
        }
        for l in [statusLabel, driveLabel, liveLabel] { l.font = Theme.font9; l.textColor = T.MUTED; l.lineBreakMode = .byTruncatingTail }
        driveLabel.alignment = .right
        liveLabel.alignment = .right

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

        // Views above the list and the chart
        buildViews()
        for p in pages {
            listPanel.addSubview(p.root)
            p.table.menu = contextMenu
            p.table.target = self
            p.table.doubleAction = #selector(listDoubleClick(_:))
            let t = p.table
            p.table.onKey = { [unowned self] e in self.handleKey(e, in: t) }
            p.table.onHot = { [unowned self, unowned p] r in
                if self.activePage === p { self.chart.setHover(self.rowSlice[r] ?? -1) }
            }
            p.onSorted = { [unowned self] pg in if self.activePage === pg { self.refreshChart() } }
            p.onSelection = { [unowned self] pg in self.lastView = pg.table; self.updateButtons() }
        }
        pContents.root.isHidden = false
        listPanel.onLayout = { [unowned self] r in for p in self.pages { p.root.frame = r } }
        viewTabs.background = T.BG
        viewTabs.onChange = { [unowned self] _ in self.showPage() }

        // Chart
        chartPanel.addSubview(chart)
        chartPanel.onLayout = { [unowned self] r in self.chart.frame = r }
        chart.onHover = { [unowned self] i in
            self.activePage.table.setHighlight(i >= 0 && i < self.chart.slices.count ? self.chart.slices[i].rows : [])
        }
        chart.onClick = { [unowned self] s in if let d = s.dir, !self.busy, self.isAttached(d) { self.selectDir(d) } }
        chart.onCenterClick = { [unowned self] in if self.activePage === self.pContents { self.goUp() } }

        split2.isVertical = true
        split2.dividerStyle = .thin
        split2.delegate = self
        split2.addArrangedSubview(listPanel); split2.addArrangedSubview(chartPanel)
        split2.setHoldingPriority(NSLayoutConstraint.Priority(250), forSubviewAt: 0)
        split2.setHoldingPriority(NSLayoutConstraint.Priority(270), forSubviewAt: 1)
        rightPane.fill = T.BG
        rightPane.addSubview(viewTabs); rightPane.addSubview(split2)
        rightPane.onLayout = { [unowned self] r in
            self.viewTabs.frame = NSRect(x: 0, y: 0, width: r.width, height: 30)
            self.split2.frame = NSRect(x: 0, y: 36, width: r.width, height: max(50, r.height - 36))
            if !self.split2Positioned && r.width > 400 {
                self.split2Positioned = true
                let w = r.width
                let chartW = min(330, w * 0.38)
                self.split2.setPosition(w - chartW - self.split2.dividerThickness, ofDividerAt: 0)
            }
        }

        split.isVertical = true
        split.dividerStyle = .thin
        split.delegate = self
        split.addArrangedSubview(treePanel); split.addArrangedSubview(rightPane)
        split.setHoldingPriority(NSLayoutConstraint.Priority(260), forSubviewAt: 0)
        split.setHoldingPriority(NSLayoutConstraint.Priority(250), forSubviewAt: 1)

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
        updateLiveLabel()
    }

    @objc func layoutRoot() {
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
        for btn in [btnUp, btnRescan, btnFinder, btnAddList, btnDelete] {
            if btn === btnDelete { x += 10 }
            let p = btn.preferredSize
            btn.frame = NSRect(x: x, y: y, width: p.width, height: h)
            x += p.width + gap
        }
        let ms = measure.preferredSize, es = btnExport.preferredSize
        measure.frame = NSRect(x: b.width - m - ms.width, y: y, width: ms.width, height: h)
        btnExport.frame = NSRect(x: measure.frame.minX - 16 - es.width, y: y, width: es.width, height: h)
        y += h + 10
        if !scanBar.isHidden { scanBar.frame = NSRect(x: m, y: y, width: b.width - 2 * m, height: ScanBar.height); y += ScanBar.height }
        let statusH: CGFloat = 28
        split.frame = NSRect(x: m, y: y, width: b.width - 2 * m, height: max(100, b.height - y - statusH))
        layoutStatus()
        overlay.frame = b
        if !splitPositioned && split.frame.width > 300 {
            splitPositioned = true
            let w = split.frame.width
            split.setPosition(min(430, w * 0.30), ofDividerAt: 0)
        }
    }

    func layoutStatus() {
        let b = root.bounds, m: CGFloat = 15
        let sy = b.height - 28 + 6
        let vw = Theme.textWidth(versionLink.textValue, Theme.font9) + 4
        versionLink.frame = NSRect(x: b.width - m - vw, y: sy, width: vw, height: 16)
        let dw = min(b.width / 3, Theme.textWidth(driveLabel.stringValue, Theme.font9) + 6)
        driveLabel.frame = NSRect(x: versionLink.frame.minX - 20 - dw, y: sy, width: dw, height: 16)
        let lw = liveLabel.stringValue.isEmpty ? 0 : Theme.textWidth(liveLabel.stringValue, Theme.font9) + 6
        liveLabel.frame = NSRect(x: driveLabel.frame.minX - (lw > 0 ? 18 : 0) - lw, y: sy, width: lw, height: 16)
        statusLabel.frame = NSRect(x: m, y: sy, width: max(50, liveLabel.frame.minX - 16 - m), height: 16)
    }

    func splitView(_ splitView: NSSplitView, constrainMinCoordinate proposedMinimumPosition: CGFloat, ofSubviewAt dividerIndex: Int) -> CGFloat {
        splitView === split ? 180 : 200
    }
    func splitView(_ splitView: NSSplitView, constrainMaxCoordinate proposedMaximumPosition: CGFloat, ofSubviewAt dividerIndex: Int) -> CGFloat {
        splitView === split ? splitView.bounds.width - 420 : splitView.bounds.width - 180
    }
    func windowDidResize(_ notification: Notification) { layoutRoot() }

    // MARK: Start-up

    func start(initialPath: String?) {
        loadDrives()
        refreshTree(nil)
        if let p = initialPath { scanPath(p) } else { scanPath("/") }
        if selfTestDir == nil {
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in self?.updates.check(manual: false) }
        }
    }

    // MARK: Drives

    static func same(_ a: String, _ b: String) -> Bool { PathUtil.same(a, b) }

    func findRoot(_ path: String) -> ScanRoot? { roots.first { PathUtil.same($0.path, path) } }
    func rootOf(_ top: DirNode?) -> ScanRoot? { guard let t = top else { return nil }; return roots.first { $0.node === t } }
    func isAttached(_ d: DirNode?) -> Bool { guard let d = d else { return false }; return rootOf(d.top) != nil }

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

    func refreshCapacity(_ r: ScanRoot) { let c = MainWindowController.capacity(r.path); r.total = c.0; r.free = c.1 }

    private func loadDrives() {
        let keys: [URLResourceKey] = [.volumeNameKey, .volumeIsLocalKey, .volumeIsInternalKey, .volumeIsRemovableKey, .volumeIsEjectableKey, .volumeIsBrowsableKey]
        let urls = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: keys, options: [.skipHiddenVolumes]) ?? []
        var ready: [(String, URLResourceValues)] = []
        for u in urls {
            guard let v = try? u.resourceValues(forKeys: Set(keys)) else { continue }
            if v.volumeIsLocal == false || v.volumeIsBrowsable == false { continue }   // network shares are not listed
            ready.append((u.path, v))
        }
        roots.removeAll { r in r.isDrive && !ready.contains { PathUtil.same($0.0, r.path) } && !r.scanning }
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
        guard (obj.object as? NSTextField) === pathField else { return }
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
        if r == nil { let n = ScanRoot(path: path); refreshCapacity(n); roots.append(n); sortRoots(); r = n }
        let target = r!
        let s = Scanner()
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
                self.whenNoReaders {
                    target.scanning = false
                    self.scanner = nil; self.scanRoot = nil
                    self.setScanning(false, nil)
                    target.node = result
                    self.refreshCapacity(target)
                    self.rememberScan(target, result, complete: !s.cancelled)
                    self.startWatching(target)
                    var goTo: DirNode?
                    if let c = self.current, let old = oldNode, c.top === old { goTo = MainWindowController.findByPath(result, c.fullPath) ?? result }
                    else if let a = self.active, a !== target { goTo = self.current }
                    else { goTo = result }
                    if self.active === target { self.current = nil }
                    self.refreshTree(goTo, fallback: target)
                    self.viewsChanged()
                    self.updateDriveLabel()
                    self.statusLabel.stringValue = "\(result.name): \(Fmt.size(result.size)) in \(Fmt.count(result.files)) files and \(Fmt.count(result.dirs)) folders - scanned in \(String(format: "%.2f", Date().timeIntervalSince(started))) s" + (s.cancelled ? "  (stopped - partial results)" : "")
                    self.onScanFinished?()
                }
            }
        }
    }

    /// Runs f once no background task reads the scanned trees (they are only changed on the main thread).
    func whenNoReaders(_ f: @escaping () -> Void) {
        if readers == 0 { f(); return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in self?.whenNoReaders(f) }
    }

    /// Runs work on a background thread while the trees are held still, then done on the main thread.
    func readInBackground<R>(_ work: @escaping () -> R, _ done: @escaping (R) -> Void) {
        readers += 1
        DispatchQueue.global(qos: .userInitiated).async {
            let r = work()
            DispatchQueue.main.async { self.readers -= 1; done(r) }
        }
    }

    static func findByPath(_ root: DirNode, _ full: String) -> DirNode? {
        let rp = root.fullPath
        guard PathUtil.isWithin(full, rp) else { return nil }
        var node = root
        for part in full.dropFirst(rp.count).split(separator: "/") {
            guard let next = node.children.first(where: { $0.name == part }) else { break }
            node = next
        }
        return node
    }

    /// The deepest scanned folder holding path; with exact, only a folder whose path is exactly path.
    func findScanned(_ path: String, exact: Bool) -> DirNode? {
        for r in roots {
            guard let n = r.node, PathUtil.isWithin(path, n.fullPath) else { continue }
            if let d = MainWindowController.findByPath(n, path), !exact || PathUtil.same(d.fullPath, path) { return d }
        }
        return nil
    }

    func setScanning(_ on: Bool, _ text: String?) {
        scanning = on
        if on { scanBar.start(text ?? "Starting..."); startProgressTimer() }
        else { scanBar.stop(); if !busy { stopProgressTimer() } }
        layoutRoot()
        updateButtons()
        if !on && drivesChanged { DispatchQueue.main.async { self.volumesChanged() } }
    }

    func setBusy(_ on: Bool) {
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
            else if let r = scanRoot, r.isDrive, r.used > 0, PathUtil.same(r.path, s.rootPath) {
                let f = min(0.99, Double(bytes) / Double(r.used))
                scanBar.set("Scanning \(r.path): \(Fmt.size(bytes)) of \(Fmt.size(r.used)) done (\(Int(f * 100)) %)", detail, f)
            } else { scanBar.set("Scanning \(Fmt.shorten(s.rootPath, 60)): \(Fmt.size(bytes)) so far", detail, -1) }
        }
        if let j = deleteJob {
            let moved = j.recycled
            overlay.set(moved > 0 ? "Moving to the Trash: \(Fmt.count(moved)) items moved" : "Deleting: \(Fmt.count(j.filesDeleted)) files removed", Fmt.shorten(j.current, 80))
        }
    }

    func updateButtons() {
        let idle = !busy && !scanning
        let any = roots.contains { $0.node != nil }
        btnScan.isEnabled = idle
        btnBrowse.isEnabled = idle
        pathField.isEnabled = !busy
        btnUp.isEnabled = !busy && current?.parent != nil
        btnRescan.isEnabled = idle && current != nil
        btnFinder.isEnabled = !busy && (current != nil || active != nil)
        btnDelete.isEnabled = idle && any
        btnAddList.isEnabled = !busy && any
        btnExport.isEnabled = !busy && any
        measure.isEnabled = !busy
        if pBasket != nil {
            btnBasketRemove.isEnabled = !busy && !basket.isEmpty
            btnBasketClear.isEnabled = !busy && !basket.isEmpty
            btnBasketTrash.isEnabled = idle && !basket.isEmpty
            btnBasketDelete.isEnabled = idle && !basket.isEmpty
        }
    }

    func updateDriveLabel() {
        defer { layoutStatus() }
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
        if suppressTreeSelection { return }
        lastView = outline
        let item = outline.item(atRow: outline.selectedRow)
        if let r = item as? ScanRoot {
            if let n = r.node { showContents(n) } else { showUnscanned(r) }
        } else if let d = item as? DirNode { showContents(d) }
    }

    @objc private func treeDoubleClick() {
        if let r = outline.item(atRow: outline.clickedRow) as? ScanRoot, r.node == nil { scanPath(r.path) }
    }

    func refreshTree(_ target: DirNode?, fallback: ScanRoot? = nil) {
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

    /// Reloads the tree after live changes without touching the selection or the lists.
    func refreshTreeQuietly() {
        var expanded: [Any] = []
        for i in 0..<outline.numberOfRows { if let it = outline.item(atRow: i), outline.isItemExpanded(it) { expanded.append(it) } }
        let selected = outline.item(atRow: outline.selectedRow)
        suppressTreeSelection = true
        outline.reloadData()
        for it in expanded {
            if let r = it as? ScanRoot, roots.contains(where: { $0 === r }) { outline.expandItem(r) }
            else if let d = it as? DirNode, rootOf(d.top) != nil { expandPath(to: d) }
        }
        if let s = selected {
            let row = outline.row(forItem: s)
            if row >= 0 { outline.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false) }
        }
        suppressTreeSelection = false
    }

    private func expandPath(to d: DirNode) {
        var chain: [DirNode] = []
        var n: DirNode? = d
        while let c = n { chain.insert(c, at: 0); n = c.parent }
        guard let r = rootOf(chain.first) else { return }
        outline.expandItem(r)
        for c in chain.dropFirst() { outline.expandItem(c) }
    }

    func selectRoot(_ r: ScanRoot) {
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

    func showUnscanned(_ r: ScanRoot) {
        current = nil
        active = r
        refreshCapacity(r)
        if !busy && !scanning { pathField.stringValue = r.path }
        pContents.setRows([], r.scanning
            ? "Scanning \(r.displayName)...\nThe results appear here when the scan finishes. You can browse other drives meanwhile."
            : "\(r.displayName) has not been scanned yet.\nPress Scan, or double-click the drive, to analyze it.")
        viewsChanged()
        pContents.setCaption(r.displayName + (r.scanning ? "  -  scanning..." : "  -  not scanned"))
        if activePage === pContents { updateChartUnscanned(r) }
        updateDriveLabel()
        updateButtons()
    }

    func showContents(_ d: DirNode) {
        current = d
        if let r = rootOf(d.top), r !== active {
            active = r
            updateDriveLabel()
            if !busy && !scanning { pathField.stringValue = r.path }
        }
        var items: [ItemInfo] = d.children.map { ItemInfo.forDir($0, d.size) }
        var files = Scanner.listFiles(d)
        files.sort { $0.size > $1.size }
        let maxShown = MainWindowController.maxFilesShown
        for f in files.prefix(maxShown) { items.append(ItemInfo.forFile(f, d.size)) }
        if files.count > maxShown {
            let rest = files.dropFirst(maxShown).reduce(Int64(0)) { $0 + $1.size }
            let s = ItemInfo()
            s.isSummary = true
            s.name = "[\(Fmt.count(files.count - maxShown)) smaller files not listed]"
            s.size = rest; s.files = Int64(files.count - maxShown)
            s.percent = d.size > 0 ? 100 * Double(rest) / Double(d.size) : 0
            items.append(s)
        }
        setChanges(d, items)
        for it in items {
            it.cells = [it.name, Fmt.size(it.size), Fmt.pct(it.percent), changeText(it), Fmt.count(it.files), it.isDir ? Fmt.count(it.dirs) : "", Fmt.date(it.modified)]
            if it.isDir, let dn = it.dir, dn.denied { it.color = T.MUTED }
        }
        pContents.setRows(items, "")
        pContents.setCaption("\(Fmt.shorten(d.fullPath, 90))  -  \(Fmt.size(d.size)) in \(Fmt.count(d.files)) files and \(Fmt.count(d.dirs)) folders" + changeCaption(d))
        contentsChanged()
        updateButtons()
    }

    // MARK: Selection and actions

    /// The selected items of the tree or of the list that had the focus last.
    func selection() -> [ItemInfo] {
        if lastView === outline || lastView == nil {
            if outline.item(atRow: outline.selectedRow) is ScanRoot, current == nil { return [] }
            if let d = current { return [ItemInfo.forDir(d, d.parent?.size ?? d.size)] }
            return []
        }
        if let p = pages.first(where: { $0.table === lastView }) {
            return p.selectedItems.filter { !$0.isSummary && !$0.isGroup && ($0.canDelete || $0.fullPath != nil) }
        }
        return []
    }

    @objc private func listDoubleClick(_ sender: Any?) {
        guard let t = sender as? HoverTable, let p = pages.first(where: { $0.table === t }) else { return }
        lastView = t
        onListDoubleClick(p)
    }

    func openSelected() {
        let sel = selection()
        guard sel.count == 1, !busy else { return }
        let it = sel[0]
        if it.isDir, let d = it.dir { if isAttached(d) { selectDir(d) }; return }
        guard let f = it.file else { showInFinder(); return }
        if lastView !== contents && isAttached(f.dir) {
            selectDir(f.dir)
            viewTabs.selectedIndex = 0
            if let i = pContents.items.firstIndex(where: { $0.file != nil && $0.file!.name == f.name }) {
                contents.selectRowIndexes(IndexSet(integer: i), byExtendingSelection: false)
                contents.scrollRowToVisible(i)
                window?.makeFirstResponder(contents)
                lastView = contents
            }
            return
        }
        showInFinder()
    }

    func goUp() { if !busy, let p = current?.parent { selectDir(p) } }

    func showInFinder() {
        let sel = selection()
        let paths = sel.isEmpty ? [current?.fullPath ?? active?.path].compactMap { $0 } : sel.compactMap { $0.fullPath }
        NSWorkspace.shared.activateFileViewerSelecting(paths.map { URL(fileURLWithPath: $0) })
    }

    func copyPath() {
        let paths = selection().compactMap { $0.fullPath }
        guard !paths.isEmpty else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(paths.joined(separator: "\n"), forType: .string)
    }

    func handleKey(_ e: NSEvent, in view: NSView) -> Bool {
        let cmd = e.modifierFlags.contains(.command)
        switch e.keyCode {
        case 36, 76:                                                     // Return / Enter
            if view === outline, let r = outline.item(atRow: outline.selectedRow) as? ScanRoot, r.node == nil { scanPath(r.path); return true }
            if view !== outline { lastView = view; openSelected(); return true }
        case 51 where cmd: lastView = view; deleteSelected(); return true      // Cmd+Delete
        case 126 where cmd: goUp(); return true                         // Cmd+Up
        case 114: lastView = view; addSelectionToBasket(); return true  // Help / Insert key of full-size keyboards
        default: break
        }
        return false
    }

    // MARK: Context menu

    func menuNeedsUpdate(_ m: NSMenu) {
        m.removeAllItems()
        if let v = window?.firstResponder as? NSView, v === outline || pages.contains(where: { $0.table === v }) { lastView = v }
        let sel = selection()
        let any = !sel.isEmpty && !busy
        let deletable = any && sel.allSatisfy { $0.canDelete }
        func add(_ title: String, _ key: String, _ mods: NSEvent.ModifierFlags, _ enabled: Bool, _ action: Selector, warn: Bool = false) {
            let it = NSMenuItem(title: title, action: action, keyEquivalent: key)
            if !key.isEmpty { it.keyEquivalentModifierMask = mods }
            it.target = self; it.isEnabled = enabled
            if warn && enabled { it.attributedTitle = NSAttributedString(string: title, attributes: [.foregroundColor: T.WARN_FG, .font: NSFont.menuFont(ofSize: 0)]) }
            m.addItem(it)
        }
        m.autoenablesItems = false
        add("Open", "", [], any && sel.count == 1, #selector(menuOpen))
        add("Show in Finder", "", [], any, #selector(menuFinder))
        add("Copy path", "", [], any, #selector(menuCopy))
        add("Rescan folder", "r", .command, any && !scanning && sel.count == 1 && sel[0].isDir, #selector(menuRescan))
        m.addItem(.separator())
        if lastView !== pBasket.table { add("Add to delete list", "d", [.command, .shift], deletable, #selector(menuAddList)) }
        add("Move to Trash...", "", [], deletable && !scanning, #selector(menuTrash))
        add("Delete permanently...", "\u{8}", .command, deletable && !scanning, #selector(menuDelete), warn: true)
    }

    @objc private func menuOpen() { openSelected() }
    @objc private func menuFinder() { showInFinder() }
    @objc private func menuCopy() { copyPath() }
    @objc private func menuRescan() { rescanSelected() }
    @objc private func menuAddList() { addSelectionToBasket() }
    @objc private func menuTrash() { trashSelected() }
    @objc private func menuDelete() { deleteSelected() }

    // MARK: Model updates

    func removeNode(_ dn: DirNode) {
        guard let p = dn.parent else { return }
        p.children.removeAll { $0 === dn }
        p.addUp(-dn.bytes, -dn.alloc, -dn.files, -(dn.dirs + 1))
        dn.parent = nil
    }

    func replaceNode(_ old: DirNode, _ fresh: DirNode) {
        guard let p = old.parent else { if let r = rootOf(old) { r.node = fresh }; return }
        fresh.name = old.name
        fresh.modified = old.modified
        fresh.parent = p
        if let i = p.children.firstIndex(where: { $0 === old }) { p.children[i] = fresh }
        p.addUp(fresh.bytes - old.bytes, fresh.alloc - old.alloc, fresh.files - old.files, fresh.dirs - old.dirs)
        old.parent = nil
    }

    /// Switches every view between file size and size on disk.
    func setMeasure(_ onDisk: Bool) {
        if DirNode.onDisk == onDisk { return }
        if measure.selectedIndex != (onDisk ? 1 : 0) { measure.selectedIndex = onDisk ? 1 : 0; return }
        whenNoReaders {
            DirNode.onDisk = onDisk
            for r in self.roots { r.node?.resortAll() }
            let keep = self.current
            self.current = nil
            if let k = keep, self.isAttached(k) { self.refreshTree(k) } else if self.active != nil { self.refreshTree(nil) }
            self.viewsChanged()
            self.statusLabel.stringValue = onDisk
                ? "Showing size on disk: the space files really take (whole blocks; files not downloaded from iCloud take nothing; hard links count once)."
                : "Showing file size: the length of each file, as Finder's Size column shows it."
        }
    }

    func rescanSelected() {
        if busy || scanning { return }
        let sel = selection()
        guard let dir = sel.count == 1 && sel[0].isDir ? sel[0].dir : current else { return }
        if dir.parent == nil { scanPath(rootOf(dir)?.path ?? dir.fullPath); return }
        let path = dir.fullPath
        let s = Scanner()
        scanner = s; scanRoot = rootOf(dir.top)
        setScanning(true, "Rescanning " + Fmt.shorten(path, 80) + "...")
        let started = Date()
        DispatchQueue.global(qos: .userInitiated).async {
            let fresh = s.run(path)
            DispatchQueue.main.async {
                self.whenNoReaders {
                    self.scanner = nil; self.scanRoot = nil
                    self.setScanning(false, nil)
                    let inside = self.current.map { $0.isUnder(dir) } ?? false
                    let here = inside ? self.current!.fullPath : nil
                    var target = self.current
                    if !FileManager.default.fileExists(atPath: path) { if inside { target = dir.parent }; self.removeNode(dir) }
                    else { self.replaceNode(dir, fresh); if inside { target = MainWindowController.findByPath(fresh, here!) ?? fresh } }
                    if target == nil || self.rootOf(target!.top) == nil { target = dir.parent }
                    self.ignoreOwnChanges()
                    self.current = nil
                    self.refreshTree(target)
                    self.viewsChanged()
                    self.updateDriveLabel()
                    self.statusLabel.stringValue = "Rescanned \(path): \(Fmt.size(fresh.size)) in \(String(format: "%.2f", Date().timeIntervalSince(started))) s"
                }
            }
        }
    }

    func alert(_ text: String) {
        let a = NSAlert()
        a.messageText = "Disk Space Analyzer"
        a.informativeText = text
        if let w = window { a.beginSheetModal(for: w) } else { a.runModal() }
    }

    func windowWillClose(_ notification: Notification) { scanner?.cancel(); stopAllWatching(); NSApp.terminate(nil) }

    // MARK: Menu bar actions (validated)

    @objc func menuBarRescan(_ sender: Any?) { rescanSelected() }
    @objc func menuBarDelete(_ sender: Any?) { deleteSelected() }
    @objc func menuBarTrash(_ sender: Any?) { trashSelected() }
    @objc func menuBarAddList(_ sender: Any?) { addSelectionToBasket() }
    @objc func menuBarUp(_ sender: Any?) { goUp() }
    @objc func menuBarFinder(_ sender: Any?) { showInFinder() }
    @objc func menuBarScan(_ sender: Any?) { startScan() }
    @objc func menuBarFind(_ sender: Any?) { showFilesSearch() }
    @objc func menuBarExport(_ sender: Any?) { exportScan() }
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

/// Full-window overlay for work that must not be interrupted (deleting).
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
