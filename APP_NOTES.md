# APP_NOTES — Disk Space Analyzer

Project notes for the App Engineer agent (`app-engineer.agent.md` from `sphrd/Agents`, stack-agnostic version).
This project is C# WinForms on the in-box .NET Framework 4.x; the design language (Part 2) and interaction
patterns (Part 3) are applied through the WinForms mapping below.

## Locations

| What | Where |
|---|---|
| Entry point (GUI + headless `--cli scan <folder>`, alias `--scan <folder>`) | `src/Program.cs` |
| Main window (layout, wiring, all behavior) | `src/MainForm.cs` |
| Theme tokens (palette, fonts), drawing helpers, DWM interop | `src/Theme.cs` |
| Widgets: RoundButton, RoundedPanel, PathInput, TabStrip, PillScrollBar, ScrollHost, BusyOverlay, Spinner, DarkRenderer, DarkTreeView, TreeHeader | `src/Widgets.cs` |
| List view (rows, bars, header) + sorting | `src/Controls.cs` |
| Scanner / deleter / interop (domain, not UI) | `src/Scanner.cs`, `src/Deleter.cs`, `src/Native.cs` |
| Asset generator (icon `.ico` + 256 px `.png`) | `tools/make_icon.ps1` (run by every build) |
| Build script | `build.ps1` -> compiles into `bin/`, moves `DiskSpaceAnalyzer.exe` to the repo root (git-ignored), deletes `bin/` |
| Version | `src/Program.cs` (`AssemblyVersion` / `AssemblyFileVersion`), `src/app.manifest` |

## App identity

- Name: Disk Space Analyzer. Repo: `sphrd/DiskSpaceAnalyzer`. License: MIT.
- AppUserModelID: `sphrd.DiskSpaceAnalyzer`.
- Icon concept: **accent tile with a white pie chart missing a wedge on the right (Pac-Man style) = disk usage
  analyzer that frees space.** The upper jaw is an `accent-light` slice split off by one `accent` gap line, so it
  reads as a pie chart, not only Pac-Man. 3 elements, checked at 16 / 32 / 48 / 256 px.
- Primary action: **Scan**. Destructive action: **Delete permanently** (warn kind, amber).
- Domain status strings: `Scanning: <n> folders, <n> files, <size> so far` (no known total, so no `i/n`),
  `Cancelling... (finishing current folder)`, `Deleting: <n> files removed`, detail line = current path (muted).

## User requirements

- 2026-10-27: "I wanna creat a new free, open source, fast windows disk space analyzer and cleaner. to analyze
  the sizes of dirs and subdirs fastest possible way, and give the option to permenantly delete files to open
  up space." ... "can be a simple small size app with a gui. a new project to be committed to my sphrd github account."
- 2026-10-27: "use the app-engineer.agent.md from my sphrd/Agents github repo. restyle the app's theme without
  changing functionality."
- 2026-10-27: "build script should buildthe exe from src and copy it to repo root folder."
- 2026-10-27: "reread the app design agent form github. the restrictions are changed. the app logo should be changed.
  i prefer something similar to pacman logo here. like a pie chart missing a pice on the right side that symbolizes
  disk space. theres a bug in the app when hovering over the right side window the calculated sizes disappear.
  until i click which then they come back. also in the left side panel add always visible columns to show size and
  percentage on the right side of it, not attached to the names so this way they orm a column for a cleaner read."

## WinForms mapping of the design language

- Tokens and fonts live only in `Theme.cs`; widgets use those names, no inline hex.
- Rounded shapes: GDI+ anti-aliased paths (`Theme.FillRound`; outline = outer fill + 1 px inset fill).
  The icon is drawn at 4x and downscaled (HighQualityBicubic).
- Popups and context menu: `DarkRenderer`; on Windows 11 DWM gives anti-aliased rounded corners with a border
  outline (`DWMWA_WINDOW_CORNER_PREFERENCE` / `DWMWA_BORDER_COLOR`); Windows 10 falls back to square + border line.
- Title bar: DWM immersive dark mode (attribute 20, fallback 19).
- Scrollbars: native ListView/TreeView scrollbars cannot be styled. `ScrollHost` sizes the native control so its
  scrollbars fall outside a clipping panel and shows `PillScrollBar`s (6 px thumb in a 10 px track) that mirror
  `GetScrollInfo` and scroll via `LVM_SCROLL` (list) / `WM_xSCROLL SB_THUMBPOSITION` (tree).
- Tree: name on the left, then right-aligned **Size** and **% of parent** columns that line up on every row, with a
  `TreeHeader` above. `TVS_NOHSCROLL`: long names are ellipsized instead of scrolling the columns away.
- Busy overlay covers the whole client area (anchored, not docked), spinner per §2.6, Cancel for scans.
- Message boxes and the folder picker stay OS-styled (accepted by §2.1).

## Deviations from the agent file (kept to preserve existing behavior)

- Window size stays 1240x780 (min 760x460): the app is a tree + list explorer, not the single-column form of §2.5
  (§1.6: window size changes need the user's approval). Layout: toolbar (path row + action row),
  tree | list panels, status line.
- The path box is editable (type a path + Enter), so only its chevron opens the drive list, not the whole box.
- Deletion has no Cancel on the overlay: the deleter never had cancellation; adding it would change behavior.
- No splash screen: §2.9 asks for one only when startup takes noticeable time; this ~100 KB exe has no unpack step
  and the window appears immediately.
- Folder/file icons are muted glyphs instead of shell icons (yellow shell folders would add a hue).
- The former toolbar **Stop** button became the overlay's **Cancel** (same action; Stop was only usable while scanning).

## Decisions and gotchas

- **Owner-drawn ListView, details view:** when the cursor moves over a row, the native list repaints it by raising
  `DrawItem` *without* `DrawSubItem`. Painting cells in `OnDrawSubItem` (and the row background in `OnDrawItem`)
  made every hovered row lose its size/percent cells until a click. Fix: paint the whole row (background,
  selection, every cell in display order) in `OnDrawItem`; `OnDrawSubItem` is empty.
- That bug is invisible to `PrintWindow(..., PW_RENDERFULLCONTENT)` (it repaints everything) and to synthetic
  `WM_MOUSEMOVE`/`WM_MOUSEHOVER`. Reproduce it with real cursor movement (`SetCursorPos`, no clicks) and a plain
  screen capture (`CopyFromScreen`).
- The hosted list/tree must not be docked: docking undoes `ScrollHost`'s bounds and causes a
  layout -> paint -> sync loop (window showed "Not Responding").
- `PathInput.OnLayout` runs from the base constructor (setting Padding) before children exist: null-guard it.
- Header area right of the last column is not owner-drawn; `HeaderPainter` fills it after `WM_PAINT`.
- Tree column widths: measure samples without `EndEllipsis` (with it, `MeasureText` returns a too-small width).
  Each sample is the wider of the longest value and the header title.
- Testing: clicks and keys on a shared desktop can land in another app. Post messages to specific window handles
  (`WM_KEYDOWN` to the tree, `WM_LBUTTONDOWN/UP` to a button, `WM_COMMAND IDYES` to the confirm dialog);
  cursor moves without clicks are safe.
- Driving `MainForm` in-process from PowerShell is fine for synchronous checks, but `await` continuations stall
  there (no WinForms message loop); test async flows (scan/cancel/rescan/delete) on the real exe.
- Headless check: `Start-Process DiskSpaceAnalyzer.exe -ArgumentList '--cli','scan','"<dir>"' -Wait -NoNewWindow
  -RedirectStandardOutput <file>`; a plain call does not capture a winexe's stdout. Headless crashes go to
  `DiskSpaceAnalyzer_error.txt` in the working directory.

## Releases

- v1.0.0 (2026-10-27): first release, light WinForms UI, ~66 KB.
- v1.1.0 (2026-10-27): design-language dark theme restyle, ~96 KB. Verified: Program Files scan
  matches pre-restyle counts; cancel, rescan and permanent delete on a sandbox folder work on the real exe.
- v1.2.0 (2026-10-27): pie-chart icon, hover fix in the lists, aligned Size / % of parent columns in the tree,
  `--cli scan` headless mode, build moves the exe to the root. ~100 KB.
