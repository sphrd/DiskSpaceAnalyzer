import AppKit

final class ChartSlice {
    var label = "", detail = ""
    var size: Int64 = 0
    var color: NSColor = T.ACCENT
    var dir: DirNode?           // folder opened on click (single-folder slices only)
    var empty = false           // free space: nothing drawn
    var rows: [Int] = []        // rows of the open list this slice stands for
    init(_ label: String, _ size: Int64, _ color: NSColor) { self.label = label; self.size = size; self.color = color }
}

/// A section of an outer ring: a subfolder (ring 1) or a sub-subfolder (ring 2) of a folder slice.
final class ChartArc {
    var start: CGFloat = 0, sweep: CGFloat = 0
    var level = 1
    var owner = 0
    var color: NSColor = T.ACCENT
    var dir: DirNode
    var parentSize: Int64 = 0
    init(_ dir: DirNode) { self.dir = dir }
}

/// Donut chart of the open list. Slices follow the list row for row (same order); runs of small rows are merged. For a
/// folder's contents up to two thin outer rings show the subfolders and sub-subfolders of folder slices (sunburst). Free
/// space is an empty gap ending at 12 o'clock. Before data exists the app logo is shown on the same circle; when data
/// arrives it fades out while the slices sweep in. Hover is linked with the list both ways.
final class PieChart: NSView {
    private(set) var slices: [ChartSlice] = []
    private var arcs: [ChartArc] = []
    private var rings = 0
    private var hoverArc = -1
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
        slices = []; arcs = []; rings = 0; total = 0; hover = -1; hoverArc = -1; reported = -1
        logoMode = true
        animTimer?.invalidate(); animTimer = nil; anim = 1
        needsDisplay = true
    }

    /// Shows slices; rings > 0 adds that many outer rings with the subfolders of folder slices.
    func setData(_ title: String, _ subtitle: String, _ slices: [ChartSlice], rings: Int = 0) {
        self.title = title; self.subtitle = subtitle; self.slices = slices
        total = slices.reduce(Int64(0)) { $0 + max(0, $1.size) }
        self.rings = rings
        arcs = []
        hoverArc = -1
        if rings > 0 && total > 0 {
            var a = startAngle
            for (i, s) in slices.enumerated() {
                let sweep = CGFloat(360 * Double(max(0, s.size)) / Double(total))
                if let d = s.dir { addArcs(d, a, sweep, 1, i, s.color) }
                a += sweep
            }
        }
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

    private func addArcs(_ dir: DirNode, _ start: CGFloat, _ sweep: CGFloat, _ level: Int, _ owner: Int, _ base: NSColor) {
        if level > rings || dir.children.isEmpty || dir.size <= 0 || sweep < 1 { return }
        var a = start
        var k = 0
        for c in dir.children {
            let s = CGFloat(Double(sweep) * Double(c.size) / Double(dir.size))
            if s < 1.5 { break }                   // children are sorted by size: the rest are smaller still
            let arc = ChartArc(c)
            arc.start = a; arc.sweep = s; arc.level = level; arc.owner = owner; arc.parentSize = dir.size
            arc.color = PieChart.blend(base, T.SURFACE, (level == 1 ? 0.28 : 0.5) + (k % 2 == 0 ? 0 : 0.12))
            k += 1
            arcs.append(arc)
            addArcs(c, a, s, level + 1, owner, base)
            a += s
        }
    }

    static func blend(_ a: NSColor, _ b: NSColor, _ t: CGFloat) -> NSColor { a.blended(withFraction: t, of: b) ?? a }

    // Radii as fractions of the chart radius (with outer rings): hole, inner ring, then one band per outer ring.
    private var holeRatio: CGFloat { rings > 0 ? 0.48 : 0.58 }
    private var innerOuterRatio: CGFloat { rings > 0 ? 1 - 0.15 * CGFloat(rings) : 1 }
    private func ringInner(_ level: Int) -> CGFloat { innerOuterRatio + 0.02 + 0.15 * CGFloat(level - 1) }
    private func ringOuter(_ level: Int) -> CGFloat { innerOuterRatio + 0.15 * CGFloat(level) }

    /// Highlight from the list (does not call onHover).
    func setHover(_ i: Int) {
        let v = (i >= slices.count || animating) ? -1 : i
        if v != hover || hoverArc != -1 { hover = v; hoverArc = -1; needsDisplay = true }
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

    private static func band(_ c: NSPoint, _ r0: CGFloat, _ r1: CGFloat, _ start: CGFloat, _ sweep: CGFloat) -> NSBezierPath {
        let p = NSBezierPath()
        p.appendArc(withCenter: c, radius: r1, startAngle: start, endAngle: start + sweep, clockwise: false)
        p.appendArc(withCenter: c, radius: r0, startAngle: start + sweep, endAngle: start, clockwise: true)
        p.close()
        return p
    }

    private func fillBand(_ r0: CGFloat, _ r1: CGFloat, _ start: CGFloat, _ sweep: CGFloat, _ color: NSColor) {
        if r1 - r0 < 0.5 || sweep <= 0 { return }
        color.setFill()
        PieChart.band(NSPoint(x: pieRect.midX, y: pieRect.midY), r0, r1, start, sweep).fill()
    }

    private func drawMorph() {
        let e = PieChart.ease(anim), r = pieRect
        Theme.drawLogo(in: r, alpha: 1 - e)
        let shrink = r.width * (1 - innerOuterRatio) / 2
        let pr = r.insetBy(dx: shrink, dy: shrink)
        let reveal = 360 * e, alpha = min(1, anim * 2.5)
        var start = startAngle
        for s in slices {
            let sweep = CGFloat(360 * Double(s.size) / Double(total))
            if sweep <= 0 { continue }
            let visible = min(sweep, reveal - (start - startAngle))
            if visible <= 0 { break }
            if !s.empty { s.color.withAlphaComponent(alpha).setFill(); Theme.pie(pr, start: start, sweep: max(0.01, visible)).fill() }
            start += sweep
        }
        if rings > 0 {
            let R = r.width / 2
            for a in arcs {
                let visible = min(a.sweep, reveal - (a.start - startAngle))
                if visible > 0 { fillBand(R * ringInner(a.level), R * ringOuter(a.level), a.start, visible, a.color.withAlphaComponent(alpha * e)) }
            }
        }
        let hole = r.width * holeRatio * e
        if hole > 0 { T.SURFACE.setFill(); NSBezierPath(ovalIn: NSRect(x: r.midX - hole / 2, y: r.midY - hole / 2, width: hole, height: hole)).fill() }
    }

    private func drawDonut() {
        let r = pieRect, R = r.width / 2
        if total <= 0 {
            T.BORDER.setFill(); NSBezierPath(ovalIn: r).fill()
        } else {
            var start = startAngle
            for (i, s) in slices.enumerated() {
                let sweep = CGFloat(360 * Double(s.size) / Double(total))
                if sweep <= 0 { continue }
                if !s.empty {
                    if rings > 0 {
                        let col = i == hover && hoverArc < 0 ? PieChart.blend(s.color, .white, 0.25) : s.color
                        fillBand(R * holeRatio, R * innerOuterRatio, start, max(0.01, sweep), col)
                    } else {
                        let rr = i == hover ? r.insetBy(dx: -6, dy: -6) : r
                        s.color.setFill(); Theme.pie(rr, start: start, sweep: max(0.01, sweep)).fill()
                    }
                } else if i == hover {
                    fillBand(R * holeRatio, R, start, sweep, T.BORDER)
                }
                start += sweep
            }
            for (i, a) in arcs.enumerated() {
                fillBand(R * ringInner(a.level), R * ringOuter(a.level), a.start, a.sweep, i == hoverArc ? PieChart.blend(a.color, .white, 0.3) : a.color)
            }
            // Thin separators in the panel colour between visible slices.
            if slices.filter({ $0.size > 0 }).count > 1 {
                start = startAngle
                let c = NSPoint(x: r.midX, y: r.midY), rad = R + 7
                T.SURFACE.setStroke()
                for s in slices where s.size > 0 {
                    let a = start * .pi / 180
                    let p = NSBezierPath(); p.move(to: c); p.line(to: NSPoint(x: c.x + rad * cos(a), y: c.y + rad * sin(a)))
                    p.lineWidth = 1.5; p.stroke()
                    start += CGFloat(360 * Double(s.size) / Double(total))
                }
            }
        }
        let hole = r.width * holeRatio
        let hr = NSRect(x: r.midX - hole / 2, y: r.midY - hole / 2, width: hole, height: hole)
        T.SURFACE.setFill(); NSBezierPath(ovalIn: hr).fill()
        var l1 = "Total", l2 = Fmt.size(total), l3 = ""
        if hoverArc >= 0 && hoverArc < arcs.count {
            let a = arcs[hoverArc]
            l1 = a.dir.name; l2 = Fmt.size(a.dir.size)
            l3 = String(format: "%.1f %%", 100 * Double(a.dir.size) / Double(max(1, a.parentSize))) + " of " + (a.dir.parent?.name ?? "parent")
        } else if hover >= 0 && hover < slices.count { l1 = slices[hover].label; l2 = Fmt.size(slices[hover].size); l3 = slices[hover].detail }
        let inner = hr.insetBy(dx: 16, dy: 0)
        let h1: CGFloat = 15, h2: CGFloat = 19, h3: CGFloat = l3.isEmpty ? 0 : 15
        let ty = hr.minY + (hr.height - h1 - h2 - h3) / 2
        Theme.drawText(l1, in: NSRect(x: inner.minX, y: ty, width: inner.width, height: h1), font: Theme.font9, color: T.MUTED, align: .center)
        Theme.drawText(l2, in: NSRect(x: inner.minX, y: ty + h1, width: inner.width, height: h2), font: Theme.font11, color: T.FG, align: .center)
        if h3 > 0 { Theme.drawText(l3, in: NSRect(x: inner.minX, y: ty + h1 + h2, width: inner.width, height: h3), font: Theme.font9, color: T.MUTED, align: .center) }
    }

    private func angleAt(_ p: NSPoint) -> (dist: Double, angle: Double) {
        let dx = Double(p.x - pieRect.midX), dy = Double(p.y - pieRect.midY)
        return ((dx * dx + dy * dy).squareRoot(), atan2(dy, dx) * 180 / .pi)
    }

    /// Outer-ring section under the point (-1 = none).
    private func arcAt(_ p: NSPoint) -> Int {
        if rings == 0 || arcs.isEmpty || logoMode || animating || pieRect.width <= 0 { return -1 }
        let (d, ang) = angleAt(p)
        let dist = d / Double(pieRect.width / 2)
        if dist <= Double(innerOuterRatio) || dist > 1.02 { return -1 }
        for (i, a) in arcs.enumerated() {
            if dist < Double(ringInner(a.level)) - 0.01 || dist > Double(ringOuter(a.level)) + 0.01 { continue }
            let rel = ((ang - Double(a.start)).truncatingRemainder(dividingBy: 360) + 360).truncatingRemainder(dividingBy: 360)
            if rel <= Double(a.sweep) { return i }
        }
        return -1
    }

    private func sliceAt(_ p: NSPoint) -> Int {
        if total <= 0 || pieRect.width <= 0 || logoMode || animating { return -1 }
        let (dist, a0) = angleAt(p)
        let R = Double(pieRect.width) / 2
        if dist > R + 6 || dist < R * Double(holeRatio) { return -1 }
        if rings > 0 && dist > R * Double(innerOuterRatio) {
            let a = arcAt(p)
            return a >= 0 ? arcs[a].owner : -1
        }
        var ang = a0 - Double(startAngle)
        ang = (ang.truncatingRemainder(dividingBy: 360) + 360).truncatingRemainder(dividingBy: 360)
        var acc = 0.0
        for (i, s) in slices.enumerated() where s.size > 0 {
            acc += 360 * Double(s.size) / Double(total)
            if ang <= acc { return i }
        }
        return -1
    }

    override func mouseMoved(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        let h = sliceAt(p), ha = arcAt(p)
        if ha >= 0 || (h >= 0 && slices[h].dir != nil) { NSCursor.pointingHand.set() } else { NSCursor.arrow.set() }
        if h != hover || ha != hoverArc { hover = h; hoverArc = ha; needsDisplay = true }
        if h != reported { reported = h; onHover?(h) }
    }

    override func mouseExited(with event: NSEvent) {
        NSCursor.arrow.set()
        if hover != -1 || hoverArc != -1 { hover = -1; hoverArc = -1; needsDisplay = true }
        if reported != -1 { reported = -1; onHover?(-1) }
    }

    override func mouseUp(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        let ha = arcAt(p)
        if ha >= 0 {
            let s = ChartSlice(arcs[ha].dir.name, arcs[ha].dir.size, arcs[ha].color)
            s.dir = arcs[ha].dir
            onClick?(s)
            return
        }
        let h = sliceAt(p)
        if h >= 0, slices[h].dir != nil { onClick?(slices[h]) }
    }
}
