<p align="center">
  <img src="assets/app.png" alt="Disk Space Analyzer logo" width="128" height="128">
</p>

<h1 align="center">Disk Space Analyzer</h1>

<p align="center">A <b>free, open-source, fast</b> disk space analyzer and cleaner for Windows and macOS.</p>

It starts analyzing your system drive the moment it opens. See exactly where
your space went in a size tree, a contents list and a pie chart, then
permanently delete what you don't need to reclaim space.

![Disk Space Analyzer showing a drive with its size tree, contents list and pie chart (demo data)](assets/screenshot.png)

## Why

Checking folder sizes by hand (`robocopy ... /L /S | findstr Bytes`, re-running it
in every subfolder) is slow and tedious. This does the whole tree at once, in
parallel, and matches `robocopy`'s byte counts exactly — while being a tiny
single-file GUI.

## Features

- **Fast parallel scan** using the Win32 `FindFirstFileEx` API (basic info +
  large fetch) across multiple worker threads, aggregating sizes bottom-up.
- **Starts analyzing right away:** the system drive is scanned as soon as the
  app opens. Other local drives (second disks, USB/external drives) are listed
  in the tree with their used space and are scanned only when you ask; drives
  plugged in or removed while the app runs appear and disappear automatically.
- **Scans in the background:** a progress bar under the toolbar shows how far
  the scan is (for a whole drive, as a percentage of its used space), the file
  and folder counts and the current folder, with **Cancel** for partial
  results. You can keep browsing other drives meanwhile.
- **Size tree** of drives, folders and subfolders with aligned **Size** and
  **% of parent** columns plus a percentage bar; the contents list adds
  file/folder counts and is sortable by any column.
- **Pie chart pane** for the selected drive or folder. Its slices follow the
  folder contents list row for row, in the same order (also after you re-sort
  the list); small rows next to each other are merged into one "smaller items"
  slice. For a drive it adds the space taken by system/unreadable files and the
  **free space**. Hover a slice to highlight its row (or all the rows of a
  merged slice) in the list; hover a row to highlight its slice. Click a slice
  to open that folder. The chart is drawn from the sizes already in memory, so
  it never slows the scan.
- **Folder contents** pane with owner-drawn percentage bars, plus a
  **Largest files (top 1000)** tab to find big files anywhere in the tree.
- **Permanent delete** (bypasses the Recycle Bin) of files and folders, with a
  confirmation dialog showing item count and total size. Frees space immediately.
- **Correct and safe:** follows nothing through junctions/symlinks (their targets
  are never scanned or deleted), supports paths longer than 260 characters, and
  clears read-only attributes so deletes don't get stuck.
- **About** (version and author, <kbd>F1</kbd>) and **Check for updates** in the title bar, next to the app name.
- **Rescan folder**, **Show in Explorer**, **Copy path**, drag-and-drop a
  folder onto the window, and an optional **Run as administrator** relaunch for
  protected folders.
- **Dark, flat, rounded UI**: one blue accent, amber only for the destructive
  action, thin pill scrollbars, dark title bar and menus.
- **No installer, no runtime download.** A single ~100 KB `.exe` that runs on the
  .NET Framework 4.x built into every Windows 10/11 machine.

## Download

Grab `DiskSpaceAnalyzer.exe` (Windows) or `DiskSpaceAnalyzer-macOS.dmg` (macOS) from the
[Releases](../../releases) page. Nothing to install on Windows: just run the exe.

### macOS

Download `DiskSpaceAnalyzer-macOS.dmg` from the same release, open it and drag
**Disk Space Analyzer** into **Applications**. It runs natively on Apple
Silicon (M series) and Intel Macs (macOS 11 or later) and is about 1 MB.

The app is not signed with an Apple Developer ID, so the first time macOS
blocks it: right-click the app and choose **Open**, then **Open** again (or go to
**System Settings > Privacy & Security** and click **Open Anyway**). To let it
read every folder, give it **Full Disk Access** (the button in the toolbar opens
that setting). It scans the startup disk when it opens; external drives are
listed and scanned when you select them and press **Scan**. **About** and
**Check for Updates** are in the app menu next to the Apple menu.

### Updates

A few seconds after it starts, the app checks GitHub for a newer release (it
stays silent if there is none, or if you are offline). When one exists, a
small window (like About) offers **Update now**, **What's new** (the release page) and **Later**.
**Update now** downloads the new `.exe`, checks it against the checksum GitHub
publishes for the release, puts it in place of the running one and offers
**Restart now**. To check by hand, click **Check for updates** in the title bar, or the version number at the bottom right.
If the app's folder can't be written to (for example inside Program Files),
the update window links to the download page instead.

## Usage

1. Start the app: it begins analyzing your system drive immediately.
2. To analyze another drive, select it in the tree and press **Scan** (or
   double-click it). To analyze any folder, type or browse to it, or drag it
   onto the window, and press **Scan** (or Enter).
3. Explore the tree, the pie chart and the **Largest files** tab. Click column
   headers to sort, and chart slices to open a folder.
4. Select items and press **Delete permanently** (or <kbd>Del</kbd>) to reclaim
   space. Use **Rescan folder** (<kbd>F5</kbd>) to refresh after changes outside
   the app.

> ⚠️ **Deletion is permanent.** Deleted items do **not** go to the Recycle Bin and
> cannot be recovered. Read the confirmation dialog before confirming.

## Build from source

No Visual Studio, SDK, or NuGet required — it compiles with the C# compiler that
ships with Windows:

```powershell
git clone https://github.com/sphrd/DiskSpaceAnalyzer.git
cd DiskSpaceAnalyzer
.\build.ps1
```

The build moves `DiskSpaceAnalyzer.exe` to the repository root and removes the
intermediate `bin\` folder (close a running copy first).

macOS (needs Xcode or the Command Line Tools, and python3):

```bash
bash macos/build.sh
```

This builds a universal `Disk Space Analyzer.app` (Apple Silicon + Intel) and packs it into
`DiskSpaceAnalyzer-macOS.dmg` in the repository root. Both apps take their version from
`src/Program.cs` and their colours (logo included) from `src/Theme.cs`.

### Headless scan (for scripting / verification)

```powershell
DiskSpaceAnalyzer.exe --cli scan "C:\Program Files"
```

Prints the total size, file/folder counts, scan time, and top-level breakdown.
(`--scan <folder>` still works as a shorthand.) Run it with output redirected,
e.g. `Start-Process ... -Wait -NoNewWindow -RedirectStandardOutput out.txt`, or
from a console. Crashes are written to `DiskSpaceAnalyzer_error.txt`.

## How it works

- **Scanner** (`src/Scanner.cs`): worker threads pull directories from a shared
  LIFO queue, enumerate each with `FindFirstFileEx`, and a directory's totals are
  aggregated as soon as its last child finishes (via per-node pending counters).
  A lock-free size threshold lets workers skip all but the largest files when
  maintaining the top-1000 min-heap.
- **Native** (`src/Native.cs`): thin P/Invoke layer; filenames are read straight
  from the `WIN32_FIND_DATA` buffer without marshaling overhead, and all paths use
  the `\\?\` long-path prefix.
- **Deleter** (`src/Deleter.cs`): custom recursive delete via `DeleteFileW` /
  `RemoveDirectoryW` so it isn't limited by `MAX_PATH` and removes reparse points
  as links rather than descending into them.
- **UI** (`src/Theme.cs`, `src/Widgets.cs`, `src/Controls.cs`, `src/Chart.cs`):
  palette and fonts in one place; owner-drawn rounded buttons, panels, tabs,
  tree, list, scan bar and donut chart; the native list/tree scrollbars are
  clipped away and replaced by pill scrollbars that mirror and drive the native
  scroll position. Design notes are in [APP_NOTES.md](APP_NOTES.md).

## License

[MIT](LICENSE) © sphrd
