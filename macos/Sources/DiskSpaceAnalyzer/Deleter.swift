import Foundation
import Darwin

/// Permanent deletion (nothing goes to the Trash). Symbolic links are removed as links; their targets are never touched.
/// Locked (uchg) flags are cleared so deletes don't get stuck; errors are collected and reported, not fatal.
final class DeleteJob {
    private let lock = NSLock()
    private var _current = "", _files: Int64 = 0
    private(set) var errors: [String] = []

    var current: String { lock.lock(); defer { lock.unlock() }; return _current }
    var filesDeleted: Int64 { lock.lock(); defer { lock.unlock() }; return _files }

    func delete(_ path: String) {
        var st = stat()
        if lstat(path, &st) != 0 { error(path, errno); return }
        if (st.st_mode & S_IFMT) == S_IFDIR { deleteTree(path) } else { deleteFile(path, st) }
    }

    private func deleteTree(_ dir: String) {
        lock.lock(); _current = dir; lock.unlock()
        if let names = try? FileManager.default.contentsOfDirectory(atPath: dir) {
            for n in names {
                let p = dir.hasSuffix("/") ? dir + n : dir + "/" + n
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
        lock.lock(); errors.append(path + "  -  " + String(cString: strerror(code))); lock.unlock()
    }
}
