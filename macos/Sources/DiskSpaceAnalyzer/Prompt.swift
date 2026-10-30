import AppKit

/// The sign on the left of a prompt: a red warning sign (permanent deletes, uninstall), an amber one (warnings, errors)
/// or the app logo (plain information).
enum PromptIcon { case danger, warn, info }

/// Warning sign: a triangle with rounded corners and an exclamation mark cut out in the background colour.
final class WarningSignView: NSView {
    var color: NSColor { didSet { needsDisplay = true } }
    init(frame: NSRect, color: NSColor) { self.color = color; super.init(frame: frame) }
    required init?(coder: NSCoder) { fatalError() }
    override var isFlipped: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        let b = bounds
        let lw = b.width * 0.14
        let r = b.insetBy(dx: lw / 2 + 1, dy: lw / 2 + 1)
        let tri = NSBezierPath()
        tri.move(to: NSPoint(x: r.midX, y: r.minY))
        tri.line(to: NSPoint(x: r.maxX, y: r.maxY))
        tri.line(to: NSPoint(x: r.minX, y: r.maxY))
        tri.close()
        tri.lineWidth = lw
        tri.lineJoinStyle = .round
        color.setFill(); color.setStroke()
        tri.fill(); tri.stroke()
        let bw = b.width * 0.1
        T.BG.setFill()
        Theme.roundRect(NSRect(x: b.midX - bw / 2, y: r.minY + r.height * 0.32, width: bw, height: r.height * 0.36), bw / 2).fill()
        let d = bw * 1.15
        NSBezierPath(ovalIn: NSRect(x: b.midX - d / 2, y: r.minY + r.height * 0.76, width: d, height: d)).fill()
    }
}

/// The app's one prompt, in the About window's frame: a sign on the left, a bold heading, the text, an optional amber
/// line, an optional box listing items (one per line, selectable, scrollable, never cut short) and rounded buttons at the
/// bottom right. Return presses the default button (outlined), Escape the cancel button. Shown as a sheet on the main
/// window; done gets the index of the button pressed.
final class PromptDialog: AppDialog {
    private(set) var heading = "", text = "", warning = ""
    private(set) var icon: PromptIcon = .info
    private(set) var detailLines: [String] = []
    private(set) var detailsBox: PanelView?
    private(set) var detailsView: NSTextView?
    private(set) var defaultIndex = 0, cancelIndex = 0
    private(set) var result = -1
    private var done: ((Int) -> Void)?
    private var monitor: Any?
    private weak var sheetParent: NSWindow?
    /// The prompts on screen (kept alive while they show).
    private(set) static var open: [PromptDialog] = []

    convenience init(icon: PromptIcon, heading: String, text: String, warning: String = "", details: [String] = [],
                     buttons: [(String, ButtonKind)], defaultIndex: Int = 0, cancelIndex: Int? = nil) {
        self.init(title: "Disk Space Analyzer")
        self.icon = icon; self.heading = heading; self.text = text; self.warning = warning; self.detailLines = details
        self.defaultIndex = defaultIndex
        self.cancelIndex = cancelIndex ?? buttons.count - 1
        let width: CGFloat = details.isEmpty ? 540 : 700
        resize(width: width, height: 300)
        if icon != .info {
            logo.isHidden = true
            root.addSubview(WarningSignView(frame: NSRect(x: 34, y: 30, width: 84, height: 76), color: icon == .danger ? T.DANGER : T.WARN))
        }
        var y: CGFloat = 26
        let headFont = NSFont.boldSystemFont(ofSize: 18)
        addLabel(heading, headFont, T.FG, y: &y, height: measure(heading, headFont), gapAfter: 8)
        if !text.isEmpty { addLabel(text, Theme.font, T.FG, y: &y, height: measure(text, Theme.font), gapAfter: 8) }
        if !warning.isEmpty { addLabel(warning, Theme.font, T.WARN_FG, y: &y, height: measure(warning, Theme.font), gapAfter: 8) }
        if !details.isEmpty {
            y += 4
            let font = Theme.font9
            let lineH = Theme.lineHeight(font) + 1
            let h = min(260, max(44, CGFloat(details.count) * lineH + 18))
            let box = PanelView(frame: NSRect(x: AppDialog.contentX, y: y, width: contentWidth, height: h))
            box.radius = 8
            box.panelFill = T.SURFACE; box.outline = T.BORDER; box.backFill = T.BG
            let scroll = NSScrollView(frame: box.bounds.insetBy(dx: 2, dy: 2))
            scroll.hasVerticalScroller = true
            scroll.hasHorizontalScroller = true
            scroll.autohidesScrollers = true
            scroll.scrollerStyle = .overlay
            scroll.scrollerKnobStyle = .light
            scroll.drawsBackground = false
            scroll.borderType = .noBorder
            let tv = NSTextView(frame: NSRect(origin: .zero, size: scroll.contentSize))
            tv.isEditable = false
            tv.isSelectable = true
            tv.drawsBackground = false
            tv.textContainerInset = NSSize(width: 6, height: 7)
            tv.isHorizontallyResizable = true
            tv.isVerticallyResizable = true
            tv.minSize = NSSize(width: 0, height: scroll.contentSize.height)
            tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
            tv.textContainer?.widthTracksTextView = false
            tv.textContainer?.containerSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
            tv.textStorage?.setAttributedString(NSAttributedString(string: details.joined(separator: "\n"),
                                                                   attributes: [.font: font, .foregroundColor: T.FG]))
            tv.selectedTextAttributes = [.backgroundColor: T.ACCENT, .foregroundColor: T.BG]
            scroll.documentView = tv
            box.addSubview(scroll)
            root.addSubview(box)
            detailsBox = box
            detailsView = tv
            y += h + 8
        }
        resize(width: width, height: max(210, y + 72))
        var items: [(String, () -> Void)] = []
        for (i, b) in buttons.enumerated() { items.append((b.0, { [weak self] in self?.finish(i) })) }
        setButtons(items, kinds: buttons.map { $0.1 })
        if defaultIndex < self.buttons.count { self.buttons[defaultIndex].ring = true }
    }

    private func measure(_ s: String, _ font: NSFont) -> CGFloat {
        ceil((s as NSString).boundingRect(with: NSSize(width: contentWidth - 4, height: 10000), options: [.usesLineFragmentOrigin, .usesFontLeading],
                                          attributes: [.font: font]).height) + 4
    }

    var buttonTitles: [String] { buttons.map { $0.title } }
    var buttonKinds: [ButtonKind] { buttons.map { $0.kind } }

    /// Shows the prompt as a sheet on parent (a window of its own when there is none or it has a sheet already).
    func show(over parent: NSWindow?, _ done: @escaping (Int) -> Void) {
        self.done = done
        PromptDialog.open.append(self)
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            guard let s = self, let w = s.window, e.window === w else { return e }
            return s.handleKey(e.keyCode) ? nil : e
        }
        guard let w = window else { return }
        if let p = parent, p.isVisible, p.attachedSheet == nil {
            sheetParent = p
            p.beginSheet(w) { _ in }
        } else {
            present(over: parent)
        }
        w.makeFirstResponder(nil)
    }

    /// Return / Enter press the default button, Escape the cancel button.
    @discardableResult
    func handleKey(_ keyCode: UInt16) -> Bool {
        switch keyCode {
        case 36, 76: finish(defaultIndex); return true
        case 53: finish(cancelIndex); return true
        default: return false
        }
    }

    /// Closes the prompt as if button i was pressed.
    func finish(_ i: Int) {
        if result >= 0 { return }
        result = i
        if let m = monitor { NSEvent.removeMonitor(m); monitor = nil }
        if let w = window {
            if let p = sheetParent { p.endSheet(w) }
            w.orderOut(nil)
        }
        PromptDialog.open.removeAll { $0 === self }
        let d = done
        done = nil
        d?(i)
    }

    override func cancelOperation(_ sender: Any?) { finish(cancelIndex) }
    override func close() { finish(cancelIndex) }
    override func windowWillClose(_ notification: Notification) { finish(cancelIndex) }
}

extension MainWindowController {
    /// A themed message with one OK button (warning sign unless icon says otherwise).
    func alert(_ heading: String, _ text: String = "", icon: PromptIcon = .warn, details: [String] = [], _ done: (() -> Void)? = nil) {
        if selfTestDir != nil { statusLabel.stringValue = heading + (text.isEmpty ? "" : " " + text); done?(); return }
        let d = PromptDialog(icon: icon, heading: heading, text: text, details: details, buttons: [("OK", .primary)], defaultIndex: 0, cancelIndex: 0)
        d.show(over: window) { _ in done?() }
    }
}
