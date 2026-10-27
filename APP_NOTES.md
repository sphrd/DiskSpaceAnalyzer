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
| Widgets: RoundButton, RoundedPanel, PathInput, TabStrip, PillScrollBar, ScrollHost, BusyOverlay, Spinner, ScanBar, DarkRenderer, DarkTreeView, TreeHeader | `src/Widgets.cs` |
| List view (rows, bars, header, empty-state hint) + sorting | `src/Controls.cs` |
| Pie chart pane (donut, legend, capacity bar) | `src/Chart.cs` |
| Scanner / deleter / interop (domain, not UI) | `src/Scanner.cs`, `src/Deleter.cs`, `src/Native.cs` |
| Asset generator (icon `.ico`, 256 px `app.png` for the README, 1280x640 `social-preview.png` for GitHub) | `tools/make_icon.ps1` (run by every build) |
| Build script | `build.ps1` -> compiles into `bin/`, moves `DiskSpaceAnalyzer.exe` to the repo root (git-ignored), deletes `bin/` |
| Version | `src/Program.cs` (`AssemblyVersion` / `AssemblyFileVersion`), `src/app.manifest` |

## App identity

- Name: Disk Space Analyzer. Repo: `sphrd/DiskSpaceAnalyzer`. License: MIT.
- AppUserModelID: `sphrd.DiskSpaceAnalyzer`.
- Icon concept: **accent tile with a pie chart shaped like Pac-Man facing left = disk usage analyzer that frees space.**
  One circle (same centre and radius) split only by colour: white right half, upper-left `accent-light` (#cfe4ff),
  lower-left `#a6cfff` (icon-only shade between accent-light and accent-active, requested by the user), mouth on the left.
  No gaps: the left sections run a few degrees under the white half so shared edges blend colour into colour. 3 elements, checked at 16 / 24 / 32 / 48 / 256 px.
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
- 2026-10-27: "the icon has a cut, why? the pie chart is not cut smoothly. make it smooth. put a half circle (right side),
  then on the left side add two smaller circle sections above and below each with a slightly different shade of blue,
  in a way that looks like pacman, but flipped form current version."
- 2026-10-27: "the circles radius ar enot matching! fix it. it should look like a smooth circle separate only by color.
  remove the gap. wait with commit or releasing until i tell u. why did u put a screenshot of my pc analysis on the github????"
- 2026-10-27: "i need a macos and macos arm (M series) version released as well. whats the effort for that? can we keep it this fast?"

- 2026-10-27: "after hte app is opened, it should automatically start analyzing the entire disk. show the percentage in
  the left pane as a bar as well. show the external drives as well, and for them only scan manually. add a graphical
  pie chart view on the right of app (a new pane) without compromising speed. the chart should show free storage as
  well, and it should show currently selected (the main drive - C - after startup)"
- 2026-10-27: window size question answered: "Open at 1500x880 (shrunk to fit smaller screens)".
- 2026-10-27: "make the screenshot, with fake data, and commit and push and release all. use the Logo for the repo's
  description as well."
- 2026-10-27: "the scan should not block the page. it should run with a bar on the top of app showing the progress and
  scan data."
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
- Scans run in the background behind a **scan bar** under the toolbar (spinner, status, counts + current path,
  pill progress bar, Cancel); browsing stays possible, Scan / Rescan / Delete / Browse are disabled until it ends.
  For a whole drive the bar is real progress: bytes found / drive used space (capped at 99 % until done). Folders have
  no known total, so no bar, only counts. The full-window busy overlay (§2.6) is kept for permanent deletes only.
- Tree top level = one node per local drive (system drive first) plus scanned folders (`ScanRoot`). The system drive
  is scanned at startup; other drives show used space from the volume and are scanned on Scan / double-click / Enter.
- Tree `% of parent` column has a 44 px pill bar; for a scanned drive root it is the share of the drive capacity.
- Pie chart pane (right, 330 px, fixed): donut of the selection's 6 biggest subfolders + other folders + files in
  the folder; for a drive root also "System / not readable" (used - scanned) and "Free space". Capacity bar below for
  any selection. Chart colours are data-only tokens in `Theme.CHART*` (accent shades + greys), allowed by §2.1.
- Message boxes and the folder picker stay OS-styled (accepted by §2.1).

## Deviations from the agent file (kept to preserve existing behavior)

- Window opens at 1500x880 (shrunk to the screen's working area; min 900x520), approved by the user. The app is a
  three-pane explorer, not the single-column form of §2.5. Layout: toolbar (path row + action row), scan bar (while
  scanning), tree | list | chart panels, status line.
- The path box is editable (type a path + Enter), so only its chevron opens the drive list, not the whole box.
- Deletion has no Cancel on the overlay: the deleter never had cancellation; adding it would change behavior.
- No splash screen: §2.9 asks for one only when startup takes noticeable time; this ~100 KB exe has no unpack step
  and the window appears immediately.
- Folder/file icons are muted glyphs instead of shell icons (yellow shell folders would add a hue).
- The former toolbar **Stop** button became the overlay's **Cancel** (same action; Stop was only usable while scanning).

## Decisions and gotchas

- Mapped network drives are not listed: `DriveInfo.IsReady` on a disconnected share can block for seconds. A network
  path can still be typed and scanned. Drives are re-read on `WM_DEVICECHANGE` (arrival / removal), deferred while a
  scan or delete runs.
- While a scan runs the user can navigate. When it ends, the view stays where the user is: a selection inside the old
  copy of the rescanned tree is mapped onto the new one by path (`FindByPath`); another drive stays selected.
  Model updates (`ReplaceNode` etc.) use the largest-files list of the root that owns the node (`TopOf`), not the
  active one, because the user may have switched drives during a background rescan.
- Chart legend: the donut shrinks so every legend row fits (`Free space` is last and must stay visible).
- Startup: the window shows in ~0.3 s warm. The first start of a freshly built exe takes 2-10 s while Windows
  Defender inspects the new file; that is not app work.
- Full C: scan (~870k files, 203k folders) took 10 s on an idle machine and 20-35 s under load; the old and new
  scanner measured the same under the same load, so the UI changes cost no scan speed.

- **Privacy: screenshots for the repo never show a real scan of the user's machine** (v1.0-v1.2 showed the user's
  C:\Program Files). Build a demo tree of sparse files under a neutral path (e.g. `C:\Demo`: `fsutil sparse setflag`,
  then `SetLength`; no disk space used), capture with `PrintWindow`, blank the drive free-space label, delete the
  demo tree afterwards. Never commit machine-specific details (paths, user names, security policies).
  Since v1.3 the app scans the system drive and lists real drives at startup, so the screenshot is taken in-process:
  scan `C:\Demo` (sparse files), then via reflection rename that root to a made-up `E:\ Data` drive with a made-up
  capacity, add a made-up `F:\ Backup` drive, drop the real drives, and set the status/drive labels before capturing.
- Repo logo: `assets/app.png` at the top of the README; `assets/social-preview.png` is the GitHub social preview
  (Settings > General > Social preview; GitHub has no API for it, so it is uploaded by hand).

- Icon: do not cut pie slices with thick strokes in the tile colour. v1.2.0 did, and the square line end left a
  ragged notch on the rim. Separate slices by offsetting whole `FillPie` shapes instead.

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
- v1.3.0 (2026-10-27): auto-scan of the system drive at startup, background scan with progress bar, all local drives
  in the tree (others scanned on demand), percentage bars in the tree, pie chart pane with free space, 1500x880 window,
  smooth Pac-Man icon. ~117 KB.
