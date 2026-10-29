import AppKit

/// Anything an ActionBar can lay out.
protocol BarItem: NSView {
    var preferredSize: NSSize { get }
}

extension RoundButton: BarItem {}

/// Two or more options in one rounded field; the chosen one is an accent pill (e.g. Size | Size on disk).
final class Segmented: NSView, BarItem {
    private let options: [String]
    private var hover = -1
    var onChange: ((Int) -> Void)?
    var isEnabled = true { didSet { needsDisplay = true } }
    var backFill: NSColor = T.BG
    var tip: String? { didSet { toolTip = tip } }
    var selectedIndex = 0 {
        didSet { if oldValue != selectedIndex { needsDisplay = true; onChange?(selectedIndex) } }
    }
    override var isFlipped: Bool { true }

    init(_ options: [String]) { self.options = options; super.init(frame: .zero) }
    required init?(coder: NSCoder) { fatalError() }

    private func optionWidth(_ i: Int) -> CGFloat { Theme.textWidth(options[i], Theme.font) + 26 }

    var preferredSize: NSSize {
        NSSize(width: 6 + options.indices.reduce(CGFloat(0)) { $0 + optionWidth($1) }, height: ceil(Theme.font.ascender - Theme.font.descender) + 14)
    }

    private func optionRect(_ i: Int) -> NSRect {
        var x: CGFloat = 3
        for k in 0..<i { x += optionWidth(k) }
        return NSRect(x: x, y: 3, width: optionWidth(i), height: bounds.height - 6)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self))
    }
    override func resetCursorRects() { if isEnabled { addCursorRect(bounds, cursor: .pointingHand) } }

    private func hit(_ e: NSEvent) -> Int {
        let p = convert(e.locationInWindow, from: nil)
        return options.indices.first { optionRect($0).contains(p) } ?? -1
    }
    override func mouseMoved(with event: NSEvent) { let h = hit(event); if h != hover { hover = h; needsDisplay = true } }
    override func mouseExited(with event: NSEvent) { hover = -1; needsDisplay = true }
    override func mouseDown(with event: NSEvent) { let h = hit(event); if h >= 0 && isEnabled { selectedIndex = h } }

    override func draw(_ dirtyRect: NSRect) {
        backFill.setFill(); bounds.fill()
        Theme.fillRound(bounds, 8, fill: T.SURFACE, outline: T.BORDER)
        for i in options.indices {
            let r = optionRect(i)
            let fore: NSColor = !isEnabled ? T.DISABLED_FG : i == selectedIndex ? T.BG : i == hover ? T.FG : T.MUTED
            if i == selectedIndex { Theme.fillRound(r, 6, fill: isEnabled ? T.ACCENT : T.DISABLED_BG) }
            Theme.drawText(options[i], in: r, font: Theme.font, color: fore, align: .center)
        }
    }
}

/// Text field in a rounded SURFACE field with a muted placeholder; onChange fires 300 ms after typing pauses (or at
/// once on Return). Esc clears the text.
final class SearchBox: PanelView, BarItem, NSTextFieldDelegate {
    let field = NSTextField()
    var onChange: (() -> Void)?
    private var timer: Timer?

    init(_ placeholder: String) {
        super.init(frame: .zero)
        radius = 8
        backFill = T.SURFACE
        field.isBordered = false; field.drawsBackground = true; field.backgroundColor = T.SURFACE
        field.textColor = T.FG; field.font = Theme.font; field.focusRingType = .none
        field.placeholderAttributedString = NSAttributedString(string: placeholder, attributes: [.foregroundColor: T.MUTED, .font: Theme.font])
        field.cell?.isScrollable = true
        field.cell?.wraps = false
        field.delegate = self
        addSubview(field)
    }
    required init?(coder: NSCoder) { fatalError() }

    var text: String {
        get { field.stringValue }
        set { field.stringValue = newValue; timer?.invalidate(); timer = nil }
    }

    var preferredSize: NSSize { NSSize(width: 220, height: ceil(Theme.font.ascender - Theme.font.descender) + 14) }

    override func layout() {
        super.layout()
        let h: CGFloat = 18
        field.frame = NSRect(x: 10, y: (bounds.height - h) / 2, width: max(10, bounds.width - 20), height: h)
    }
    override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); needsLayout = true }
    override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(field) }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .iBeam) }

    func focus() {
        window?.makeFirstResponder(field)
        field.currentEditor()?.selectAll(nil)
    }

    private func fire() { timer?.invalidate(); timer = nil; onChange?() }

    func controlTextDidChange(_ obj: Notification) {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.3, repeats: false) { [weak self] _ in self?.fire() }
    }
    func controlTextDidBeginEditing(_ obj: Notification) { highlight = true }
    func controlTextDidEndEditing(_ obj: Notification) { highlight = false }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.cancelOperation(_:)) {
            if !field.stringValue.isEmpty { field.stringValue = ""; fire() }
            return true
        }
        if commandSelector == #selector(NSResponder.insertNewline(_:)) { fire(); return true }
        return false
    }
}

/// Drop-down choice: the current choice and a chevron in a rounded field; a click opens the list.
final class ChoiceBox: PanelView, BarItem {
    private let choices: [String]
    private let prefix: String
    private var hover = false
    var onChange: ((Int) -> Void)?
    var isEnabled = true { didSet { needsDisplay = true } }
    var tip: String? { didSet { toolTip = tip } }
    var selectedIndex = 0 {
        didSet { if oldValue != selectedIndex { needsDisplay = true; onChange?(selectedIndex) } }
    }

    init(_ prefix: String, _ choices: [String]) {
        self.prefix = prefix; self.choices = choices
        super.init(frame: .zero)
        radius = 8
        backFill = T.SURFACE
    }
    required init?(coder: NSCoder) { fatalError() }

    var preferredSize: NSSize {
        let w = choices.map { Theme.textWidth(prefix + $0, Theme.font) }.max() ?? 40
        return NSSize(width: w + 46, height: ceil(Theme.font.ascender - Theme.font.descender) + 14)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self))
    }
    override func resetCursorRects() { if isEnabled { addCursorRect(bounds, cursor: .pointingHand) } }
    override func mouseEntered(with event: NSEvent) { hover = true; needsDisplay = true }
    override func mouseExited(with event: NSEvent) { hover = false; needsDisplay = true }

    override func mouseDown(with event: NSEvent) {
        guard isEnabled else { return }
        let m = NSMenu()
        m.appearance = NSAppearance(named: .darkAqua)
        m.autoenablesItems = false
        for (i, c) in choices.enumerated() {
            let it = NSMenuItem(title: c, action: #selector(pick(_:)), keyEquivalent: "")
            it.target = self
            it.tag = i
            it.state = i == selectedIndex ? .on : .off
            m.addItem(it)
        }
        m.minimumWidth = bounds.width
        highlight = true
        m.popUp(positioning: nil, at: NSPoint(x: 0, y: bounds.height + 2), in: self)
        highlight = false
    }

    @objc private func pick(_ sender: NSMenuItem) { selectedIndex = sender.tag }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        Theme.drawText(prefix + choices[selectedIndex], in: NSRect(x: 10, y: 0, width: bounds.width - 36, height: bounds.height),
                       font: Theme.font, color: isEnabled ? T.FG : T.DISABLED_FG)
        Theme.drawChevron(NSRect(x: bounds.width - 24, y: (bounds.height - 12) / 2, width: 12, height: 12),
                          hover || highlight ? T.ACCENT : T.MUTED, down: true)
    }
}

/// A row of filter or action controls above a list; items in `right` sit at the right end. When the pane is too narrow
/// the controls wrap onto further rows; a search box takes the spare width of its row (and the first row to itself).
final class ActionBar: NSView {
    private(set) var items: [BarItem] = []
    private var right = Set<ObjectIdentifier>()
    private(set) var barHeight: CGFloat = 0
    var minRowHeight: CGFloat = 30
    var onHeightChange: (() -> Void)?
    override var isFlipped: Bool { true }

    init(_ items: [BarItem], right: [BarItem] = []) {
        super.init(frame: .zero)
        self.items = items
        for r in right { self.right.insert(ObjectIdentifier(r)) }
        for i in items { addSubview(i) }
    }
    required init?(coder: NSCoder) { fatalError() }

    private func isRight(_ v: BarItem) -> Bool { right.contains(ObjectIdentifier(v)) }
    private func pref(_ v: BarItem) -> CGFloat { v.preferredSize.width }

    /// Lays the items out for a width and returns the height needed.
    @discardableResult
    func arrange(width: CGFloat) -> CGFloat {
        let gap: CGFloat = 6, W = max(100, width)
        var h: CGFloat = minRowHeight
        for i in items where !i.isHidden { h = max(h, i.preferredSize.height) }
        let leftItems = items.filter { !$0.isHidden && !isRight($0) }
        let rightItems = items.filter { !$0.isHidden && isRight($0) }
        let leftW: CGFloat = leftItems.reduce(CGFloat(0)) { $0 + pref($1) + gap }
        let allRightW: CGFloat = rightItems.reduce(CGFloat(0)) { $0 + pref($1) + gap }
        let oneRow = leftW + allRightW <= W
        var rows: [[BarItem]] = [[]]
        var x: CGFloat = 0
        for c in leftItems {
            if !oneRow && c is SearchBox && rows[0].isEmpty { rows[0].append(c); rows.append([]); x = 0; continue }
            let w = pref(c)
            if !rows[rows.count - 1].isEmpty && x + w > W { rows.append([]); x = 0 }
            rows[rows.count - 1].append(c)
            x += w + gap
        }
        let rightW = rightItems.reduce(CGFloat(0)) { $0 + pref($1) + gap } - (rightItems.isEmpty ? 0 : gap)
        let rightOwnRow = !rightItems.isEmpty && !rows[rows.count - 1].isEmpty && x + rightW > W
        var y: CGFloat = 0
        for (i, row) in rows.enumerated() {
            let avail = W - (i == rows.count - 1 && !rightItems.isEmpty && !rightOwnRow ? rightW + 16 : 0)
            let used = row.reduce(CGFloat(0)) { $0 + pref($1) + gap } - (row.isEmpty ? 0 : gap)
            let extra = max(0, avail - used)
            x = 0
            for c in row {
                let w = pref(c) + (c is SearchBox ? extra : 0)
                c.frame = NSRect(x: x, y: y, width: w, height: h)
                x += w + gap
            }
            if i < rows.count - 1 || rightOwnRow { y += h + gap }
        }
        // The right-hand items, right-aligned; on rows of their own they wrap when they do not fit side by side.
        var rightRows: [[BarItem]] = [[]]
        var rw: CGFloat = 0
        for c in rightItems {
            let w = pref(c)
            if !rightRows[rightRows.count - 1].isEmpty && rw + w > W { rightRows.append([]); rw = 0 }
            rightRows[rightRows.count - 1].append(c)
            rw += w + gap
        }
        for (i, row) in rightRows.enumerated() {
            if i > 0 { y += h + gap }
            x = W
            for c in row.reversed() {
                let w = pref(c)
                x -= w
                c.frame = NSRect(x: max(0, x), y: y, width: w, height: h)
                x -= gap
            }
        }
        let total = y + h + 4
        if total != barHeight { barHeight = total }
        return total
    }

    /// Re-lays out after an item changed its text or visibility; tells the owner when the height changed.
    func relayout() {
        let old = barHeight
        arrange(width: bounds.width)
        if barHeight != old { onHeightChange?() }
        needsDisplay = true
    }
}
