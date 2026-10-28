import Foundation
import Darwin

/// A scanned directory. Only directories are kept in memory; files are listed on demand (same model as Windows).
final class DirNode {
    var name: String
    weak var parent: DirNode?
    var children: [DirNode] = []
    var size: Int64 = 0, files: Int64 = 0, dirs: Int64 = 0
    var localSize: Int64 = 0
    var localFiles: Int64 = 0
    var denied = false
    fileprivate var pending = 0                       // children not finished yet (guarded by the scanner lock)

    init(name: String, parent: DirNode?) { self.name = name; self.parent = parent }

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

    /// Totals from this folder's own files plus its (finished) children; children sorted largest first.
    func aggregate() {
        var s = localSize, f = localFiles, d: Int64 = 0
        for c in children { s += c.size; f += c.files; d += c.dirs + 1 }
        children.sort { $0.size > $1.size }
        size = s; files = f; dirs = d
    }
}

struct FileEntry {
    let name: String
    unowned let dir: DirNode
    let size: Int64
    let modified: Date
    var fullPath: String { let p = dir.fullPath; return p.hasSuffix("/") ? p + name : p + "/" + name }
}

/// Bounded min-heap keeping the N largest files seen.
final class TopFiles {
    private var heap: [FileEntry] = []
    private let capacity: Int
    private let lock = NSLock()
    private var _threshold: Int64 = 0
    var threshold: Int64 { lock.lock(); defer { lock.unlock() }; return _threshold }

    init(capacity: Int) { self.capacity = max(1, capacity) }

    func offer(_ e: FileEntry) {
        lock.lock(); defer { lock.unlock() }
        if heap.count < capacity {
            heap.append(e); siftUp(heap.count - 1)
            if heap.count == capacity { _threshold = heap[0].size }
        } else if e.size > heap[0].size {
            heap[0] = e; siftDown(0); _threshold = heap[0].size
        }
    }

    func sorted() -> [FileEntry] { lock.lock(); defer { lock.unlock() }; return heap.sorted { $0.size > $1.size } }

    private func siftUp(_ i0: Int) {
        var i = i0
        while i > 0 { let p = (i - 1) / 2; if heap[p].size <= heap[i].size { break }; heap.swapAt(p, i); i = p }
    }

    private func siftDown(_ i0: Int) {
        var i = i0
        while true {
            let l = 2 * i + 1, r = l + 1; var m = i
            if l < heap.count && heap[l].size < heap[m].size { m = l }
            if r < heap.count && heap[r].size < heap[m].size { m = r }
            if m == i { break }
            heap.swapAt(m, i); i = m
        }
    }
}

/// Paths where another file system is mounted (from getmntinfo).
func mountPoints() -> Set<String> {
    var result = Set<String>()
    var buf: UnsafeMutablePointer<statfs>?
    let n = getmntinfo(&buf, MNT_NOWAIT)
    guard n > 0, let mounts = buf else { return result }
    for i in 0..<Int(n) {
        var m = mounts[i]
        let path = withUnsafePointer(to: &m.f_mntonname) { $0.withMemoryRebound(to: CChar.self, capacity: Int(MAXPATHLEN)) { String(cString: $0) } }
        result.insert(path)
    }
    return result
}

/// Parallel scanner. Worker threads take directories from a shared LIFO stack and list each one with getattrlistbulk
/// (one system call returns many entries with their type and size). A directory's totals are aggregated as soon as its
/// last child finishes. Symbolic links are not followed and other mounted volumes are not entered: on the startup disk
/// this skips /System/Volumes/* (including the data volume, which the firmlinked /Users, /Applications... already
/// cover), /Volumes/* and /dev, so nothing is counted twice.
final class Scanner {
    private let lock = NSCondition()
    private var _files: Int64 = 0, _dirs: Int64 = 0, _bytes: Int64 = 0
    private var _current = ""
    private var _cancelled = false
    private var stack: [DirNode] = []
    private var finished = false
    private let top: TopFiles
    private var skip = Set<String>()

    private(set) var topFiles: [FileEntry] = []
    private(set) var rootPath = ""

    init(topCount: Int) { top = TopFiles(capacity: topCount) }

    var filesSeen: Int64 { lock.lock(); defer { lock.unlock() }; return _files }
    var dirsSeen: Int64 { lock.lock(); defer { lock.unlock() }; return _dirs }
    var bytesSeen: Int64 { lock.lock(); defer { lock.unlock() }; return _bytes }
    var current: String { lock.lock(); defer { lock.unlock() }; return _current }
    var cancelled: Bool { lock.lock(); defer { lock.unlock() }; return _cancelled }

    func cancel() { lock.lock(); _cancelled = true; lock.broadcast(); lock.unlock() }

    func run(_ path: String) -> DirNode {
        rootPath = path
        skip = mountPoints()
        skip.remove(path)
        skip.remove(path.hasSuffix("/") && path.count > 1 ? String(path.dropLast()) : path + "/")
        let root = DirNode(name: path, parent: nil)
        stack = [root]
        let n = min(32, max(8, 2 * ProcessInfo.processInfo.activeProcessorCount))
        let group = DispatchGroup()
        for i in 0..<n {
            group.enter()
            let t = Thread { [unowned self] in self.worker(); group.leave() }
            t.name = "scan-\(i)"
            t.stackSize = 1 << 20
            t.start()
        }
        group.wait()
        if cancelled { aggregateAll(root) }
        topFiles = top.sorted()
        return root
    }

    private func worker() {
        while true {
            lock.lock()
            while stack.isEmpty && !finished && !_cancelled { lock.wait() }
            if finished || _cancelled { lock.unlock(); return }
            let node = stack.removeLast()
            lock.unlock()
            process(node)
        }
    }

    /// Lists one directory. getattrlistbulk returns, per entry: length (u32), returned attribute set, then (because it
    /// is requested) the error code (u32) right after it, then the other requested attributes in the order of their
    /// bits: name (attrreference), object type (u32), modification time (timespec), and for files the data length
    /// (off_t). Fields are only 4-byte aligned, so every read is unaligned.
    private func process(_ node: DirNode) {
        let path = node.fullPath
        lock.lock(); _current = path; lock.unlock()
        var subs: [DirNode] = []
        var size: Int64 = 0, files: Int64 = 0

        let fd = open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        if fd < 0 {
            if errno != ENOENT { node.denied = true }
        } else {
            defer { close(fd) }
            var req = attrlist()
            req.bitmapcount = u_short(ATTR_BIT_MAP_COUNT)
            req.commonattr = attrgroup_t(ATTR_CMN_RETURNED_ATTRS) | attrgroup_t(ATTR_CMN_NAME) | attrgroup_t(ATTR_CMN_ERROR)
                | attrgroup_t(ATTR_CMN_OBJTYPE) | attrgroup_t(ATTR_CMN_MODTIME)
            req.fileattr = attrgroup_t(ATTR_FILE_DATALENGTH)
            let bufSize = 256 * 1024
            let buf = UnsafeMutableRawPointer.allocate(byteCount: bufSize, alignment: 16)
            defer { buf.deallocate() }
            var threshold = top.threshold
            var stop = false
            while !stop {
                let count = getattrlistbulk(fd, &req, buf, bufSize, 0)
                if count < 0 { node.denied = true; break }
                if count == 0 { break }
                var entry = UnsafeRawPointer(buf)
                for _ in 0..<Int(count) {
                    let length = Int(entry.loadUnaligned(as: UInt32.self))
                    defer { entry += length }
                    var p = entry + 4
                    let returned = p.loadUnaligned(as: attribute_set_t.self)
                    p += MemoryLayout<attribute_set_t>.size
                    if returned.commonattr & attrgroup_t(ATTR_CMN_ERROR) != 0 {
                        let err = p.loadUnaligned(as: UInt32.self)
                        p += 4
                        if err != 0 { continue }
                    }
                    guard returned.commonattr & attrgroup_t(ATTR_CMN_NAME) != 0 else { continue }
                    let nameRef = p.loadUnaligned(as: attrreference_t.self)
                    let namePtr = (p + Int(nameRef.attr_dataoffset)).assumingMemoryBound(to: CChar.self)
                    p += MemoryLayout<attrreference_t>.size
                    var type: UInt32 = 0
                    if returned.commonattr & attrgroup_t(ATTR_CMN_OBJTYPE) != 0 {
                        type = p.loadUnaligned(as: UInt32.self); p += 4
                    }
                    var mtime = timespec()
                    if returned.commonattr & attrgroup_t(ATTR_CMN_MODTIME) != 0 {
                        mtime = p.loadUnaligned(as: timespec.self); p += MemoryLayout<timespec>.size
                    }
                    if type == VDIR.rawValue {
                        let name = String(cString: namePtr)
                        let child = path.hasSuffix("/") ? path + name : path + "/" + name
                        if skip.contains(child) { continue }               // another volume is mounted here
                        subs.append(DirNode(name: name, parent: node))
                    } else if type == VREG.rawValue {
                        var s: Int64 = 0
                        if returned.fileattr & attrgroup_t(ATTR_FILE_DATALENGTH) != 0 { s = Int64(p.loadUnaligned(as: off_t.self)) }
                        size += s
                        files += 1
                        if s > threshold {
                            let date = Date(timeIntervalSince1970: TimeInterval(mtime.tv_sec))
                            top.offer(FileEntry(name: String(cString: namePtr), dir: node, size: s, modified: date))
                            threshold = top.threshold
                        }
                    } else if type != VLNK.rawValue {
                        files += 1                                          // sockets, pipes, devices: counted, no size
                    }
                }
                if cancelled { stop = true }
            }
        }

        node.localSize = size
        node.localFiles = files
        node.children = subs

        lock.lock()
        _bytes += size; _files += files; _dirs += 1
        if !subs.isEmpty && !_cancelled {
            node.pending = subs.count
            stack.append(contentsOf: subs)
            lock.broadcast()
            lock.unlock()
        } else {
            lock.unlock()
            complete(node)
        }
    }

    private func complete(_ start: DirNode) {
        var node = start
        while true {
            node.aggregate()
            guard let p = node.parent else {
                lock.lock(); finished = true; lock.broadcast(); lock.unlock()
                return
            }
            lock.lock()
            p.pending -= 1
            let done = p.pending == 0
            lock.unlock()
            if !done { return }
            node = p
        }
    }

    private func aggregateAll(_ root: DirNode) {
        var order: [DirNode] = []
        var st = [root]
        while let n = st.popLast() { order.append(n); st.append(contentsOf: n.children) }
        for n in order.reversed() { n.aggregate() }
    }

    /// Files (not folders) directly inside a directory, for the contents list.
    static func listFiles(_ dir: DirNode) -> [FileEntry] {
        var list: [FileEntry] = []
        let base = dir.fullPath
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: base) else { return list }
        for n in names {
            var st = stat()
            let p = base.hasSuffix("/") ? base + n : base + "/" + n
            if lstat(p, &st) != 0 { continue }
            if (st.st_mode & S_IFMT) == S_IFREG {
                list.append(FileEntry(name: n, dir: dir, size: Int64(st.st_size),
                                      modified: Date(timeIntervalSince1970: TimeInterval(st.st_mtimespec.tv_sec))))
            }
        }
        return list
    }
}
