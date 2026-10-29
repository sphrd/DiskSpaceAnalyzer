import AppKit

/// Text of the hover tip: surface fill, 1 px border, 9 pt fg text with 8 / 5 px padding.
final class HoverTipView: NSView {
    var text = "" { didSet { needsDisplay = true } }
    override var isFlipped: Bool { true }
    static let padX: CGFloat = 8, padY: CGFloat = 5

    static var attributes: [NSAttributedString.Key: Any] {
        let ps = NSMutableParagraphStyle()
        ps.lineBreakMode = .byCharWrapping
        return [.font: Theme.font9, .foregroundColor: T.FG, .paragraphStyle: ps]
    }

    static func size(_ s: String) -> NSSize {
        let r = (s as NSString).boundingRect(with: NSSize(width: 720, height: 2000), options: [.usesLineFragmentOrigin], attributes: attributes)
        return NSSize(width: ceil(r.width) + 2 * padX + 2, height: ceil(r.height) + 2 * padY)
    }

    override func draw(_ dirtyRect: NSRect) {
        Theme.fillRound(bounds, 5, fill: T.SURFACE, outline: T.BORDER)
        (text as NSString).draw(with: bounds.insetBy(dx: HoverTipView.padX, dy: HoverTipView.padY), options: [.usesLineFragmentOrigin],
                                attributes: HoverTipView.attributes)
    }
}

/// The hover tip of the lists and the tree: shown next to the cursor about 0.45 s after it rests on a new row, replaced
/// when it moves to another row, hidden when it leaves the list, scrolls or clicks. (The system tooltips of table
/// cells do not follow the rows reliably.)
final class HoverTip {
    static let shared = HoverTip()
    static let delay: TimeInterval = 0.45

    private var panel: NSPanel?
    private let view = HoverTipView()
    private var timer: Timer?
    private weak var owner: NSView?
    private var key = -1
    private var text: String?
    private(set) var point = NSPoint.zero        // last mouse position over the owner, in screen coordinates

    private init() {
        NotificationCenter.default.addObserver(forName: NSApplication.didResignActiveNotification, object: nil, queue: .main) { _ in
            HoverTip.shared.reset()
        }
    }

    var isShown: Bool { panel?.isVisible ?? false }
    var shownText: String? { isShown ? view.text : nil }
    var tipWindow: NSWindow? { panel }

    /// The mouse moved over row `key` of `owner` (-1 = no row); `text` is that row's tip.
    func track(_ owner: NSView, key: Int, text: String?, event: NSEvent) {
        if let w = owner.window { point = w.convertPoint(toScreen: event.locationInWindow) }
        if owner === self.owner && key == self.key { return }
        hide()
        self.owner = owner; self.key = key; self.text = text
        guard key >= 0, let t = text, !t.isEmpty else { return }
        timer = Timer.scheduledTimer(withTimeInterval: HoverTip.delay, repeats: false) { [weak self] _ in self?.fire() }
    }

    private func fire() {
        timer = nil
        guard let o = owner, let w = o.window, w.isVisible, !o.isHiddenOrHasHiddenAncestor, let t = text else { return }
        show(t, at: point)
    }

    /// Shows the tip 16 px right of and 20 px below the screen point, kept on its screen.
    func show(_ t: String, at p: NSPoint) {
        let size = HoverTipView.size(t)
        let screen = NSScreen.screens.first { NSMouseInRect(p, $0.frame, false) } ?? NSScreen.main
        let vf = screen?.visibleFrame ?? NSRect(x: -1e5, y: -1e5, width: 2e5, height: 2e5)
        var x = p.x + 16, y = p.y - 20 - size.height
        if x + size.width > vf.maxX { x = vf.maxX - size.width }
        if x < vf.minX { x = vf.minX }
        if y < vf.minY { y = p.y + 8 }
        if y + size.height > vf.maxY { y = vf.maxY - size.height }
        let pn = panel ?? makePanel()
        view.text = t
        pn.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: false)
        view.frame = NSRect(origin: .zero, size: size)
        view.needsDisplay = true
        pn.orderFrontRegardless()
    }

    private func makePanel() -> NSPanel {
        let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 10, height: 10), styleMask: [.borderless, .nonactivatingPanel],
                        backing: .buffered, defer: true)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.ignoresMouseEvents = true
        p.level = .popUpMenu
        p.isReleasedWhenClosed = false
        p.hidesOnDeactivate = false
        p.collectionBehavior = [.transient, .ignoresCycle, .canJoinAllSpaces]
        p.appearance = NSAppearance(named: .darkAqua)
        let host = NSView()
        host.addSubview(view)
        p.contentView = host
        panel = p
        return p
    }

    /// Hides the tip; it does not show again until the mouse rests on another row.
    func hide() {
        timer?.invalidate(); timer = nil
        panel?.orderOut(nil)
    }

    /// Hides the tip and forgets the row (the next mouse move over any row arms it again).
    func reset() { hide(); owner = nil; key = -1; text = nil }

    /// reset() when the tip belongs to v.
    func reset(owner v: NSView) { if owner === v { reset() } }
}

extension NSTableView {
    /// A synthetic mouse move to the middle of row `row`, 80 px from the left of the visible part (self-test, screenshots).
    func simulateMouseMove(row: Int, x: CGFloat = 80) {
        guard let w = window, row >= 0, row < numberOfRows else { return }
        scrollRowToVisible(row)
        let p = convert(NSPoint(x: visibleRect.minX + x, y: rect(ofRow: row).midY), to: nil)
        if let e = NSEvent.mouseEvent(with: .mouseMoved, location: p, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                      windowNumber: w.windowNumber, context: nil, eventNumber: 0, clickCount: 0, pressure: 0) {
            mouseMoved(with: e)
        }
    }
}
