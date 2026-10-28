import AppKit

/// About window: logo, name, version, author, description, repository link, Check for updates / Close.
final class AboutWindow: NSWindowController {
    static let repoURL = URL(string: "https://github.com/sphrd/DiskSpaceAnalyzer")!

    convenience init(checkForUpdates: @escaping () -> Void) {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 470, height: 250), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = "About Disk Space Analyzer"
        w.appearance = NSAppearance(named: .darkAqua)
        w.backgroundColor = T.BG
        w.isReleasedWhenClosed = false
        self.init(window: w)
        let root = FlatView(frame: w.contentRect(forFrameRect: w.frame))
        root.fill = T.BG
        w.contentView = root

        let logo = LogoView(frame: NSRect(x: 30, y: 30, width: 92, height: 92))
        root.addSubview(logo)
        var y: CGFloat = 26
        func label(_ s: String, _ font: NSFont, _ color: NSColor, _ h: CGFloat) -> NSTextField {
            let l = NSTextField(labelWithString: s)
            l.font = font; l.textColor = color
            l.frame = NSRect(x: 146, y: y, width: 300, height: h)
            root.addSubview(l)
            y += h
            return l
        }
        _ = label("Disk Space Analyzer", NSFont.boldSystemFont(ofSize: 20), T.FG, 28); y += 4
        _ = label("Version \(Updater.currentString)", Theme.font, T.FG, 19)
        _ = label("Author: sphrd", Theme.font, T.FG, 19); y += 10
        let desc = label("Fast disk space analyzer and cleaner for macOS.\nFree and open source (MIT licence).", Theme.font9, T.MUTED, 34)
        desc.maximumNumberOfLines = 2
        y += 6
        let link = LinkLabel("github.com/sphrd/DiskSpaceAnalyzer") { NSWorkspace.shared.open(AboutWindow.repoURL) }
        link.frame = NSRect(x: 146, y: y, width: 300, height: 17)
        root.addSubview(link)

        let close = RoundButton("Close", .primary) { [weak w] in w?.close() }
        close.minChars = 8
        let check = RoundButton("Check for updates", .secondary) { [weak w] in w?.close(); checkForUpdates() }
        let cs = close.preferredSize, ks = check.preferredSize
        let by = root.bounds.height - 20 - cs.height
        close.frame = NSRect(x: root.bounds.width - 24 - cs.width, y: by, width: cs.width, height: cs.height)
        check.frame = NSRect(x: close.frame.minX - 8 - ks.width, y: by, width: ks.width, height: ks.height)
        root.addSubview(close); root.addSubview(check)
    }

    func present(over parent: NSWindow?) {
        guard let w = window else { return }
        if let p = parent { w.setFrameOrigin(NSPoint(x: p.frame.midX - w.frame.width / 2, y: p.frame.midY - w.frame.height / 2)) }
        else { w.center() }
        showWindow(nil)
        w.makeKeyAndOrderFront(nil)
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
