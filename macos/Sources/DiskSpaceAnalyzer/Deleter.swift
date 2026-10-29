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
final class DeleteJob {
    private let lock = NSLock()
    private var _current = "", _files: Int64 = 0, _recycled: Int64 = 0, _skipped: Int64 = 0
    private var _errors: [String] = []
    private var _failed: [String] = []
    private var quiet = false

    var current: String { get { lock.lock(); defer { lock.unlock() }; return _current } set { lock.lock(); _current = newValue; lock.unlock() } }
    var filesDeleted: Int64 { lock.lock(); defer { lock.unlock() }; return _files }
    var recycled: Int64 { lock.lock(); defer { lock.unlock() }; return _recycled }
    var skipped: Int64 { lock.lock(); defer { lock.unlock() }; return _skipped }
    var errors: [String] { lock.lock(); defer { lock.unlock() }; return _errors }
    /// Items that stayed (in use, protected, no permission), skipped ones included; not the folders that held them.
    var failedPaths: [String] { lock.lock(); defer { lock.unlock() }; return _failed }

    func run(_ targets: [DeleteTarget], trash: Bool) {
        for t in targets {
            current = t.emptyTrash ? "Trash" : t.path
            quiet = t.contentsOnly || t.emptyTrash
            if t.emptyTrash { deleteContents(t.path, t.exclude) }
            else if trash { moveToTrash(t.contentsOnly ? DeleteJob.children(t.path, t.exclude) : [t.path]) }
            else if t.contentsOnly { deleteContents(t.path, t.exclude) }
            else { delete(t.path) }
            quiet = false
        }
    }

    static func children(_ dir: String, _ exclude: Set<String> = []) -> [String] {
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: dir) else { return [] }
        return names.filter { !exclude.contains($0) }.map { PathUtil.join(dir, $0) }
    }

    private func moveToTrash(_ paths: [String]) {
        for p in paths {
            current = p
            do {
                try FileManager.default.trashItem(at: URL(fileURLWithPath: p), resultingItemURL: nil)
                lock.lock(); _recycled += 1; lock.unlock()
            } catch {
                lock.lock(); _failed.append(p); lock.unlock()
                if quiet { lock.lock(); _skipped += 1; lock.unlock() }
                else { lock.lock(); _errors.append(p + "  -  could not be moved to the Trash (" + error.localizedDescription + ")"); lock.unlock() }
            }
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
                let p = PathUtil.join(dir, n)
                var st = stat()
                if lstat(p, &st) != 0 { error(p, errno); continue }
                if (st.st_mode & S_IFMT) == S_IFDIR { deleteTree(p) } else { deleteFile(p, st) }
            }
        }
        if rmdir(dir) != 0 {
            let e = errno
            if chflags(dir, 0) == 0 && rmdir(dir) == 0 { return }
            error(dir, e)
        }
    }

    private func deleteFile(_ path: String, _ st: stat) {
        if st.st_flags & UInt32(UF_IMMUTABLE) != 0 { lchflags(path, 0) }
        if unlink(path) == 0 { lock.lock(); _files += 1; lock.unlock() }
        else { error(path, errno) }
    }

    private func error(_ path: String, _ code: Int32) {
        lock.lock(); defer { lock.unlock() }
        if code != ENOTEMPTY { _failed.append(path) }
        if quiet { _skipped += 1; return }
        _errors.append(path + "  -  " + String(cString: strerror(code)))
    }
}
