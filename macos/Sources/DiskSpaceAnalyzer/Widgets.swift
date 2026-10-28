import AppKit

enum ButtonKind { case primary, secondary, warn }

/// Flat rounded button (radius 8). The action fires on mouse-up inside the button.
final class RoundButton: NSView {
    var title: String { didSet { invalidateIntrinsicContentSize(); needsDisplay = true } }
    var kind: ButtonKind { didSet { needsDisplay = true } }
    var font: NSFont = Theme.font { didSet { invalidateIntrinsicContentSize(); needsDisplay = true } }
    var minChars = 0
    var isEnabled = true { didSet { needsDisplay = true; window?.invalidateCursorRects(for: self) } }
    var onClick: (() -> Void)?
    var tip: String? { didSet { toolTip = tip } }
    private var hover = false, pressed = false

    init(_ title: String, _ kind: ButtonKind, _ action: (() -> Void)? = nil) {
        self.title = title; self.kind = kind; self.onClick = action
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }

    var preferredSize: NSSize {
        let sample = minChars > 0 ? String(repeating: "0", count: minChars) : title
        return NSSize(width: max(Theme.textWidth(sample, font), Theme.textWidth(title, font)) + 32,
                      height: ceil(font.ascender - font.descender) + 14)
    }
    override var intrinsicContentSize: NSSize { preferredSize }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self))
    }
    override func resetCursorRects() { if isEnabled { addCursorRect(bounds, cursor: .pointingHand) } }
    override func mouseEntered(with event: NSEvent) { hover = true; needsDisplay = true }
    override func mouseExited(with event: NSEvent) { hover = false; needsDisplay = true }
    override func mouseDown(with event: NSEvent) { if isEnabled { pressed = true } }
    override func mouseUp(with event: NSEvent) {
        let inside = bounds.contains(convert(event.locationInWindow, from: nil))
        let fire = pressed && isEnabled && inside
        pressed = false
        if fire { onClick?() }
    }

    override func draw(_ dirtyRect: NSRect) {
        let fill: NSColor, fore: NSColor
        if !isEnabled { fill = T.DISABLED_BG; fore = T.DISABLED_FG }
        else {
            switch kind {
            case .primary: fill = hover ? T.ACCENT_ACTIVE : T.ACCENT; fore = T.BG
            case .warn: fill = hover ? T.WARN_FG : T.WARN; fore = T.BG
            case .secondary: fill = hover ? T.SECONDARY_HOVER : T.BORDER; fore = T.FG
            }
        }
        Theme.fillRound(bounds, 8, fill: fill)
        Theme.drawText(title, in: bounds.insetBy(dx: 8, dy: 0), font: font, color: fore, align: .center)
    }
}

/// Accent arc spinner: 120 degrees, rotating 18 degrees every 40 ms.
final class Spinner: NSView {
    private var timer: Timer?
    private var angle: CGFloat = 0
    var background: NSColor = T.SURFACE
    override var isFlipped: Bool { true }

    var spinning: Bool {
        get { timer != nil }
        set {
            if newValue == spinning { return }
            if newValue {
                timer = Timer.scheduledTimer(withTimeInterval: 0.04, repeats: true) { [weak self] _ in
                    guard let s = self else { return }
                    s.angle = (s.angle + 18).truncatingRemainder(dividingBy: 360); s.needsDisplay = true
                }
            } else { timer?.invalidate(); timer = nil }
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        background.setFill(); bounds.fill()
        let w = max(2, 6 * bounds.width / 64), inset = 4 * bounds.width / 64 + w / 2
        let r = bounds.insetBy(dx: inset, dy: inset)
        let p = NSBezierPath()
        p.appendArc(withCenter: NSPoint(x: r.midX, y: r.midY), radius: r.width / 2, startAngle: angle, endAngle: angle + 120, clockwise: false)
        p.lineWidth = w; p.lineCapStyle = .round
        T.ACCENT.setStroke(); p.stroke()
    }
}

/// Non-blocking progress strip under the toolbar: spinner, status line, detail line, pill bar (when the total is
/// known) and Cancel. The rest of the window stays usable while it shows.
final class ScanBar: NSView {
    let spinner = Spinner()
    let cancelButton = RoundButton("Cancel", .secondary)
    private var status = "", detail = ""
    private var fraction: Double = -1
    override var isFlipped: Bool { true }

    override init(frame: NSRect) {
        super.init(frame: frame)
        cancelButton.minChars = 8
        addSubview(spinner); addSubview(cancelButton)
        isHidden = true
    }
    required init?(coder: NSCoder) { fatalError() }

    static let height: CGFloat = 64
    override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); needsLayout = true }

    func start(_ text: String) {
        status = text; detail = ""; fraction = -1
        cancelButton.isEnabled = true
        isHidden = false
        spinner.spinning = true
        needsLayout = true; needsDisplay = true
    }

    func stop() { spinner.spinning = false; isHidden = true }

    func set(_ text: String, _ detailText: String, _ frac: Double) {
        status = text; detail = detailText; fraction = frac
        needsDisplay = true
    }

    private var box: NSRect { NSRect(x: 0, y: 0, width: bounds.width, height: bounds.height - 10) }

    override func layout() {
        super.layout()
        let b = box
        spinner.frame = NSRect(x: b.minX + 14, y: b.minY + (b.height - 22) / 2, width: 22, height: 22)
        let cs = cancelButton.preferredSize
        cancelButton.frame = NSRect(x: b.maxX - 12 - cs.width, y: b.minY + (b.height - cs.height) / 2, width: cs.width, height: cs.height)
    }

    override func draw(_ dirtyRect: NSRect) {
        T.BG.setFill(); bounds.fill()
        let b = box
        Theme.fillRound(b, 12, fill: T.SURFACE, outline: T.BORDER)
        let x = spinner.frame.maxX + 12, right = cancelButton.frame.minX - 16
        let lineH: CGFloat = 17, smallH: CGFloat = 15, barH: CGFloat = 6
        let total = lineH + 2 + smallH + (fraction >= 0 ? 6 + barH : 0)
        var y = b.minY + (b.height - total) / 2
        Theme.drawText(status, in: NSRect(x: x, y: y, width: max(0, right - x), height: lineH), font: Theme.font, color: T.FG)
        y += lineH + 2
        Theme.drawText(detail, in: NSRect(x: x, y: y, width: max(0, right - x), height: smallH), font: Theme.font9, color: T.MUTED)
        if fraction >= 0 {
            y += smallH + 6
            let bar = NSRect(x: x, y: y, width: max(0, right - x), height: barH)
            Theme.fillRound(bar, barH / 2, fill: T.BORDER)
            let w = (bar.width * CGFloat(min(1, fraction))).rounded()
            if w > 0 { Theme.fillRound(NSRect(x: bar.minX, y: bar.minY, width: max(w, barH), height: barH), barH / 2, fill: T.ACCENT) }
        }
    }
}

/// Text tabs: the current one in accent with an underline pill, the others muted.
final class TabStrip: NSView {
    private var titles: [String]
    private var rects: [NSRect] = []
    private var hover = -1
    var selectedIndex = 0 { didSet { if oldValue != selectedIndex { needsDisplay = true; onChange?(selectedIndex) } } }
    var onChange: ((Int) -> Void)?
    var background: NSColor = T.SURFACE
    override var isFlipped: Bool { true }

    init(_ titles: [String]) { self.titles = titles; super.init(frame: .zero) }
    required init?(coder: NSCoder) { fatalError() }

    func setTitle(_ i: Int, _ t: String) { titles[i] = t; needsDisplay = true }
    override func resetCursorRects() { measure(); for r in rects { addCursorRect(r, cursor: .pointingHand) } }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self))
    }

    private func measure() {
        rects = []
        var x: CGFloat = 0
        for t in titles {
            let w = min(Theme.textWidth(t, Theme.font) + 12, max(40, bounds.width - x))
            rects.append(NSRect(x: x, y: 0, width: w, height: bounds.height))
            x += w + 10
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        background.setFill(); bounds.fill()
        measure()
        for (i, r) in rects.enumerated() {
            let c = i == selectedIndex ? T.ACCENT : i == hover ? T.FG : T.MUTED
            Theme.drawText(titles[i], in: NSRect(x: r.minX, y: r.minY, width: r.width, height: r.height - 4), font: Theme.font, color: c, align: .center)
            if i == selectedIndex { Theme.fillRound(NSRect(x: r.minX + 6, y: r.maxY - 4, width: r.width - 12, height: 3), 1.5, fill: T.ACCENT) }
        }
    }

    private func hit(_ e: NSEvent) -> Int {
        let p = convert(e.locationInWindow, from: nil)
        return rects.firstIndex { $0.contains(p) } ?? -1
    }
    override func mouseMoved(with event: NSEvent) { let h = hit(event); if h != hover { hover = h; needsDisplay = true } }
    override func mouseExited(with event: NSEvent) { hover = -1; needsDisplay = true }
    override func mouseDown(with event: NSEvent) { let h = hit(event); if h >= 0 { selectedIndex = h } }
}
