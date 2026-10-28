import AppKit

final class ChartSlice {
    var label = "", detail = ""
    var size: Int64 = 0
    var color: NSColor = T.ACCENT
    var dir: DirNode?           // folder opened on click (single-folder slices only)
    var empty = false           // free space: nothing drawn
    var rows: [Int] = []        // rows of the contents list this slice stands for
    init(_ label: String, _ size: Int64, _ color: NSColor) { self.label = label; self.size = size; self.color = color }
}

/// Donut chart of one folder or drive. Slices follow the contents list row for row (same order); runs of small rows
/// are merged. Free space is an empty gap ending at 12 o'clock. Before data exists the app logo is shown on the same
/// circle; when data arrives it fades out while the slices sweep in. Hover is linked with the list both ways.
final class PieChart: NSView {
    private(set) var slices: [ChartSlice] = []
    private var total: Int64 = 0
    private var title = "", subtitle = ""
    private var hover = -1, reported = -1
    private var pieRect = NSRect.zero
    private var logoMode = false
    private var logoNote = ""
    private var anim: CGFloat = 1
    private var animStart = Date()
    private var animTimer: Timer?
    private let animSeconds = 0.65
    private let startAngle: CGFloat = -90

    var onHover: ((Int) -> Void)?
    var onClick: ((ChartSlice) -> Void)?
    override var isFlipped: Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self))
    }

    private var animating: Bool { anim < 1 }

    func showLogo(_ title: String, _ subtitle: String, _ note: String) {
        self.title = title; self.subtitle = subtitle; logoNote = note
        slices = []; total = 0; hover = -1; reported = -1
        logoMode = true
        animTimer?.invalidate(); animTimer = nil; anim = 1
        needsDisplay = true
    }

    func setData(_ title: String, _ subtitle: String, _ slices: [ChartSlice]) {
        self.title = title; self.subtitle = subtitle; self.slices = slices
        total = slices.reduce(0) { $0 + max(0, $1.size) }
        hover = -1; reported = -1
        if logoMode && total > 0 && window != nil {
            anim = 0
            animStart = Date()
            animTimer?.invalidate()
            animTimer = Timer.scheduledTimer(withTimeInterval: 0.015, repeats: true) { [weak self] t in
                guard let s = self else { t.invalidate(); return }
                s.anim = CGFloat(min(1, Date().timeIntervalSince(s.animStart) / s.animSeconds))
                if s.anim >= 1 { t.invalidate(); s.animTimer = nil }
                s.needsDisplay = true
            }
        }
        logoMode = false
        needsDisplay = true
    }

    /// Highlight from the list (does not call onHover).
    func setHover(_ i: Int) {
        let v = (i >= slices.count || animating) ? -1 : i
        if v != hover { hover = v; needsDisplay = true }
    }

    override func draw(_ dirtyRect: NSRect) {
        T.SURFACE.setFill(); bounds.fill()
        guard !title.isEmpty else { return }
        let pad: CGFloat = 12
        var y = pad
        Theme.drawText(title, in: NSRect(x: pad, y: y, width: bounds.width - 2 * pad, height: 20), font: Theme.font11, color: T.FG)
        y += 22
        if !subtitle.isEmpty {
            Theme.drawText(subtitle, in: NSRect(x: pad, y: y, width: bounds.width - 2 * pad, height: 16), font: Theme.font9, color: T.MUTED)
            y += 16
        }
        y += 16
        let grow: CGFloat = 6
        var d = min(bounds.width - 2 * pad - 2 * grow, bounds.height - y - pad - 2 * grow)
        d = max(60, d)
        let top = y + max(0, (bounds.height - y - pad - d) / 3)
        pieRect = NSRect(x: ((bounds.width - d) / 2).rounded(), y: top.rounded(), width: d, height: d)
        if logoMode {
            Theme.drawLogo(in: pieRect)
            if !logoNote.isEmpty {
                let ps = NSMutableParagraphStyle(); ps.alignment = .center
                (logoNote as NSString).draw(in: NSRect(x: 10, y: pieRect.maxY + 14, width: bounds.width - 20, height: 40),
                                            withAttributes: [.font: Theme.font9, .foregroundColor: T.MUTED, .paragraphStyle: ps])
            }
        } else if animating { drawMorph() }
        else { drawDonut() }
    }

    private static func ease(_ t: CGFloat) -> CGFloat { 1 - pow(1 - t, 3) }

    private func drawMorph() {
        let e = PieChart.ease(anim), r = pieRect
        Theme.drawLogo(in: r, alpha: 1 - e)
        let reveal = 360 * e, alpha = min(1, anim * 2.5)
        var start = startAngle
        for s in slices {
            let sweep = CGFloat(360 * Double(s.size) / Double(total))
            if sweep <= 0 { continue }
            let visible = min(sweep, reveal - (start - startAngle))
            if visible <= 0 { break }
            if !s.empty { s.color.withAlphaComponent(alpha).setFill(); Theme.pie(r, start: start, sweep: max(0.01, visible)).fill() }
            start += sweep
        }
        let hole = r.width * 0.58 * e
        if hole > 0 { T.SURFACE.setFill(); NSBezierPath(ovalIn: NSRect(x: r.midX - hole / 2, y: r.midY - hole / 2, width: hole, height: hole)).fill() }
    }

    private func drawDonut() {
        let r = pieRect
        if total <= 0 {
            T.BORDER.setFill(); NSBezierPath(ovalIn: r).fill()
        } else {
            var start = startAngle
            for (i, s) in slices.enumerated() {
                let sweep = CGFloat(360 * Double(s.size) / Double(total))
                if sweep <= 0 { continue }
                if !s.empty {
                    let rr = i == hover ? r.insetBy(dx: -6, dy: -6) : r
                    s.color.setFill(); Theme.pie(rr, start: start, sweep: max(0.01, sweep)).fill()
                } else if i == hover {
                    T.BORDER.setFill(); Theme.pie(r, start: start, sweep: sweep).fill()
                }
                start += sweep
            }
            // Thin separators in the panel colour between visible slices.
            if slices.filter({ $0.size > 0 }).count > 1 {
                start = startAngle
                let c = NSPoint(x: r.midX, y: r.midY), rad = r.width / 2 + 7
                T.SURFACE.setStroke()
                for s in slices where s.size > 0 {
                    let a = start * .pi / 180
                    let p = NSBezierPath(); p.move(to: c); p.line(to: NSPoint(x: c.x + rad * cos(a), y: c.y + rad * sin(a)))
                    p.lineWidth = 1.5; p.stroke()
                    start += CGFloat(360 * Double(s.size) / Double(total))
                }
            }
        }
        let hole = r.width * 0.58
        let hr = NSRect(x: r.midX - hole / 2, y: r.midY - hole / 2, width: hole, height: hole)
        T.SURFACE.setFill(); NSBezierPath(ovalIn: hr).fill()
        var l1 = "Total", l2 = Fmt.size(total), l3 = ""
        if hover >= 0 && hover < slices.count { l1 = slices[hover].label; l2 = Fmt.size(slices[hover].size); l3 = slices[hover].detail }
        let inner = hr.insetBy(dx: 16, dy: 0)
        let h1: CGFloat = 15, h2: CGFloat = 19, h3: CGFloat = l3.isEmpty ? 0 : 15
        let ty = hr.minY + (hr.height - h1 - h2 - h3) / 2
        Theme.drawText(l1, in: NSRect(x: inner.minX, y: ty, width: inner.width, height: h1), font: Theme.font9, color: T.MUTED, align: .center)
        Theme.drawText(l2, in: NSRect(x: inner.minX, y: ty + h1, width: inner.width, height: h2), font: Theme.font11, color: T.FG, align: .center)
        if h3 > 0 { Theme.drawText(l3, in: NSRect(x: inner.minX, y: ty + h1 + h2, width: inner.width, height: h3), font: Theme.font9, color: T.MUTED, align: .center) }
    }

    private func hitTest(_ p: NSPoint) -> Int {
        if total <= 0 || pieRect.width <= 0 || logoMode || animating { return -1 }
        let dx = Double(p.x - pieRect.midX), dy = Double(p.y - pieRect.midY), dist = (dx * dx + dy * dy).squareRoot()
        if dist > Double(pieRect.width) / 2 + 6 || dist < Double(pieRect.width) * 0.29 { return -1 }
        var ang = atan2(dy, dx) * 180 / .pi - Double(startAngle)
        ang = (ang.truncatingRemainder(dividingBy: 360) + 360).truncatingRemainder(dividingBy: 360)
        var acc = 0.0
        for (i, s) in slices.enumerated() where s.size > 0 {
            acc += 360 * Double(s.size) / Double(total)
            if ang <= acc { return i }
        }
        return -1
    }

    override func mouseMoved(with event: NSEvent) {
        let h = hitTest(convert(event.locationInWindow, from: nil))
        if h >= 0 && slices[h].dir != nil { NSCursor.pointingHand.set() } else { NSCursor.arrow.set() }
        if h != hover { hover = h; needsDisplay = true }
        if h != reported { reported = h; onHover?(h) }
    }

    override func mouseExited(with event: NSEvent) {
        NSCursor.arrow.set()
        if hover != -1 { hover = -1; needsDisplay = true }
        if reported != -1 { reported = -1; onHover?(-1) }
    }

    override func mouseUp(with event: NSEvent) {
        let h = hitTest(convert(event.locationInWindow, from: nil))
        if h >= 0, slices[h].dir != nil { onClick?(slices[h]) }
    }
}
