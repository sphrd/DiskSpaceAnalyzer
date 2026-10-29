import AppKit

// Disk Space Analyzer for macOS.
//   DiskSpaceAnalyzer                      opens the window and scans the startup disk
//   DiskSpaceAnalyzer <folder>             opens the window and scans <folder>
//   DiskSpaceAnalyzer --cli scan <folder>  headless scan, prints totals (same format as the Windows exe)
//   DiskSpaceAnalyzer --cli screenshot <out-folder> <folder>
//                                          opens the window, scans, saves a PNG of every view, About, the update window
//                                          and the Full Disk Access dialog
//   DiskSpaceAnalyzer --cli selftest <scratch-folder>
//                                          end-to-end self-test of the real window; writes <scratch-folder>.log, exit code 0 = passed

func headlessScan(_ path: String) -> Int32 {
    let full = (path as NSString).expandingTildeInPath
    let started = Date()
    let s = Scanner()
    let root = s.run(full)
    let secs = Date().timeIntervalSince(started)
    print("")
    print("\(root.name)\t\(root.bytes) bytes\t\(root.alloc) on disk\t\(root.files) files\t\(root.dirs) dirs\t\(String(format: "%.3f", secs)) s")
    for c in root.children {
        print("  " + c.name.padding(toLength: max(40, c.name.count), withPad: " ", startingAt: 0)
              + " \(Fmt.count(c.bytes).leftPad(15)) bytes \(Fmt.count(c.alloc).leftPad(15)) on disk \(Fmt.count(c.files).leftPad(10)) files")
    }
    return 0
}

extension String {
    func leftPad(_ n: Int) -> String { count >= n ? self : String(repeating: " ", count: n - count) + self }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    var main: MainWindowController!
    var about: AboutWindow?
    var fullDiskAccess: FullDiskAccessWindow?
    let initialPath: String?
    let screenshotDir: String?
    let selfTestDir: String?
    private var shots: ShotRun?
    private var test: SelfTestRun?

    init(initialPath: String?, screenshotDir: String?, selfTestDir: String? = nil) {
        self.initialPath = initialPath; self.screenshotDir = screenshotDir; self.selfTestDir = selfTestDir
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        Updater.cleanUp()
        NSApp.appearance = NSAppearance(named: .darkAqua)
        buildMenu()
        main = MainWindowController()
        main.selfTestDir = selfTestDir
        main.showWindow(nil)
        main.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        if let dir = screenshotDir { startScreenshots(dir) }
        if let dir = selfTestDir {
            main.onScanFinished = { [weak self] in
                guard let self = self, self.test == nil else { return }
                self.main.onScanFinished = nil
                let t = SelfTestRun(self.main, dir)
                self.test = t
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) { t.start() }
            }
        }
        // Ask for Full Disk Access before the first scan (never in the --cli modes); the scan starts when the dialog closes.
        if screenshotDir == nil && selfTestDir == nil && FullDiskAccess.shouldPrompt {
            FullDiskAccess.notePrompted()
            let path = initialPath
            let d = FullDiskAccessWindow { [weak self] in
                guard let self = self else { return }
                DispatchQueue.main.async { self.fullDiskAccess = nil }
                self.main.window?.makeKeyAndOrderFront(nil)
                self.main.start(initialPath: path)
            }
            fullDiskAccess = d
            DispatchQueue.main.async { d.present(over: self.main.window) }
            return
        }
        main.start(initialPath: initialPath)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    @objc func showAbout(_ sender: Any?) {
        if about == nil { about = AboutWindow(checkForUpdates: { [weak self] in self?.main.updates.check(manual: true) }) }
        about?.present(over: main.window)
    }

    @objc func checkForUpdates(_ sender: Any?) { main.updates.check(manual: true) }
    @objc func openRepo(_ sender: Any?) { NSWorkspace.shared.open(AboutWindow.repoURL) }
    @objc func chooseFolder(_ sender: Any?) {
        let p = NSOpenPanel()
        p.canChooseDirectories = true; p.canChooseFiles = false
        p.message = "Choose a folder to analyze"
        if p.runModal() == .OK, let u = p.url { main.scanPath(u.path) }
    }

    private func buildMenu() {
        let bar = NSMenu()
        func item(_ m: NSMenu, _ title: String, _ action: Selector?, _ key: String = "", _ mods: NSEvent.ModifierFlags = .command, target: AnyObject? = nil) {
            let it = NSMenuItem(title: title, action: action, keyEquivalent: key)
            it.keyEquivalentModifierMask = mods
            it.target = target
            m.addItem(it)
        }
        func sub(_ title: String) -> NSMenu {
            let top = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            let m = NSMenu(title: title)
            top.submenu = m
            bar.addItem(top)
            return m
        }

        // App menu: About and Check for Updates are the app-level actions (design language: menu bar).
        let app = sub("Disk Space Analyzer")
        item(app, "About Disk Space Analyzer", #selector(showAbout(_:)), target: self)
        item(app, "Check for Updates...", #selector(checkForUpdates(_:)), target: self)
        app.addItem(.separator())
        item(app, "Hide Disk Space Analyzer", #selector(NSApplication.hide(_:)), "h")
        item(app, "Hide Others", #selector(NSApplication.hideOtherApplications(_:)), "h", [.command, .option])
        item(app, "Show All", #selector(NSApplication.unhideAllApplications(_:)))
        app.addItem(.separator())
        item(app, "Quit Disk Space Analyzer", #selector(NSApplication.terminate(_:)), "q")

        let file = sub("File")
        item(file, "Choose Folder...", #selector(chooseFolder(_:)), "o", target: self)
        item(file, "Export...", #selector(MainWindowController.menuBarExport(_:)), "e", [.command, .shift])
        file.addItem(.separator())
        item(file, "Close Window", #selector(NSWindow.performClose(_:)), "w")

        let edit = sub("Edit")
        item(edit, "Cut", #selector(NSText.cut(_:)), "x")
        item(edit, "Copy", #selector(NSText.copy(_:)), "c")
        item(edit, "Paste", #selector(NSText.paste(_:)), "v")
        item(edit, "Select All", #selector(NSText.selectAll(_:)), "a")
        edit.addItem(.separator())
        item(edit, "Find Files...", #selector(MainWindowController.menuBarFind(_:)), "f")

        let folder = sub("Folder")
        item(folder, "Up", #selector(MainWindowController.menuBarUp(_:)), String(UnicodeScalar(NSUpArrowFunctionKey)!))
        item(folder, "Rescan Folder", #selector(MainWindowController.menuBarRescan(_:)), "r")
        item(folder, "Show in Finder", #selector(MainWindowController.menuBarFinder(_:)), "r", [.command, .shift])
        folder.addItem(.separator())
        item(folder, "Add to Delete List", #selector(MainWindowController.menuBarAddList(_:)), "d", [.command, .shift])
        item(folder, "Move to Trash...", #selector(MainWindowController.menuBarTrash(_:)))
        item(folder, "Delete Permanently...", #selector(MainWindowController.menuBarDelete(_:)), "\u{8}")

        let window = sub("Window")
        item(window, "Minimize", #selector(NSWindow.performMiniaturize(_:)), "m")
        item(window, "Zoom", #selector(NSWindow.performZoom(_:)))
        NSApp.windowsMenu = window

        let help = sub("Help")
        item(help, "Disk Space Analyzer on GitHub", #selector(openRepo(_:)), target: self)
        NSApp.helpMenu = help

        NSApp.mainMenu = bar
    }

    // MARK: CI screenshots

    private func startScreenshots(_ dir: String) {
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let run = ShotRun(self, dir)
        shots = run
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { run.shot("0-scanning") }
        main.onScanFinished = { [weak self] in
            guard let self = self else { return }
            self.main.onScanFinished = nil
            run.start()
        }
    }
}

Dataless.disableMaterialization()      // before anything reads the disk: never download iCloud items (see Access.swift)

let args = CommandLine.arguments
if args.count >= 2 && args[1] == "--cli" {
    if args.count == 4 && args[2] == "scan" { exit(headlessScan(args[3])) }
    if args.count == 5 && args[2] == "screenshot" {
        let app = NSApplication.shared
        let d = AppDelegate(initialPath: args[4], screenshotDir: args[3])
        app.delegate = d
        app.setActivationPolicy(.regular)
        app.run()
        exit(0)
    }
    if args.count == 4 && args[2] == "selftest" {
        let dir = SelfTest.prepare(args[3])
        let app = NSApplication.shared
        let d = AppDelegate(initialPath: dir, screenshotDir: nil, selfTestDir: dir)
        app.delegate = d
        app.setActivationPolicy(.regular)
        app.run()
        exit(0)
    }
    print("Usage: DiskSpaceAnalyzer --cli scan <folder>")
    print("       DiskSpaceAnalyzer --cli screenshot <out-folder> <folder>")
    print("       DiskSpaceAnalyzer --cli selftest <scratch-folder>")
    exit(2)
}

let initial = args.dropFirst().first { !$0.hasPrefix("-") && FileManager.default.fileExists(atPath: $0) }
let app = NSApplication.shared
let delegate = AppDelegate(initialPath: initial, screenshotDir: nil)
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
