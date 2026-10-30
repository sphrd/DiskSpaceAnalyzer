import Foundation
import Darwin

/// Something to delete: a file, a folder, only the contents of a folder, or the Trash itself (emptied).
final class DeleteTarget {
    var path = "", label = "", note = ""
    var isDir = false, contentsOnly = false, emptyTrash = false
    var size: Int64 = 0
    var dir: DirNode?            // the scanned folder, when known
    var file: FileEntry?         // the scanned file, when known
    var exclude = Set<String>()  // names kept when only the contents are deleted

    init() {}
    init(path: String, label: String, isDir: Bool = false, size: Int64 = 0, note: String = "") {
        self.path = path; self.label = label; self.isDir = isDir; self.size = size; self.note = note
    }

    static func from(_ it: ItemInfo) -> [DeleteTarget] {
        if let t = it.targets { return t }
        if it.isDir, let d = it.dir {
            let t = DeleteTarget(path: d.fullPath, label: d.name, isDir: true, size: d.size)
            t.dir = d
            return [t]
        }
        if let f = it.file {
            let t = DeleteTarget(path: f.fullPath, label: f.name, size: f.size)
            t.file = f
            return [t]
        }
        return []
    }
}

/// Deletion, permanent or to the Trash. Symbolic links are removed as links; their targets are never touched. Locked
/// (uchg) flags are cleared so deletes don't get stuck. When only the contents of a folder are deleted (caches, logs),
/// items that cannot be removed (in use, protected) are skipped quietly and counted; other errors are collected.
/// While it runs, the window polls `drain()` for the paths deleted so far and the targets finished, so the open list
/// can drop their rows as it happens. stop() finishes the current file, then stops (the rest stays).
final class DeleteJob {
    private let lock = NSLock()
    private var _current = "", _files: Int64 = 0, _recycled: Int64 = 0, _skipped: Int64 = 0
    private var _errors: [String] = []
    private var _failed: [String] = []
    private var _bytes: Int64 = 0, _total: Int64 = 0
    private var _stop = false
    private var _gone: [String] = []
    private var _finished: [String] = []
    private var quiet = false
    var trash = false

    /// Pause after each item (microseconds): the self-test and the screenshots slow a delete down to watch it.
    static var throttle: useconds_t = 0

    var current: String { get { lock.lock(); defer { lock.unlock() }; return _current } set { lock.lock(); _current = newValue; lock.unlock() } }
    var filesDeleted: Int64 { lock.lock(); defer { lock.unlock() }; return _files }
    var recycled: Int64 { lock.lock(); defer { lock.unlock() }; return _recycled }
    var skipped: Int64 { lock.lock(); defer { lock.unlock() }; return _skipped }
    var errors: [String] { lock.lock(); defer { lock.unlock() }; return _errors }
    /// Items that stayed (in use, protected, no permission), skipped ones included; not the folders that held them.
    var failedPaths: [String] { lock.lock(); defer { lock.unlock() }; return _failed }
    /// Bytes deleted (or moved to the Trash) so far, and the size of all the targets (0 = unknown).
    var bytesDone: Int64 { lock.lock(); defer { lock.unlock() }; return _bytes }
    var totalBytes: Int64 { lock.lock(); defer { lock.unlock() }; return _total }
    var stopped: Bool { lock.lock(); defer { lock.unlock() }; return _stop }

    func stop() { lock.lock(); _stop = true; lock.unlock() }

    /// The paths deleted (files, folders, items moved to the Trash) and the targets finished since the last call.
    func drain() -> (gone: [String], finished: [String]) {
        lock.lock(); defer { lock.unlock() }
        let r = (_gone, _finished)
        _gone = []; _finished = []
        return r
    }

    private func gone(_ p: String) { lock.lock(); _gone.append(p); lock.unlock() }
    private func addBytes(_ n: Int64) { lock.lock(); _bytes += n; lock.unlock() }

    func run(_ targets: [DeleteTarget], trash: Bool) {
        lock.lock(); _total = targets.reduce(Int64(0)) { $0 + max(0, $1.size) }; lock.unlock()
        for t in targets {
            if stopped { break }
            current = t.emptyTrash ? "Trash" : t.path
            quiet = t.contentsOnly || t.emptyTrash
            if t.emptyTrash { deleteContents(t.path, t.exclude) }
            else if trash {
                moveToTrash(t.contentsOnly ? DeleteJob.children(t.path, t.exclude) : [t.path])
                addBytes(max(0, t.size))
            }
            else if t.contentsOnly { deleteContents(t.path, t.exclude) }
            else { delete(t.path) }
            quiet = false
            if !stopped { lock.lock(); _finished.append(t.path); lock.unlock() }
        }
    }

    static func children(_ dir: String, _ exclude: Set<String> = []) -> [String] {
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: dir) else { return [] }
        return names.filter { !exclude.contains($0) }.map { PathUtil.join(dir, $0) }
    }

    private func moveToTrash(_ paths: [String]) {
        for p in paths {
            if stopped { return }
            current = p
            do {
                try FileManager.default.trashItem(at: URL(fileURLWithPath: p), resultingItemURL: nil)
                lock.lock(); _recycled += 1; lock.unlock()
                gone(p)
            } catch {
                lock.lock(); _failed.append(p); lock.unlock()
                if quiet { lock.lock(); _skipped += 1; lock.unlock() }
                else { lock.lock(); _errors.append(p + "  -  could not be moved to the Trash (" + error.localizedDescription + ")"); lock.unlock() }
            }
            if DeleteJob.throttle > 0 { usleep(DeleteJob.throttle) }
        }
    }

    func delete(_ path: String) {
        var st = stat()
        if lstat(path, &st) != 0 { error(path, errno); return }
        if (st.st_mode & S_IFMT) == S_IFDIR { deleteTree(path) } else { deleteFile(path, st) }
    }

    /// Deletes everything inside a folder and keeps the folder.
    private func deleteContents(_ dir: String, _ exclude: Set<String>) {
        for p in DeleteJob.children(dir, exclude) {
            if stopped { return }
            var st = stat()
            if lstat(p, &st) != 0 { continue }
            if (st.st_mode & S_IFMT) == S_IFDIR { deleteTree(p) } else { deleteFile(p, st) }
        }
    }

    private func deleteTree(_ dir: String) {
        current = dir
        // A folder not downloaded from iCloud cannot be listed while downloads are off; allow fetching its names here
        // (the files inside stay in the cloud and are only removed).
        let list = { try? FileManager.default.contentsOfDirectory(atPath: dir) }
        if let names = Dataless.isDataless(dir) ? Dataless.allowingDownloads(list) : list() {
            for n in names {
                if stopped { return }
                let p = PathUtil.join(dir, n)
                var st = stat()
                if lstat(p, &st) != 0 { error(p, errno); continue }
                if (st.st_mode & S_IFMT) == S_IFDIR { deleteTree(p) } else { deleteFile(p, st) }
            }
        }
        if stopped { return }
        if rmdir(dir) != 0 {
            let e = errno
            if chflags(dir, 0) == 0 && rmdir(dir) == 0 { gone(dir); return }
            error(dir, e)
        } else { gone(dir) }
    }

    private func deleteFile(_ path: String, _ st: stat) {
        current = path
        if st.st_flags & UInt32(UF_IMMUTABLE) != 0 { lchflags(path, 0) }
        if unlink(path) == 0 {
            let size = DirNode.onDisk ? Int64(st.st_blocks) * 512 : Int64(st.st_size)
            lock.lock(); _files += 1; _bytes += size; _gone.append(path); lock.unlock()
        }
        else { error(path, errno) }
        if DeleteJob.throttle > 0 { usleep(DeleteJob.throttle) }
    }

    private func error(_ path: String, _ code: Int32) {
        lock.lock(); defer { lock.unlock() }
        if code != ENOTEMPTY { _failed.append(path) }
        if quiet { _skipped += 1; return }
        _errors.append(path + "  -  " + String(cString: strerror(code)))
    }
}
