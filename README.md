<p align="center">
  <img src="assets/app.png" alt="Disk Space Analyzer logo" width="128" height="128">
</p>

<h1 align="center">Disk Space Analyzer</h1>

<p align="center">A <b>free, open-source, fast</b> disk space analyzer and cleaner for Windows and macOS.</p>

It starts analyzing your system drive the moment it opens. See exactly where
your space went in a size tree, a contents list and a ring chart, find big,
old and duplicate files, follow cleanup suggestions, and delete what you don't
need, to the Recycle Bin or permanently.
![Disk Space Analyzer showing a drive with its size tree, contents list and pie chart (demo data)](assets/screenshot.png)

## Why

Checking folder sizes by hand (`robocopy ... /L /S | findstr Bytes`, re-running it
in every subfolder) is slow and tedious. This does the whole tree at once, in
parallel, and matches `robocopy`'s byte counts exactly — while being a tiny
single-file GUI.

## Features

**Scanning**

- **Whole drives in seconds:** run as administrator, it reads an NTFS drive's
  master file table directly (like WizTree): over a million files in about ten
  seconds even on a slow cloud disk, many times faster than walking folders.
  Without admin rights, for other file systems and for single folders it walks
  folders in parallel with `FindFirstFileEx`; the status line says which was used.
- **Starts right away and in the background:** the system drive is scanned when
  the app opens, with a progress bar and **Cancel**; other drives (second disks,
  USB) are listed and scanned on demand and appear/disappear as they are plugged
  in or out.
- **Live updates:** scanned drives and folders are watched; files that appear,
  change or go away update the results within seconds, without a rescan.
- **Size or size on disk:** one switch changes every view between file size (as
  Explorer's Size column) and the space really used: compressed, sparse and
  Compact OS files count what they take, small files a whole cluster,
  OneDrive "online-only" files nothing, and hard links only once (with the
  master file table).

**Seeing where the space went** (tabs above the list; the chart always shows the
open list, slice for row, and hovering one highlights the other)

- **Contents:** the selected folder's subfolders and files with size, share,
  change since the last scan, counts and dates. The ring chart shows the
  folder's contents and, in two outer rings, their subfolders; click any part
  to open it. For a drive it also shows system/unreadable space and free space.
- **Files:** the largest files under the selected folder, with a name search
  (`*.iso`, `invoice`; <kbd>Ctrl</kbd>+<kbd>F</kbd>) and filters by type, size and age
  ("older than 1 year"). Searches the in-memory index, so it is instant.
- **File types:** space by extension or by category (videos, images, archives,
  disk images, installers...). Double-click one to list its files.
- **Duplicates:** files with identical content (same size, then a hash of the
  start, then of the whole file). Hard links and online-only cloud files are not
  counted. **Select extra copies** keeps the newest of each group.
- **Suggestions:** space that can usually be freed: Recycle Bin, temporary
  files, Windows Update downloads, crash dumps, thumbnail and browser caches,
  developer caches (npm, NuGet, pip, Gradle, Maven, Cargo, Go...), node_modules
  folders, old installers in Downloads, Windows.old. Each says what deleting it
  means; caches and temporary folders are emptied, not removed.
- **Changes:** every full scan is remembered (folder sizes only, on this PC), so
  the next scan shows which folders grew or shrank since.

![Duplicates view: groups of identical files and the space their extra copies take (demo data)](assets/screenshot-duplicates.png)

**Cleaning up**

- **Delete list:** collect items from any view (<kbd>Insert</kbd>), review them
  together, then **Move to Recycle Bin** (can be undone) or **Delete
  permanently** (frees the space now). Items inside a listed folder are not
  added twice.
- **Direct delete** from any list: **Delete permanently** (<kbd>Del</kbd>) or
  **Move to Recycle Bin** in the right-click menu, always with a confirmation
  showing the items and total size, and a warning for Windows or Program Files.
- **Correct and safe:** junctions and symbolic links are never followed (their
  targets are not scanned or deleted), paths longer than 260 characters work,
  read-only files are handled, and files in use are skipped when emptying caches.

**And**

- **Export** the selected folder as an HTML report (summary, chart, largest
  folders and files, file types) or as CSV (every folder, or the list shown).
- **About** (<kbd>F1</kbd>) and **Check for updates** in the title bar; updates install themselves.
- **Rescan folder** (<kbd>F5</kbd>), **Show in Explorer**, **Copy path**,
  drag-and-drop a folder onto the window, and **Run as administrator**.
- **Dark, flat, rounded UI**: one blue accent, amber only for destructive actions.
- **No installer, no runtime download.** A single small `.exe` that runs on the
  .NET Framework 4.x built into every Windows 10/11 machine.

The macOS version has the same views and tools, except reading the master file
table (an NTFS feature): it uses the Trash instead of the Recycle Bin, finds
iCloud files that are not downloaded (they take no space), suggests Mac caches
(Xcode DerivedData and simulators, Homebrew, `~/Library/Caches`, logs, the Trash...)
and watches folders with FSEvents. Shortcuts: <kbd>⇧</kbd><kbd>⌘</kbd><kbd>D</kbd> adds to the
delete list, <kbd>⌘</kbd><kbd>F</kbd> searches files, <kbd>⇧</kbd><kbd>⌘</kbd><kbd>E</kbd> exports,
<kbd>⌘</kbd><kbd>⌫</kbd> deletes.
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

1. Start the app: it begins analyzing your system drive immediately. For the
   fastest whole-drive scans, use **Run as administrator**.
2. To analyze another drive, select it in the tree and press **Scan** (or
   double-click it). To analyze any folder, type or browse to it, or drag it
   onto the window, and press **Scan** (or Enter).
3. Explore the tree, the tabs and the chart. Click column headers to sort, and
   chart parts to open a folder. The **Files**, **File types** and
   **Duplicates** tabs work on the folder selected in the tree.
4. Add what you want gone to the **Delete list** (<kbd>Insert</kbd>), review it,
   then move it to the Recycle Bin or delete it permanently. Or delete directly
   with <kbd>Del</kbd>.

> ⚠️ **Delete permanently cannot be undone.** Those items do **not** go to the
> Recycle Bin. Read the confirmation before confirming, or use **Move to Recycle Bin**.

Command line (for scripts and checks): `DiskSpaceAnalyzer.exe --cli scan <folder> [--mft] [--files]`
prints sizes; `--cli selftest <scratch folder>` runs the end-to-end self-test;
`--cli screenshot <out folder> <folder>` saves a picture of every view.
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
