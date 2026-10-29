// Artwork for the disk image (compiled and run by macos/build.sh; not part of the app).
//   dmg_art background <out.png> <scale>
//       the window background, 600 x 380 points: dark, an accent arrow from the app (left) to Applications (right)
//       and "Drag to Applications"
//   dmg_art preview <background> <app> <appX> <appY> <linkX> <linkY> <iconSize> <out.png>
//       how Finder shows the opened disk image (icon centres from the .DS_Store, top-left origin), for CI to check
import AppKit

let W: CGFloat = 600, H: CGFloat = 380
// Icon centres (Finder coordinates, from the top left); macos/dmg_settings.py uses the same values.
let appCenter = CGPoint(x: 150, y: 190), linkCenter = CGPoint(x: 450, y: 190)

func rgb(_ hex: UInt32) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255, blue: CGFloat(hex & 0xff) / 255, alpha: 1)
}
let BG = rgb(0x222831), ACCENT = rgb(0x4da3ff), FG = rgb(0xe6e9ef), MUTED = rgb(0x9aa3b0)

/// Draws in points with the origin at the top left, into a bitmap of scale pixels per point; writes a PNG.
func render(_ scale: CGFloat, _ out: String, _ draw: () -> Void) {
    let pw = Int(W * scale), ph = Int(H * scale)
    guard let ctx = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: 0,
                              space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { exit(1) }
    ctx.translateBy(x: 0, y: CGFloat(ph))
    ctx.scaleBy(x: scale, y: -scale)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
    draw()
    NSGraphicsContext.restoreGraphicsState()
    guard let img = ctx.makeImage() else { exit(1) }
    let rep = NSBitmapImageRep(cgImage: img)
    rep.size = NSSize(width: W, height: H)          // 72 dpi at 1x, 144 dpi at 2x (tiffutil -cathidpicheck wants that)
    guard let png = rep.representation(using: .png, properties: [:]) else { exit(1) }
    do { try png.write(to: URL(fileURLWithPath: out)) } catch { print("cannot write \(out): \(error)"); exit(1) }
    print("wrote \(out) (\(pw) x \(ph))")
}

func text(_ s: String, _ font: NSFont, _ color: NSColor, centerX: CGFloat, top: CGFloat) {
    let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
    let size = (s as NSString).size(withAttributes: attrs)
    (s as NSString).draw(at: NSPoint(x: centerX - size.width / 2, y: top), withAttributes: attrs)
}

func drawBackground() {
    BG.setFill()
    NSRect(x: 0, y: 0, width: W, height: H).fill()
    text("Drag to Applications", NSFont.systemFont(ofSize: 22, weight: .semibold), FG, centerX: W / 2, top: 44)
    text("Drop Disk Space Analyzer on the Applications folder to install it", NSFont.systemFont(ofSize: 12), MUTED, centerX: W / 2, top: 78)
    // Arrow between the two icons (128 px icons: their edges are 64 from the centres).
    let y = appCenter.y, x0 = appCenter.x + 64 + 26, x1 = linkCenter.x - 64 - 26
    ACCENT.setStroke()
    let shaft = NSBezierPath()
    shaft.move(to: NSPoint(x: x0, y: y)); shaft.line(to: NSPoint(x: x1 - 4, y: y))
    shaft.lineWidth = 7; shaft.lineCapStyle = .round
    shaft.stroke()
    let head = NSBezierPath()
    head.move(to: NSPoint(x: x1 - 20, y: y - 18)); head.line(to: NSPoint(x: x1, y: y)); head.line(to: NSPoint(x: x1 - 20, y: y + 18))
    head.lineWidth = 7; head.lineCapStyle = .round; head.lineJoinStyle = .round
    head.stroke()
}

let args = CommandLine.arguments
if args.count == 4 && args[1] == "background", let scale = Double(args[3]) {
    render(CGFloat(scale), args[2]) { drawBackground() }
} else if args.count == 10 && args[1] == "preview" {
    guard let bg = NSImage(contentsOfFile: args[2]) else { print("cannot read \(args[2])"); exit(1) }
    let n = args[4...8].compactMap { Double($0) }.map { CGFloat($0) }
    guard n.count == 5 else { print("bad numbers"); exit(1) }
    let size = n[4]
    render(2, args[9]) {
        bg.draw(in: NSRect(x: 0, y: 0, width: W, height: H), from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        for (path, cx, cy, label) in [(args[3], n[0], n[1], "Disk Space Analyzer"), ("/Applications", n[2], n[3], "Applications")] {
            let icon = NSWorkspace.shared.icon(forFile: path)
            icon.draw(in: NSRect(x: cx - size / 2, y: cy - size / 2, width: size, height: size), from: .zero, operation: .sourceOver,
                      fraction: 1, respectFlipped: true, hints: nil)
            text(label, NSFont.systemFont(ofSize: 13), FG, centerX: cx, top: cy + size / 2 + 4)
        }
    }
} else {
    print("usage: dmg_art background <out.png> <scale> | preview <background> <app> <appX> <appY> <linkX> <linkY> <iconSize> <out.png>")
    exit(2)
}
