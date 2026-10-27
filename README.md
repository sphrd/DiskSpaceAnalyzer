# Disk Space Analyzer

A **free, open-source, fast** disk space analyzer and cleaner for Windows.

Point it at a drive or folder, see exactly where your space went in a sortable
size tree, then permanently delete what you don't need to reclaim space.

![screenshot](assets/screenshot.png)

## Why

Checking folder sizes by hand (`robocopy ... /L /S | findstr Bytes`, re-running it
in every subfolder) is slow and tedious. This does the whole tree at once, in
parallel, and matches `robocopy`'s byte counts exactly — while being a tiny
single-file GUI.

## Features

- **Fast parallel scan** using the Win32 `FindFirstFileEx` API (basic info +
  large fetch) across multiple worker threads, aggregating sizes bottom-up.
- **Size tree** of folders and subfolders with size, percentage of parent, and
  file/folder counts — sortable by any column.
- **Folder contents** pane with owner-drawn percentage bars, plus a
  **Largest files (top 1000)** tab to find big files anywhere in the tree.
- **Permanent delete** (bypasses the Recycle Bin) of files and folders, with a
  confirmation dialog showing item count and total size. Frees space immediately.
- **Correct and safe:** follows nothing through junctions/symlinks (their targets
  are never scanned or deleted), supports paths longer than 260 characters, and
  clears read-only attributes so deletes don't get stuck.
- **Live progress** on a busy overlay with **Cancel** for partial results,
  **Rescan folder**, **Show in Explorer**, **Copy path**, drag-and-drop a folder
  onto the window, and an optional **Run as administrator** relaunch for
  protected folders.
- **Dark, flat, rounded UI**: one blue accent, amber only for the destructive
  action, thin pill scrollbars, dark title bar and menus.
- **No installer, no runtime download.** A single ~100 KB `.exe` that runs on the
  .NET Framework 4.x built into every Windows 10/11 machine.

## Download

Grab `DiskSpaceAnalyzer.exe` from the [Releases](../../releases) page and run it.
Nothing to install.

## Usage

1. Pick a drive from the list or type/browse to a folder (or drag a folder onto
   the window).
2. Press **Scan** (or Enter).
3. Explore the tree and the **Largest files** tab. Click column headers to sort.
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

The executable is written to `bin\DiskSpaceAnalyzer.exe`.

### Headless scan (for scripting / verification)

```powershell
bin\DiskSpaceAnalyzer.exe --scan "C:\Program Files"
```

Prints the total size, file/folder counts, scan time, and top-level breakdown.

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
- **UI** (`src/Theme.cs`, `src/Widgets.cs`, `src/Controls.cs`): palette and
  fonts in one place; owner-drawn rounded buttons, panels, tabs, tree and list;
  the native list/tree scrollbars are clipped away and replaced by pill
  scrollbars that mirror and drive the native scroll position. Design notes are
  in [APP_NOTES.md](APP_NOTES.md).

## License

[MIT](LICENSE) © sphrd
