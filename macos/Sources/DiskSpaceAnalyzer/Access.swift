import AppKit
import Darwin

/// iCloud Drive and other File Provider folders keep items "dataless" until they are needed: only the name and the
/// attributes are on the Mac. Reading such a file, or listing such a folder (a Logic .logicx project is a folder), makes
/// the File Provider download it. The app switches that off for the whole process at start-up, so a stray read fails at
/// once instead of downloading; the scanner also never enters a dataless folder.
enum Dataless {
    // <sys/resource.h>
    static let iopolTypeMaterialize: Int32 = 3      // IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES
    static let scopeProcess: Int32 = 0              // IOPOL_SCOPE_PROCESS
    static let scopeThread: Int32 = 1               // IOPOL_SCOPE_THREAD
    static let materializeDefault: Int32 = 0        // IOPOL_MATERIALIZE_DATALESS_FILES_DEFAULT
    static let materializeOff: Int32 = 1            // IOPOL_MATERIALIZE_DATALESS_FILES_OFF
    static let materializeOn: Int32 = 2             // IOPOL_MATERIALIZE_DATALESS_FILES_ON
    static let flag: UInt32 = 0x40000000            // SF_DATALESS in <sys/stat.h>

    /// Result of setiopolicy_np at start-up (0 = downloads are off); -2 = not called.
    private(set) static var policyResult: Int32 = -2
    private(set) static var policyErrno: Int32 = 0

    static func disableMaterialization() {
        policyResult = setiopolicy_np(iopolTypeMaterialize, scopeProcess, materializeOff)
        policyErrno = policyResult == 0 ? 0 : errno
    }

    /// The process-wide policy now (IOPOL_MATERIALIZE_DATALESS_FILES_*), or -1.
    static var processPolicy: Int32 { getiopolicy_np(iopolTypeMaterialize, scopeProcess) }

    static func isDataless(_ path: String) -> Bool {
        var st = stat()
        return lstat(path, &st) == 0 && st.st_flags & flag != 0
    }

    /// Runs f with downloads allowed on this thread only. Used to delete a dataless folder for good: its list of
    /// names has to be fetched to remove the items inside (their contents are not downloaded).
    static func allowingDownloads<R>(_ f: () -> R) -> R {
        _ = setiopolicy_np(iopolTypeMaterialize, scopeThread, materializeOn)
        defer { _ = setiopolicy_np(iopolTypeMaterialize, scopeThread, materializeDefault) }
        return f()
    }
}

/// Full Disk Access. Without it macOS asks separately for every protected folder (Desktop, Documents, Downloads,
/// iCloud Drive, other apps' data...) the first time it is opened, and hides some others. A scan then does not enter
/// those folders (they are listed as "needs Full Disk Access") unless the user picks one of them to scan.
enum FullDiskAccess {
    static let settingsURL = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles")!
    static let promptKey = "FullDiskAccessPromptedAt"
    static let promptInterval: TimeInterval = 30 * 86400

    /// True when the app can read a file only Full Disk Access opens (checked each time: it can be granted meanwhile).
    static var granted: Bool {
        for p in ["/Library/Application Support/com.apple.TCC/TCC.db", PathUtil.home + "/Library/Safari/Bookmarks.plist"] {
            let fd = open(p, O_RDONLY | O_CLOEXEC)
            if fd >= 0 { close(fd); return true }
            let e = errno
            if e == EPERM || e == EACCES { return false }
        }
        let fd = open(PathUtil.home + "/Library/Safari", O_RDONLY | O_DIRECTORY | O_CLOEXEC)
        if fd >= 0 { close(fd); return true }
        return !(errno == EPERM || errno == EACCES)
    }

    /// Folders macOS guards with a permission prompt (or hides) for apps without Full Disk Access.
    static func protectedFolders() -> [String] {
        let h = PathUtil.trim(PathUtil.home)
        var list = ["Desktop", "Documents", "Downloads", "Pictures", "Movies", "Music", "Library/Mobile Documents", "Library/Mail",
                    "Library/Messages", "Library/Safari", "Library/Containers", "Library/Group Containers"].map { h + "/" + $0 }
        if let users = try? FileManager.default.contentsOfDirectory(atPath: "/Users") {
            for u in users where u != "Shared" && !u.hasPrefix(".") {
                let p = "/Users/" + u
                if !PathUtil.same(p, h) && PathUtil.isDir(p) { list.append(p) }
            }
        }
        // The same folders seen through the data volume (when that is scanned on its own).
        return list + list.filter { $0.hasPrefix("/Users/") }.map { "/System/Volumes/Data" + $0 }
    }

    /// The folders a scan of root must not enter: none with Full Disk Access, and none that hold root (the user picked it).
    static func blocked(for root: String) -> Set<String> {
        if granted { return [] }
        return Set(protectedFolders().filter { !PathUtil.isWithin(root, $0) })
    }

    /// Ask on the first run, then at most every 30 days after "Not now".
    static var shouldPrompt: Bool {
        if granted { return false }
        let last = UserDefaults.standard.double(forKey: promptKey)
        return last <= 0 || Date().timeIntervalSince1970 - last >= promptInterval
    }

    static func notePrompted() { UserDefaults.standard.set(Date().timeIntervalSince1970, forKey: promptKey) }
}

/// "Allow Full Disk Access", in the app's dialog frame. Shown before the first scan starts; onDone runs once, when the
/// dialog closes ("Not now", the close button or Esc). "Open System Settings" opens the pane and keeps the dialog.
final class FullDiskAccessWindow: AppDialog {
    static let heading = "Allow Full Disk Access"
    static let text = "Without it, macOS asks separately for every protected folder (Desktop, Documents, Downloads, iCloud Drive...) and hides some sizes. Turn on Disk Space Analyzer in System Settings > Privacy & Security > Full Disk Access, then reopen the app."
    private var done: (() -> Void)?

    convenience init(onDone: @escaping () -> Void) {
        self.init(title: "Disk Space Analyzer - Full Disk Access")
        done = onDone
        resize(width: 560, height: 300)
        var y: CGFloat = 26
        addLabel(FullDiskAccessWindow.heading, NSFont.boldSystemFont(ofSize: 20), T.FG, y: &y, height: 28, gapAfter: 10)
        let body = FullDiskAccessWindow.text as NSString
        let h = ceil(body.boundingRect(with: NSSize(width: contentWidth - 4, height: 1000), options: [.usesLineFragmentOrigin, .usesFontLeading],
                                       attributes: [.font: Theme.font]).height) + 6
        addLabel(FullDiskAccessWindow.text, Theme.font, T.FG, y: &y, height: h, gapAfter: 8)
        addLabel("Until then, protected folders are listed as \"needs Full Disk Access\"; pick one to scan it on its own.", Theme.font9, T.MUTED,
                 y: &y, height: 34)
        resize(width: 560, height: max(200, y + 76))
        setButtons([("Open System Settings", { NSWorkspace.shared.open(FullDiskAccess.settingsURL) }),
                    ("Not now", { [weak self] in self?.close() })])
        onClose = { [weak self] in
            let f = self?.done
            self?.done = nil
            f?()
        }
    }
}
