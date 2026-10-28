import Foundation
import Darwin

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
/// (one system call returns many entries with their type, size, size on disk, flags and file id). A directory's totals
/// are aggregated as soon as its last child finishes. Symbolic links are not followed and other mounted volumes are not
/// entered: on the startup disk this skips /System/Volumes/* (including the data volume, which the firmlinked /Users,
/// /Applications... already cover), /Volumes/* and /dev, so nothing is counted twice. Every folder keeps the list of its
/// own files (the index the Files, File types and Duplicates views search). A file with several hard links counts its
/// size on disk once per scan; files evicted to iCloud (dataless) count 0 on disk.
final class Scanner {
    private let lock = NSCondition()
    private var _files: Int64 = 0, _dirs: Int64 = 0, _bytes: Int64 = 0
    private var _current = ""
    private var _cancelled = false
    private var stack: [DirNode] = []
    private var finished = false
    private var skip = Set<String>()
    private let linkLock = NSLock()
    private var seenLinks = Set<UInt64>()

    private(set) var rootPath = ""

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
        if cancelled { Scanner.aggregateAll(root) }
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

    /// True the first time a file id with several links is seen in this scan.
    private func firstLink(_ id: UInt64) -> Bool {
        linkLock.lock(); defer { linkLock.unlock() }
        return seenLinks.insert(id).inserted
    }

    private func process(_ node: DirNode) {
        let path = node.fullPath
        lock.lock(); _current = path; lock.unlock()
        var subs: [DirNode] = []
        let result = Scanner.readFolder(path, wantDirs: true, skip: skip, cancelled: { self.cancelled },
                                        firstLink: { id in self.firstLink(id) })
        if let list = result.files {
            var b: Int64 = 0, a: Int64 = 0
            for f in list { b += f.bytes; a += f.alloc }
            node.fileList = list
            node.localBytes = b; node.localAlloc = a; node.localFiles = Int64(list.count)
        }
        node.denied = result.denied
        for (name, mtime) in result.dirs {
            let c = DirNode(name: name, parent: node)
            c.modified = mtime
            subs.append(c)
        }
        node.children = subs

        lock.lock()
        _bytes += node.localBytes; _files += node.localFiles; _dirs += 1
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

    static func aggregateAll(_ root: DirNode) {
        for n in root.descendants().reversed() { n.aggregate() }
    }

    static let sfDataless: UInt32 = 0x40000000          // SF_DATALESS in <sys/stat.h>

    /// Lists one directory. getattrlistbulk returns, per entry: length (u32), returned attribute set, the error code
    /// (u32, because it is requested), then the other requested attributes in the order of their bits: name
    /// (attrreference), object type (u32), modification time (timespec), flags (u32), file id (u64); and for files the
    /// link count (u32), allocated size (off_t) and data length (off_t). Fields are only 4-byte aligned, so every read is
    /// unaligned. files == nil when the folder cannot be read.
    static func readFolder(_ path: String, wantDirs: Bool, skip: Set<String> = [], cancelled: () -> Bool = { false },
                           firstLink: (UInt64) -> Bool) -> (files: [FileRec]?, dirs: [(String, Int64)], denied: Bool) {
        var files: [FileRec] = []
        var dirs: [(String, Int64)] = []
        let fd = open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        if fd < 0 { return (nil, [], errno != ENOENT) }
        defer { close(fd) }
        var req = attrlist()
        req.bitmapcount = u_short(ATTR_BIT_MAP_COUNT)
        req.commonattr = attrgroup_t(ATTR_CMN_RETURNED_ATTRS) | attrgroup_t(ATTR_CMN_NAME) | attrgroup_t(ATTR_CMN_ERROR)
            | attrgroup_t(ATTR_CMN_OBJTYPE) | attrgroup_t(ATTR_CMN_MODTIME) | attrgroup_t(ATTR_CMN_FLAGS) | attrgroup_t(ATTR_CMN_FILEID)
        req.fileattr = attrgroup_t(ATTR_FILE_LINKCOUNT) | attrgroup_t(ATTR_FILE_ALLOCSIZE) | attrgroup_t(ATTR_FILE_DATALENGTH)
        let bufSize = 256 * 1024
        let buf = UnsafeMutableRawPointer.allocate(byteCount: bufSize, alignment: 16)
        defer { buf.deallocate() }
        var failed = false
        while true {
            let count = getattrlistbulk(fd, &req, buf, bufSize, 0)
            if count < 0 { failed = true; break }
            if count == 0 { break }
            var entry = UnsafeRawPointer(buf)
            for _ in 0..<Int(count) {
                let entryLength = Int(entry.loadUnaligned(as: UInt32.self))
                defer { entry += entryLength }
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
                if returned.commonattr & attrgroup_t(ATTR_CMN_OBJTYPE) != 0 { type = p.loadUnaligned(as: UInt32.self); p += 4 }
                var mtime: Int64 = 0
                if returned.commonattr & attrgroup_t(ATTR_CMN_MODTIME) != 0 {
                    mtime = Int64(p.loadUnaligned(as: timespec.self).tv_sec); p += MemoryLayout<timespec>.size
                }
                var flags: UInt32 = 0
                if returned.commonattr & attrgroup_t(ATTR_CMN_FLAGS) != 0 { flags = p.loadUnaligned(as: UInt32.self); p += 4 }
                var fileId: UInt64 = 0
                if returned.commonattr & attrgroup_t(ATTR_CMN_FILEID) != 0 { fileId = p.loadUnaligned(as: UInt64.self); p += 8 }
                if type == VDIR.rawValue {
                    if !wantDirs { continue }
                    let name = String(cString: namePtr)
                    if !skip.isEmpty && skip.contains(PathUtil.join(path, name)) { continue }   // another volume is mounted here
                    dirs.append((name, mtime))
                } else if type == VLNK.rawValue {
                    continue
                } else {
                    var links: UInt32 = 1, allocSize: Int64 = 0, dataLen: Int64 = 0
                    if returned.fileattr & attrgroup_t(ATTR_FILE_LINKCOUNT) != 0 { links = p.loadUnaligned(as: UInt32.self); p += 4 }
                    if returned.fileattr & attrgroup_t(ATTR_FILE_ALLOCSIZE) != 0 { allocSize = Int64(p.loadUnaligned(as: off_t.self)); p += 8 }
                    if returned.fileattr & attrgroup_t(ATTR_FILE_DATALENGTH) != 0 { dataLen = Int64(p.loadUnaligned(as: off_t.self)); p += 8 }
                    var recFlags: UInt32 = 0
                    if type != VREG.rawValue { dataLen = 0; allocSize = 0 }             // sockets, pipes, devices: counted, no size
                    if flags & Scanner.sfDataless != 0 { allocSize = 0; recFlags |= FileRec.datalessFlag }
                    if links > 1 && fileId != 0 && !firstLink(fileId) { allocSize = 0; recFlags |= FileRec.hardLinkFlag }
                    files.append(FileRec(name: String(cString: namePtr), bytes: dataLen, alloc: allocSize, modified: mtime, flags: recFlags))
                }
            }
            if cancelled() { break }
        }
        if failed && files.isEmpty && dirs.isEmpty { return (nil, [], true) }
        return (files, dirs, failed)
    }

    /// Files (not folders) directly inside a directory, from the index.
    static func listFiles(_ dir: DirNode) -> [FileEntry] { (dir.fileList ?? []).map { FileEntry(dir, $0) } }

    /// Reads a folder's own files again (not its subfolders) and updates the index and totals. Extra hard links keep
    /// counting nothing on disk.
    @discardableResult
    static func refreshFiles(_ dir: DirNode) -> Bool {
        var linked = Set<String>()
        for f in dir.fileList ?? [] where f.flags & FileRec.hardLinkFlag != 0 { linked.insert(f.name) }
        let r = readFolder(dir.fullPath, wantDirs: false, firstLink: { _ in true })
        guard var list = r.files else { return false }
        if !linked.isEmpty {
            for i in list.indices where linked.contains(list[i].name) { list[i].alloc = 0; list[i].flags |= FileRec.hardLinkFlag }
        }
        dir.setFiles(list)
        return true
    }
}
