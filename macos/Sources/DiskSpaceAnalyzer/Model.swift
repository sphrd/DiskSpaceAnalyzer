import Foundation
import Darwin

/// One file in the in-memory index (kept per folder, so searches never touch the disk).
struct FileRec {
    var name: String
    var bytes: Int64          // file size
    var alloc: Int64          // size on disk (0 for files not downloaded from iCloud and for extra hard links)
    var modified: Int64       // last modification, seconds since 1970
    var flags: UInt32

    static let datalessFlag: UInt32 = 1      // evicted iCloud / File Provider file: nothing on the disk
    static let hardLinkFlag: UInt32 = 2      // another name of a file already counted in this scan
    var dataless: Bool { flags & FileRec.datalessFlag != 0 }
}

/// A scanned folder with its totals and the index of its own files.
final class DirNode {
    /// Which number every view uses: false = file size, true = size on disk.
    static var onDisk = true

    var name: String
    weak var parent: DirNode?
    var children: [DirNode] = []
    var bytes: Int64 = 0, alloc: Int64 = 0, files: Int64 = 0, dirs: Int64 = 0
    var localBytes: Int64 = 0, localAlloc: Int64 = 0, localFiles: Int64 = 0
    var fileList: [FileRec]?          // nil when the folder could not be read
    var modified: Int64 = 0
    var denied = false
    var pending = 0                   // children not finished yet (guarded by the scanner lock)

    init(name: String, parent: DirNode?) { self.name = name; self.parent = parent }

    var size: Int64 { DirNode.onDisk ? alloc : bytes }
    var localSize: Int64 { DirNode.onDisk ? localAlloc : localBytes }

    var fullPath: String {
        var parts: [String] = []
        var n: DirNode? = self
        while let c = n { parts.append(c.name); n = c.parent }
        var path = parts.removeLast()
        for p in parts.reversed() { path = path.hasSuffix("/") ? path + p : path + "/" + p }
        return path
    }

    var top: DirNode { var n = self; while let p = n.parent { n = p }; return n }

    func isUnder(_ ancestor: DirNode) -> Bool {
        var n: DirNode? = self
        while let c = n { if c === ancestor { return true }; n = c.parent }
        return false
    }

    static func bySize(_ a: DirNode, _ b: DirNode) -> Bool { a.size > b.size }

    /// Totals from this folder's own files plus its (finished) children; children sorted largest first.
    func aggregate() {
        var b = localBytes, a = localAlloc, f = localFiles, d: Int64 = 0
        for c in children { b += c.bytes; a += c.alloc; f += c.files; d += c.dirs + 1 }
        children.sort(by: DirNode.bySize)
        bytes = b; alloc = a; files = f; dirs = d
    }

    /// Adds a change to this folder and all its ancestors, and keeps sibling order by size.
    func addUp(_ b: Int64, _ a: Int64, _ f: Int64, _ d: Int64) {
        var n: DirNode? = self
        while let x = n {
            x.bytes += b; x.alloc += a; x.files += f; x.dirs += d
            x.children.sort(by: DirNode.bySize)
            n = x.parent
        }
    }

    /// This folder and every folder below it, pre-order, children in list order (iterative: trees can be very deep).
    func descendants() -> [DirNode] {
        var out: [DirNode] = []
        var stack: [DirNode] = [self]
        while let n = stack.popLast() {
            out.append(n)
            var i = n.children.count - 1
            while i >= 0 { stack.append(n.children[i]); i -= 1 }
        }
        return out
    }

    /// Re-sorts every child list (after switching between file size and size on disk).
    func resortAll() { for n in descendants() { n.children.sort(by: DirNode.bySize) } }

    func child(_ name: String) -> DirNode? { children.first { $0.name == name } }

    /// Removes one file of this folder from the index and the totals. Returns false if it is not indexed.
    @discardableResult
    func removeFileRec(_ name: String) -> Bool {
        guard var list = fileList, let i = list.firstIndex(where: { $0.name == name }) else { return false }
        let f = list.remove(at: i)
        fileList = list
        localBytes -= f.bytes; localAlloc -= f.alloc; localFiles -= 1
        addUp(-f.bytes, -f.alloc, -1, 0)
        return true
    }

    /// Replaces this folder's own file list (children unchanged) and updates the totals up the tree.
    func setFiles(_ list: [FileRec]) {
        var b: Int64 = 0, a: Int64 = 0
        for f in list { b += f.bytes; a += f.alloc }
        let db = b - localBytes, da = a - localAlloc, df = Int64(list.count) - localFiles
        fileList = list
        localBytes = b; localAlloc = a; localFiles = Int64(list.count)
        addUp(db, da, df, 0)
    }
}

/// A file taken out of the index for display (a row in a list).
final class FileEntry {
    let name: String
    let dir: DirNode
    let bytes: Int64, alloc: Int64, modified: Int64
    let flags: UInt32

    init(_ d: DirNode, _ f: FileRec) { name = f.name; dir = d; bytes = f.bytes; alloc = f.alloc; modified = f.modified; flags = f.flags }

    var size: Int64 { DirNode.onDisk ? alloc : bytes }
    var dataless: Bool { flags & FileRec.datalessFlag != 0 }
    var fullPath: String { PathUtil.join(dir.fullPath, name) }
}

enum PathUtil {
    static func join(_ dir: String, _ name: String) -> String { dir.hasSuffix("/") ? dir + name : dir + "/" + name }
    static func trim(_ p: String) -> String { p.count > 1 && p.hasSuffix("/") ? String(p.dropLast()) : p }
    static func same(_ a: String, _ b: String) -> Bool { trim(a) == trim(b) }
    /// True when path is folder itself or inside it.
    static func isWithin(_ path: String, _ folder: String) -> Bool {
        let f = trim(folder), p = trim(path)
        return p == f || p.hasPrefix(f == "/" ? "/" : f + "/")
    }
    static func parent(_ p: String) -> String { (trim(p) as NSString).deletingLastPathComponent }
    static func name(_ p: String) -> String { (trim(p) as NSString).lastPathComponent }
    static func exists(_ p: String) -> Bool { var st = stat(); return lstat(p, &st) == 0 }
    static func isDir(_ p: String) -> Bool { var st = stat(); return lstat(p, &st) == 0 && (st.st_mode & S_IFMT) == S_IFDIR }
    static var home: String { NSHomeDirectory() }
}

/// Queries over the in-memory file index.
enum FileIndex {
    static func measure(_ f: FileRec) -> Int64 { DirNode.onDisk ? f.alloc : f.bytes }

    /// The n largest files under root (by the current measure) that pass the filter, largest first.
    static func largest(_ root: DirNode, _ n: Int, _ filter: FileFilter?) -> (files: [FileEntry], count: Int64, size: Int64) {
        var count: Int64 = 0, total: Int64 = 0
        var heap: [(Int64, FileEntry)] = []
        var threshold: Int64 = -1
        for d in root.descendants() {
            guard let list = d.fileList else { continue }
            for f in list {
                if let flt = filter, !flt.match(f) { continue }
                let s = measure(f)
                count += 1; total += s
                if heap.count >= n && s <= threshold { continue }
                push(&heap, s, FileEntry(d, f), n)
                threshold = heap.count >= n ? heap[0].0 : -1
            }
        }
        let result = heap.map { $0.1 }.sorted { $0.size > $1.size }
        return (result, count, total)
    }

    // Min-heap on the key, capped at n entries.
    private static func push(_ h: inout [(Int64, FileEntry)], _ key: Int64, _ v: FileEntry, _ n: Int) {
        if h.count < n {
            h.append((key, v))
            var i = h.count - 1
            while i > 0 { let p = (i - 1) / 2; if h[p].0 <= h[i].0 { break }; h.swapAt(p, i); i = p }
            return
        }
        h[0] = (key, v)
        var j = 0
        while true {
            let l = 2 * j + 1, r = l + 1
            var m = j
            if l < h.count && h[l].0 < h[m].0 { m = l }
            if r < h.count && h[r].0 < h[m].0 { m = r }
            if m == j { break }
            h.swapAt(m, j); j = m
        }
    }

    static func ext(_ name: String) -> String {
        guard let dot = name.lastIndex(of: "."), dot != name.startIndex else { return "" }
        let e = name[name.index(after: dot)...]
        if e.isEmpty || e.count > 11 { return "" }
        return e.lowercased()
    }
}

/// Filter for the Files view: name pattern, file type, minimum size, not modified for a while.
final class FileFilter {
    var patterns: [String] = []       // lower case; '*' and '?' wildcards, otherwise "contains"
    var category: String?             // nil = any
    var minSize: Int64 = 0
    var modifiedBefore: Int64 = 0     // seconds since 1970; 0 = any

    var isEmpty: Bool { patterns.isEmpty && category == nil && minSize <= 0 && modifiedBefore <= 0 }

    func match(_ f: FileRec) -> Bool {
        if minSize > 0 && FileIndex.measure(f) < minSize { return false }
        if modifiedBefore > 0 && (f.modified <= 0 || f.modified >= modifiedBefore) { return false }
        if let c = category, FileTypes.category(FileIndex.ext(f.name)) != c { return false }
        if !patterns.isEmpty {
            let lower = f.name.lowercased()
            if !patterns.contains(where: { FileFilter.nameMatches(lower, $0) }) { return false }
        }
        return true
    }

    /// name and pattern in lower case.
    static func nameMatches(_ name: String, _ pattern: String) -> Bool {
        if !pattern.contains("*") && !pattern.contains("?") { return name.contains(pattern) }
        return wildcard(Array(name.unicodeScalars), Array(pattern.unicodeScalars))
    }

    private static func wildcard(_ s: [Unicode.Scalar], _ p: [Unicode.Scalar]) -> Bool {
        var si = 0, pi = 0, star = -1, mark = 0
        while si < s.count {
            if pi < p.count && (p[pi] == "?" || p[pi] == s[si]) { si += 1; pi += 1 }
            else if pi < p.count && p[pi] == "*" { star = pi; pi += 1; mark = si }
            else if star >= 0 { pi = star + 1; mark += 1; si = mark }
            else { return false }
        }
        while pi < p.count && p[pi] == "*" { pi += 1 }
        return pi == p.count
    }
}

/// File categories by extension (for the Types view and the Files filter).
enum FileTypes {
    static let categories = ["Videos", "Images", "Audio", "Archives", "Disk images", "Installers", "Documents", "Code",
                             "Programs and libraries", "Databases", "Other"]

    private static let map: [String: String] = {
        var m: [String: String] = [:]
        func add(_ cat: String, _ exts: String) { for e in exts.split(separator: " ") { m[String(e)] = cat } }
        add("Videos", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg m2ts mts ts 3gp vob")
        add("Images", "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd svg ico icns")
        add("Audio", "mp3 wav flac aac ogg wma m4a opus aiff caf")
        add("Archives", "zip rar 7z tar gz tgz bz2 xz zst cab lz4 lzma xip")
        add("Disk images", "iso img vhd vhdx vmdk vdi qcow2 dmg wim esd ova sparseimage sparsebundle")
        add("Installers", "msi msix msixbundle appx appxbundle msp msu pkg mpkg")
        add("Documents", "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md csv epub pages numbers key")
        add("Code", "c h cpp hpp cs java py js ts tsx jsx go rs rb php swift kt json xml yml yaml html css scss sql sh ps1 m mm plist")
        add("Programs and libraries", "exe dll sys so dylib jar pyd lib a o obj pdb node")
        add("Databases", "db sqlite sqlite3 mdb accdb mdf ldf ndf bak edb")
        return m
    }()

    static func category(_ ext: String) -> String { ext.isEmpty ? "Other" : (map[ext] ?? "Other") }
}
