import AppKit

/// Folder sizes of an earlier scan (folders only, no file names), kept to show what changed since.
final class PrevNode {
    var name = ""
    var bytes: Int64 = 0, alloc: Int64 = 0, files: Int64 = 0
    var children: [String: PrevNode]?
    var size: Int64 { DirNode.onDisk ? alloc : bytes }
    func child(_ n: String) -> PrevNode? { children?[n] }
}

/// Saves a compact copy of every full scan (folder names and sizes, zlib-compressed binary) in
/// ~/Library/Application Support/DiskSpaceAnalyzer/history, one file per scanned drive or folder, and loads the previous
/// one for comparison. Nothing leaves the computer.
enum History {
    static let magic: UInt32 = 0x31415344   // "DSA1"

    static var folder: String {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first?.path ?? NSHomeDirectory() + "/Library/Application Support"
        return base + "/DiskSpaceAnalyzer/history"
    }

    static func file(for rootPath: String) -> String {
        var s = ""
        for ch in PathUtil.trim(rootPath).lowercased() { s.append(ch.isLetter || ch.isNumber ? ch : "_") }
        return folder + "/" + s + ".dsa"
    }

    /// Pre-order, children in list order: name, bytes, size on disk, files, number of children. Call on the main thread.
    static func serialize(_ rootPath: String, _ root: DirNode) -> Data {
        var w = BinWriter()
        w.u32(magic)
        w.f64(Date().timeIntervalSince1970)
        w.str(rootPath)
        for d in root.descendants() {
            w.str(d.name); w.i64(d.bytes); w.i64(d.alloc); w.i64(d.files); w.u32(UInt32(d.children.count))
        }
        return w.data
    }

    static func save(_ rootPath: String, _ data: Data) throws {
        try FileManager.default.createDirectory(atPath: folder, withIntermediateDirectories: true)
        let packed = try (data as NSData).compressed(using: .zlib) as Data
        try packed.write(to: URL(fileURLWithPath: file(for: rootPath)), options: .atomic)
    }

    static func load(_ rootPath: String) -> (PrevNode, Date)? {
        guard let packed = FileManager.default.contents(atPath: file(for: rootPath)),
              let raw = try? (packed as NSData).decompressed(using: .zlib) as Data else { return nil }
        var r = BinReader([UInt8](raw))
        guard r.u32() == magic else { return nil }
        let when = Date(timeIntervalSince1970: r.f64())
        _ = r.str()
        // Iterative: very deep trees would overflow the stack.
        let root = PrevNode()
        var stack: [(PrevNode, Int)] = [(root, fill(&r, root))]
        while let top = stack.popLast() {
            if r.failed { return nil }
            if top.1 == 0 { continue }
            stack.append((top.0, top.1 - 1))
            let c = PrevNode()
            let cc = fill(&r, c)
            if top.0.children == nil { top.0.children = [:] }
            top.0.children![c.name] = c
            stack.append((c, cc))
        }
        return r.failed ? nil : (root, when)
    }

    private static func fill(_ r: inout BinReader, _ n: PrevNode) -> Int {
        n.name = r.str(); n.bytes = r.i64(); n.alloc = r.i64(); n.files = r.i64()
        return Int(r.u32())
    }
}

struct BinWriter {
    var data = Data()
    mutating func u32(_ v: UInt32) { var x = v.littleEndian; withUnsafeBytes(of: &x) { data.append(contentsOf: $0) } }
    mutating func i64(_ v: Int64) { var x = v.littleEndian; withUnsafeBytes(of: &x) { data.append(contentsOf: $0) } }
    mutating func f64(_ v: Double) { i64(Int64(bitPattern: v.bitPattern)) }
    mutating func str(_ s: String) { let u = Array(s.utf8); u32(UInt32(u.count)); data.append(contentsOf: u) }
}

struct BinReader {
    let b: [UInt8]
    var i = 0
    var failed = false
    init(_ bytes: [UInt8]) { b = bytes }
    mutating func u32() -> UInt32 {
        guard i + 4 <= b.count else { failed = true; return 0 }
        var v: UInt32 = 0
        for k in 0..<4 { v |= UInt32(b[i + k]) << (8 * UInt32(k)) }
        i += 4
        return v
    }
    mutating func i64() -> Int64 {
        guard i + 8 <= b.count else { failed = true; return 0 }
        var v: UInt64 = 0
        for k in 0..<8 { v |= UInt64(b[i + k]) << (8 * UInt64(k)) }
        i += 8
        return Int64(bitPattern: v)
    }
    mutating func f64() -> Double { Double(bitPattern: UInt64(bitPattern: i64())) }
    mutating func str() -> String {
        let n = Int(u32())
        guard !failed, i + n <= b.count else { failed = true; return "" }
        let s = String(decoding: b[i..<(i + n)], as: UTF8.self)
        i += n
        return s
    }
}

extension MainWindowController {
    func buildChangesPage() -> Page {
        let p = Page("Changes", [Column("name", "Folder", 270), Column("change", "Change", 90, true), Column("size", "Now", 80, true),
                                 Column("files", "Before", 80, true)], barColumn: -1, bar: nil)
        p.chartTitle = "Growth"
        p.chartScope = "of the growth"
        p.empty = "Nothing grew since the previous scan."
        p.fill = { [unowned self] in self.fillChanges() }
        return p
    }

    /// After a full scan: keep the previous scan for comparison and save this one (written in the background).
    func rememberScan(_ r: ScanRoot, _ result: DirNode, complete: Bool) {
        guard complete else { return }
        let path = r.path
        let data = History.serialize(path, result)
        DispatchQueue.global(qos: .utility).async {
            let prev = History.load(path)
            try? History.save(path, data)
            DispatchQueue.main.async {
                if r.previous == nil || prev != nil { r.previous = prev?.0; r.previousTime = prev?.1 ?? .distantPast }
                if let c = self.current, c.top === r.node, self.activePage === self.pContents { self.showContents(c) }
                self.setStale(self.pChanges)
            }
        }
    }

    /// The previous scan's node for a scanned folder (nil when unknown).
    func prevOf(_ d: DirNode) -> PrevNode? {
        guard let r = rootOf(d.top), let prev = r.previous else { return nil }
        var chain: [String] = []
        var n: DirNode? = d
        while let x = n, x.parent != nil { chain.append(x.name); n = x.parent }
        var p: PrevNode? = prev
        for name in chain.reversed() { p = p?.child(name); if p == nil { break } }
        return p
    }

    /// Change of each child folder of d since the previous scan, for the Change column.
    func setChanges(_ d: DirNode, _ items: [ItemInfo]) {
        guard let prev = prevOf(d) else { return }
        for it in items where it.isDir {
            guard let dn = it.dir else { continue }
            let pc = prev.child(dn.name)
            it.change = pc != nil ? dn.size - pc!.size : dn.size
            if pc == nil { it.note = "new" }
        }
    }

    func changeText(_ it: ItemInfo) -> String {
        if it.change == Int64.min { return "" }
        if it.note == "new" { return "new" }
        return Fmt.delta(it.change)
    }

    func changeCaption(_ d: DirNode) -> String {
        guard let r = rootOf(d.top), let prev = prevOf(d) else { return "" }
        let delta = d.size - prev.size
        if delta == 0 { return "  -  no change since the scan of \(Fmt.date(r.previousTime))" }
        return "  -  \(Fmt.delta(delta)) since the scan of \(Fmt.date(r.previousTime))"
    }

    func fillChanges() {
        let p = pChanges!
        p.chartTitle = "Growth"
        guard let r = active, let rootNode = r.node, let previous = r.previous else {
            p.setCaption(active?.node == nil ? "Nothing scanned yet."
                : "No earlier scan of \(active!.path) to compare with yet. Every full scan is remembered; scan again later to see what changed.")
            p.chartSub = ""
            p.empty = "No earlier scan to compare with."
            p.setRows([], "No earlier scan of this drive or folder to compare with yet.\nScan it again later to see which folders grew or shrank.")
            if activePage === p { refreshChart() }
            return
        }
        // A folder is listed when its change is big enough and not mostly explained by one of its subfolders.
        let threshold = max(Int64(10 << 20), rootNode.size / 1000)
        var found: [(DirNode, PrevNode?)] = []
        var removed: [(String, PrevNode)] = []
        var stack: [(DirNode, PrevNode?)] = [(rootNode, previous)]
        while let top = stack.popLast() {
            let (d, pn) = top
            let delta = d.size - (pn?.size ?? 0)
            if abs(delta) < threshold { continue }
            var explained = false
            for c in d.children {
                let pc = pn?.child(c.name)
                let cd = c.size - (pc?.size ?? 0)
                if abs(cd) >= threshold { stack.append((c, pc)) }
                if Double(abs(cd)) >= Double(abs(delta)) * 0.8 && cd.signum() == delta.signum() { explained = true }
            }
            if let kids = pn?.children {
                for pc in kids.values where d.child(pc.name) == nil && pc.size >= threshold {
                    removed.append((PathUtil.join(d.fullPath, pc.name), pc))
                    if Double(pc.size) >= Double(abs(delta)) * 0.8 && delta < 0 { explained = true }
                }
            }
            if !explained { found.append((d, pn)) }
        }
        var rows: [ItemInfo] = []
        var grew: Int64 = 0, shrank: Int64 = 0
        found.sort { abs($0.0.size - ($0.1?.size ?? 0)) > abs($1.0.size - ($1.1?.size ?? 0)) }
        for (d, pn) in found.prefix(500) {
            let before = pn?.size ?? 0, delta = d.size - before
            let it = ItemInfo.forDir(d, 0)
            it.name = d.fullPath
            it.change = delta
            it.files = before
            it.chartValue = max(0, delta)
            if pn == nil { it.note = "new" }
            if delta > 0 { grew += delta } else { shrank -= delta }
            it.cells = [it.name, changeText(it), Fmt.size(d.size), pn != nil ? Fmt.size(before) : "new"]
            rows.append(it)
        }
        removed.sort { $0.1.size > $1.1.size }
        for (path, pc) in removed.prefix(100) {
            let it = ItemInfo()
            it.name = path; it.change = -pc.size; it.files = pc.size; it.folderIcon = true; it.chartValue = 0
            shrank += pc.size
            it.cells = [it.name, changeText(it), "removed", Fmt.size(pc.size)]
            it.color = T.MUTED
            rows.append(it)
        }
        rows.sort { abs($0.change) > abs($1.change) }
        let total = rootNode.size - previous.size
        p.setRows(rows, "No folder changed by more than \(Fmt.size(threshold)) since the previous scan.", keepOrder: true)
        p.setCaption("Since the scan of \(Fmt.date(r.previousTime)): \(total >= 0 ? "+" : "-")\(Fmt.size(abs(total))) in total  -  folders that changed by \(Fmt.size(threshold)) or more (grew \(Fmt.size(grew)), shrank \(Fmt.size(shrank))).")
        p.chartSub = "+\(Fmt.size(grew)) since \(Fmt.day(r.previousTime))"
        p.chartTotal = 0
        p.empty = "Nothing grew since the previous scan."
        if activePage === p { refreshChart() }
    }
}
