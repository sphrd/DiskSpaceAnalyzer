<p align="center">
  <img src="assets/app.png" alt="Disk Space Analyzer logo" width="128" height="128">
</p>

<h1 align="center">Disk Space Analyzer</h1>

<p align="center">A <b>free, open-source, fast</b> disk space analyzer and cleaner for Windows.</p>

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
- **Pie chart pane** for the selected drive or folder: its biggest subfolders,
  the files directly in it, and for a drive the space taken by system/unreadable
  files and the **free space**. A capacity bar shows the drive's used and free
  space for any selection. Click a slice to open that folder. The chart is
  drawn from the sizes already in memory, so it never slows the scan.
- **Folder contents** pane with owner-drawn percentage bars, plus a
  **Largest files (top 1000)** tab to find big files anywhere in the tree.
- **Permanent delete** (bypasses the Recycle Bin) of files and folders, with a
  confirmation dialog showing item count and total size. Frees space immediately.
- **Correct and safe:** follows nothing through junctions/symlinks (their targets
  are never scanned or deleted), supports paths longer than 260 characters, and
  clears read-only attributes so deletes don't get stuck.
- **Rescan folder**, **Show in Explorer**, **Copy path**, drag-and-drop a
  folder onto the window, and an optional **Run as administrator** relaunch for
  protected folders.
- **Dark, flat, rounded UI**: one blue accent, amber only for the destructive
  action, thin pill scrollbars, dark title bar and menus.
- **No installer, no runtime download.** A single ~100 KB `.exe` that runs on the
  .NET Framework 4.x built into every Windows 10/11 machine.

## Download

Grab `DiskSpaceAnalyzer.exe` from the [Releases](../../releases) page and run it.
Nothing to install.

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
