import AppKit

/// Picks the export format in the save panel and keeps the file name's extension in step.
final class ExportFormat: NSObject {
    let popup = NSPopUpButton(frame: NSRect(x: 70, y: 8, width: 300, height: 26), pullsDown: false)
    let view = NSView(frame: NSRect(x: 0, y: 0, width: 390, height: 42))
    weak var panel: NSSavePanel?

    init(_ panel: NSSavePanel) {
        self.panel = panel
        super.init()
        let label = NSTextField(labelWithString: "Format:")
        label.frame = NSRect(x: 8, y: 13, width: 60, height: 17)
        popup.addItems(withTitles: ["HTML report (.html)", "CSV - every folder (.csv)", "CSV - the list shown now (.csv)"])
        popup.target = self
        popup.action = #selector(changed(_:))
        view.addSubview(label); view.addSubview(popup)
        panel.accessoryView = view
        panel.allowedFileTypes = ["html"]
    }

    var index: Int { popup.indexOfSelectedItem }

    @objc func changed(_ sender: Any?) {
        guard let p = panel else { return }
        let ext = index == 0 ? "html" : "csv"
        p.allowedFileTypes = [ext]
        let base = (p.nameFieldStringValue as NSString).deletingPathExtension
        p.nameFieldStringValue = base + "." + ext
    }
}

/// Export: an HTML report (self-contained, opens in any browser) or CSV for spreadsheets.
extension MainWindowController {
    func exportScan() {
        guard let sc = scope(), !busy, let w = window else { return }
        let r = rootOf(sc.top)
        let raw = sc.parent == nil ? (r?.path ?? sc.name) : sc.name
        let name = raw.components(separatedBy: CharacterSet(charactersIn: "/:")).filter { !$0.isEmpty }.joined(separator: "_")
        let panel = NSSavePanel()
        panel.title = "Export " + sc.fullPath
        panel.message = "Export " + sc.fullPath
        panel.isExtensionHidden = false
        panel.canCreateDirectories = true
        let format = ExportFormat(panel)
        panel.nameFieldStringValue = "Disk usage \(name.isEmpty ? "scan" : name) \(Fmt.day(Date())).html"
        panel.beginSheetModal(for: w) { resp in
            guard resp == .OK, let url = panel.url else { return }
            self.writeExport(format.index, url, sc)
        }
    }

    private func writeExport(_ format: Int, _ url: URL, _ sc: DirNode) {
        let listCsv = format == 2 ? MainWindowController.listCsv(activePage) : ""
        statusLabel.stringValue = "Exporting to \(url.path)..."
        let ver = Updater.currentString
        let r = rootOf(sc.top)
        let driveLine = sc.parent == nil && r != nil && r!.isDrive && r!.total > 0
            ? "\(Fmt.size(r!.used)) used of \(Fmt.size(r!.total)), \(Fmt.size(r!.free)) free" : ""
        readInBackground({ () -> String? in
            do {
                switch format {
                case 0: try MainWindowController.htmlReport(sc, ver, driveLine).write(to: url, atomically: true, encoding: .utf8)
                case 1: try MainWindowController.foldersCsv(sc).write(to: url, atomically: true, encoding: .utf8)
                default: try listCsv.write(to: url, atomically: true, encoding: .utf8)
                }
                return nil
            } catch { return error.localizedDescription }
        }, { [weak self] err in
            guard let self = self else { return }
            if let e = err {
                self.statusLabel.stringValue = "Export failed."
                self.alert("Could not save the export:\n" + e)
                return
            }
            self.statusLabel.stringValue = "Exported to " + url.path
            if format == 0 { NSWorkspace.shared.open(url) }
        })
    }

    static func csv(_ s: String) -> String {
        s.rangeOfCharacter(from: CharacterSet(charactersIn: ",\";\n\r")) != nil ? "\"" + s.replacingOccurrences(of: "\"", with: "\"\"") + "\"" : s
    }

    static func listCsv(_ p: Page) -> String {
        var lines = ["\u{FEFF}" + p.columns.map { csv($0.title) }.joined(separator: ",")]
        for it in p.items { lines.append(p.columns.indices.map { csv($0 < it.cells.count ? it.cells[$0] : "") }.joined(separator: ",")) }
        return lines.joined(separator: "\r\n") + "\r\n"
    }

    static func foldersCsv(_ scope: DirNode) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        var lines = ["\u{FEFF}Path,Size (bytes),Size on disk (bytes),Files,Folders,Modified"]
        for d in scope.descendants() {
            let mod = d.modified > 0 ? f.string(from: Date(timeIntervalSince1970: TimeInterval(d.modified))) : ""
            lines.append([csv(d.fullPath), String(d.bytes), String(d.alloc), String(d.files), String(d.dirs), mod].joined(separator: ","))
        }
        return lines.joined(separator: "\r\n") + "\r\n"
    }

    static func hex(_ c: NSColor) -> String {
        let s = c.usingColorSpace(.sRGB) ?? c
        return String(format: "#%02x%02x%02x", Int((s.redComponent * 255).rounded()), Int((s.greenComponent * 255).rounded()), Int((s.blueComponent * 255).rounded()))
    }

    static func h(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;").replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;").replacingOccurrences(of: "'", with: "&#39;")
    }

    /// "12" or "12.3" (one decimal at most, always a dot).
    static func num1(_ v: Double) -> String {
        let s = String(format: "%.1f", v)
        return s.hasSuffix(".0") ? String(s.dropLast(2)) : s
    }

    static func htmlReport(_ scope: DirNode, _ version: String, _ driveLine: String) -> String {
        var sb = ""
        let measure = DirNode.onDisk ? "size on disk" : "file size"
        sb += "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Disk usage - \(h(scope.fullPath))</title><style>"
        sb += "body{background:\(hex(T.BG));color:\(hex(T.FG));font:14px -apple-system,system-ui,'Segoe UI',sans-serif;margin:32px auto;max-width:1100px;padding:0 24px}"
        sb += "h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:32px 0 10px}.muted{color:\(hex(T.MUTED))}"
        sb += ".card{background:\(hex(T.SURFACE));border:1px solid \(hex(T.BORDER));border-radius:12px;padding:16px 20px;margin-top:16px}"
        sb += "table{width:100%;border-collapse:collapse}td,th{padding:6px 8px;text-align:left;white-space:nowrap}td.p{white-space:normal;word-break:break-all}"
        sb += "th{color:\(hex(T.MUTED));font-weight:normal;border-bottom:1px solid \(hex(T.BORDER))}td.n,th.n{text-align:right}"
        sb += ".bar{background:\(hex(T.BORDER));border-radius:4px;height:8px;width:160px}.bar i{display:block;background:\(hex(T.ACCENT));border-radius:4px;height:8px}"
        sb += ".top{display:flex;gap:32px;align-items:center}a{color:\(hex(T.ACCENT))}.sw{display:inline-block;width:10px;height:10px;border-radius:3px;margin-right:8px}"
        sb += "</style></head><body>"
        sb += "<h1>\(h(scope.fullPath))</h1>"
        sb += "<div class=\"muted\">Disk usage report, \(Fmt.date(Date())) &middot; sizes are \(measure)</div>"

        // Summary and donut of the folder's contents.
        var parts: [(String, Int64)] = scope.children.prefix(10).filter { $0.size > 0 }.map { ($0.name, $0.size) }
        let rest = scope.size - parts.reduce(Int64(0)) { $0 + $1.1 }
        if rest > 0 { parts.append(("Other folders and files", rest)) }
        sb += "<div class=\"card top\">" + donutSvg(parts) + "<div>"
        sb += "<div style=\"font-size:26px\">\(Fmt.size(scope.size))</div>"
        sb += "<div class=\"muted\">\(Fmt.count(scope.files)) files in \(Fmt.count(scope.dirs)) folders &middot; file size \(Fmt.size(scope.bytes)), size on disk \(Fmt.size(scope.alloc))</div>"
        if !driveLine.isEmpty { sb += "<div class=\"muted\">Drive: \(h(driveLine))</div>" }
        sb += "<table style=\"margin-top:12px\">"
        for (i, p) in parts.enumerated() {
            let color = i < parts.count - 1 || rest <= 0 ? hex(T.CHART[i % T.CHART.count]) : hex(T.CHART_OTHER)
            sb += "<tr><td><span class=\"sw\" style=\"background:\(color)\"></span>\(h(p.0))</td><td class=\"n\">\(Fmt.size(p.1))</td><td class=\"n muted\">\(String(format: "%.1f", 100 * Double(p.1) / Double(max(1, scope.size)))) %</td></tr>"
        }
        sb += "</table></div></div>"

        // Largest folders (two levels).
        sb += "<h2>Largest folders</h2><div class=\"card\"><table><tr><th>Folder</th><th class=\"n\">Size</th><th>% of \(h(PathUtil.name(scope.name)))</th><th class=\"n\">Files</th></tr>"
        var folders: [DirNode] = []
        for c in scope.children.prefix(25) {
            folders.append(c)
            folders.append(contentsOf: c.children.prefix(3).filter { $0.size >= c.size / 10 })
        }
        for d in folders {
            let pct = 100 * Double(d.size) / Double(max(1, scope.size))
            let top = d.parent === scope
            let rel = top ? h(d.name) : "<span class=\"muted\">\(h(d.parent?.name ?? ""))/</span>" + h(d.name)
            sb += "<tr><td class=\"p\" style=\"padding-left:\(top ? 8 : 28)px\">\(rel)</td><td class=\"n\">\(Fmt.size(d.size))</td><td><div class=\"bar\"><i style=\"width:\(num1(min(100, pct)))%\"></i></div></td><td class=\"n muted\">\(Fmt.count(d.files))</td></tr>"
        }
        sb += "</table></div>"

        // Largest files.
        let files = FileIndex.largest(scope, 50, nil).files
        sb += "<h2>Largest files</h2><div class=\"card\"><table><tr><th>File</th><th class=\"n\">Size</th><th>Modified</th></tr>"
        for f in files {
            sb += "<tr><td class=\"p\">\(h(f.fullPath))</td><td class=\"n\">\(Fmt.size(f.size))</td><td class=\"muted\">\(Fmt.date(f.modified))</td></tr>"
        }
        sb += "</table></div>"

        // File types by category.
        var cats: [String: (Int64, Int64)] = [:]
        for d in scope.descendants() {
            for f in d.fileList ?? [] {
                let c = FileTypes.category(FileIndex.ext(f.name))
                let v = cats[c] ?? (0, 0)
                cats[c] = (v.0 + FileIndex.measure(f), v.1 + 1)
            }
        }
        sb += "<h2>File types</h2><div class=\"card\"><table><tr><th>Category</th><th class=\"n\">Size</th><th>Share</th><th class=\"n\">Files</th></tr>"
        for (k, v) in cats.sorted(by: { $0.value.0 > $1.value.0 }) {
            let pct = 100 * Double(v.0) / Double(max(1, scope.size))
            sb += "<tr><td>\(h(k))</td><td class=\"n\">\(Fmt.size(v.0))</td><td><div class=\"bar\"><i style=\"width:\(num1(min(100, pct)))%\"></i></div></td><td class=\"n muted\">\(Fmt.count(v.1))</td></tr>"
        }
        sb += "</table></div>"
        sb += "<p class=\"muted\" style=\"margin-top:32px\">Made with <a href=\"https://github.com/sphrd/DiskSpaceAnalyzer\">Disk Space Analyzer</a> \(h(version)).</p>"
        sb += "</body></html>"
        return sb
    }

    static func donutSvg(_ parts: [(String, Int64)]) -> String {
        let R = 90.0, r = 52.0, C = 100.0
        let total = max(1, parts.reduce(0.0) { $0 + Double($1.1) })
        var sb = "<svg width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">"
        var a = -Double.pi / 2
        func pt(_ rad: Double, _ ang: Double) -> String { String(format: "%.2f %.2f", C + rad * cos(ang), C + rad * sin(ang)) }
        for (i, p) in parts.enumerated() {
            var sweep = 2 * Double.pi * Double(p.1) / total
            if sweep <= 0 { continue }
            if sweep >= 2 * Double.pi - 1e-6 { sweep = 2 * Double.pi - 1e-4 }
            let b = a + sweep
            let large = sweep > Double.pi ? 1 : 0
            let color = i < parts.count - 1 || p.0 != "Other folders and files" ? hex(T.CHART[i % T.CHART.count]) : hex(T.CHART_OTHER)
            sb += "<path fill=\"\(color)\" stroke=\"\(hex(T.SURFACE))\" stroke-width=\"1.5\" d=\"M \(pt(R, a)) A 90 90 0 \(large) 1 \(pt(R, b)) L \(pt(r, b)) A 52 52 0 \(large) 0 \(pt(r, a)) Z\"><title>\(h(p.0))</title></path>"
            a = b
        }
        return sb + "</svg>"
    }
}
