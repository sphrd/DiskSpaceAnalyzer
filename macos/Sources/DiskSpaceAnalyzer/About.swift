import AppKit

/// The app's small dialog frame, shared by About and the update window so they always look the same: fixed dark
/// window, the logo on the left, content in a column on the right, buttons bottom right (the first one primary).
class AppDialog: NSWindowController, NSWindowDelegate {
    let root = FlatView(frame: NSRect(x: 0, y: 0, width: 470, height: 250))
    private var buttons: [RoundButton] = []
    var onClose: (() -> Void)?
    static let contentX: CGFloat = 146

    init(title: String) {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 470, height: 250), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = title
        w.appearance = NSAppearance(named: .darkAqua)
        w.backgroundColor = T.BG
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        root.fill = T.BG
        w.contentView = root
        root.addSubview(LogoView(frame: NSRect(x: 30, y: 30, width: 92, height: 92)))
    }
    required init?(coder: NSCoder) { fatalError() }

    var contentWidth: CGFloat { root.bounds.width - AppDialog.contentX - 24 }

    /// Resizes the dialog (call before adding labels for a new width, and before setButtons for a new height).
    func resize(width: CGFloat, height: CGFloat) {
        window?.setContentSize(NSSize(width: width, height: height))
        root.frame = NSRect(x: 0, y: 0, width: width, height: height)
    }
    var buttonsTop: CGFloat { buttons.first?.frame.minY ?? root.bounds.height - 20 }

    @discardableResult
    func addLabel(_ s: String, _ font: NSFont, _ color: NSColor, y: inout CGFloat, height: CGFloat, gapAfter: CGFloat = 0) -> NSTextField {
        let l = NSTextField(wrappingLabelWithString: s)
        l.font = font; l.textColor = color
        l.frame = NSRect(x: AppDialog.contentX, y: y, width: contentWidth, height: height)
        root.addSubview(l)
        y += height + gapAfter
        return l
    }

    /// Replaces the buttons, laid out right to left; the first is primary, the others secondary.
    func setButtons(_ items: [(String, () -> Void)]) {
        buttons.forEach { $0.removeFromSuperview() }
        buttons = []
        var x = root.bounds.width - 24
        for (i, item) in items.enumerated() {
            let b = RoundButton(item.0, i == 0 ? .primary : .secondary, item.1)
            if i == 0 { b.minChars = 8 }
            let s = b.preferredSize
            x -= s.width
            b.frame = NSRect(x: x, y: root.bounds.height - 20 - s.height, width: s.width, height: s.height)
            x -= 8
            root.addSubview(b)
            buttons.append(b)
        }
    }

    func present(over parent: NSWindow?) {
        guard let w = window else { return }
        if !w.isVisible {
            if let p = parent { w.setFrameOrigin(NSPoint(x: p.frame.midX - w.frame.width / 2, y: p.frame.midY - w.frame.height / 2)) }
            else { w.center() }
        }
        showWindow(nil)
        w.makeKeyAndOrderFront(nil)
    }

    override func close() { window?.close() }
    func windowWillClose(_ notification: Notification) { onClose?() }
    override func cancelOperation(_ sender: Any?) { close() }
}

/// About window: logo, name, version, author, description and licence, repository link.
final class AboutWindow: AppDialog {
    static let repoURL = URL(string: "https://github.com/sphrd/DiskSpaceAnalyzer")!

    convenience init(checkForUpdates: @escaping () -> Void) {
        self.init(title: "About Disk Space Analyzer")
        resize(width: 540, height: 600)
        var y: CGFloat = 26
        addLabel("Disk Space Analyzer", NSFont.boldSystemFont(ofSize: 20), T.FG, y: &y, height: 28, gapAfter: 4)
        addLabel("Version \(Updater.currentString)", Theme.font, T.FG, y: &y, height: 19)
        addLabel("Author: sphrd", Theme.font, T.FG, y: &y, height: 19, gapAfter: 10)
        addLabel("Fast disk space analyzer and cleaner for macOS.\nFree and open source (MIT licence).", Theme.font9, T.MUTED, y: &y, height: 34, gapAfter: 6)
        let link = LinkLabel("github.com/sphrd/DiskSpaceAnalyzer") { NSWorkspace.shared.open(AboutWindow.repoURL) }
        link.frame = NSRect(x: AppDialog.contentX, y: y, width: 300, height: 17)
        root.addSubview(link)
        y += 17 + 18
        addLabel("Complete scans", NSFont.boldSystemFont(ofSize: 13), T.FG, y: &y, height: 18, gapAfter: 2)
        addLabel("Give the app Full Disk Access (button in the toolbar) so it can read every folder; without it macOS hides some folders and their sizes are missing.",
                 Theme.font9, T.MUTED, y: &y, height: 46, gapAfter: 12)
        addLabel("Keyboard shortcuts", NSFont.boldSystemFont(ofSize: 13), T.FG, y: &y, height: 18, gapAfter: 4)
        let keys: [(String, String)] = [("Return", "Open the selected folder or file"), ("⌘↑", "Up to the parent folder"), ("⌘R", "Rescan the selected folder"),
                                        ("⇧⌘D", "Add to the delete list"), ("⌘⌫", "Delete permanently"), ("⌘F", "Search files"), ("⇧⌘E", "Export")]
        for (k, what) in keys {
            let a = NSTextField(labelWithString: k); a.font = Theme.font9; a.textColor = T.FG
            a.frame = NSRect(x: AppDialog.contentX, y: y, width: 80, height: 17)
            let b = NSTextField(labelWithString: what); b.font = Theme.font9; b.textColor = T.MUTED
            b.frame = NSRect(x: AppDialog.contentX + 80, y: y, width: contentWidth - 80, height: 17)
            root.addSubview(a); root.addSubview(b)
            y += 19
        }
        resize(width: 540, height: y + 76)
        setButtons([("Close", { [weak self] in self?.close() }), ("Check for updates", { [weak self] in self?.close(); checkForUpdates() })])
    }
}

/// Update window, in the same frame as About: heading, one line of text, a progress bar while downloading, and the
/// buttons for the current step.
final class UpdateWindow: AppDialog {
    private var heading: NSTextField!
    private var body: NSTextField!
    private let bar = ProgressPill()

    convenience init() {
        self.init(title: "Disk Space Analyzer - updates")
        var y: CGFloat = 26
        heading = addLabel(" ", NSFont.boldSystemFont(ofSize: 20), T.FG, y: &y, height: 28, gapAfter: 8)
        body = addLabel(" ", Theme.font, T.FG, y: &y, height: 60)
        root.addSubview(bar)
        bar.isHidden = true
    }

    func setStep(_ head: String, _ text: String, progress: Double = -1, _ items: [(String, () -> Void)]) {
        heading.stringValue = head
        body.stringValue = text
        setButtons(items)
        setProgress(text, progress)
    }

    func setProgress(_ text: String, _ progress: Double) {
        body.stringValue = text
        bar.frame = NSRect(x: AppDialog.contentX, y: buttonsTop - 28, width: contentWidth, height: 8)
        bar.fraction = progress
        bar.isHidden = progress < 0
    }
}

final class ProgressPill: NSView {
    var fraction: Double = 0 { didSet { needsDisplay = true } }
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: NSRect) {
        Theme.fillRound(bounds, bounds.height / 2, fill: T.BORDER)
        let w = (bounds.width * CGFloat(min(1, max(0, fraction)))).rounded()
        if w > 0 { Theme.fillRound(NSRect(x: 0, y: 0, width: max(w, bounds.height), height: bounds.height), bounds.height / 2, fill: T.ACCENT) }
    }
}

final class LogoView: NSView {
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: NSRect) { Theme.drawLogo(in: bounds) }
}

/// Accent text that opens a link on click.
final class LinkLabel: NSView {
    private let text: String
    var textValue: String { text }
    private let action: () -> Void
    private var hover = false
    init(_ text: String, _ action: @escaping () -> Void) { self.text = text; self.action = action; super.init(frame: .zero) }
    required init?(coder: NSCoder) { fatalError() }
    override var isFlipped: Bool { true }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .pointingHand) }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }
    override func mouseEntered(with event: NSEvent) { hover = true; needsDisplay = true }
    override func mouseExited(with event: NSEvent) { hover = false; needsDisplay = true }
    override func mouseUp(with event: NSEvent) { action() }
    override func draw(_ dirtyRect: NSRect) { Theme.drawText(text, in: bounds, font: Theme.font9, color: hover ? T.ACCENT_ACTIVE : T.ACCENT) }
}
