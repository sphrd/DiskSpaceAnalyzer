# APP_NOTES — Disk Space Analyzer

Project notes for the App Engineer agent (`app-engineer.agent.md` from `sphrd/Agents`).
The agent file is written for Python/tkinter; this project is C# WinForms on the in-box
.NET Framework 4.x, so Part 2 (design language) is applied through the WinForms mapping below.
Part 3's PyInstaller/venv/splash sections do not apply.

## Locations

| What | Where |
|---|---|
| Entry point (GUI + headless `--scan`) | `src/Program.cs` |
| Main window (layout, wiring, all behavior) | `src/MainForm.cs` |
| Palette, fonts, drawing helpers, DWM interop | `src/Theme.cs` |
| Widgets: RoundButton, RoundedPanel, PathInput, TabStrip, PillScrollBar, ScrollHost, BusyOverlay, Spinner, DarkRenderer, DarkTreeView | `src/Widgets.cs` |
| List view (rows, bars, header) + sorting | `src/Controls.cs` |
| Scanner / deleter / interop (domain, not UI) | `src/Scanner.cs`, `src/Deleter.cs`, `src/Native.cs` |
| Asset generator (icon `.ico` + 256 px `.png`) | `tools/make_icon.ps1` (run by every build) |
| Build script | `build.ps1` -> `bin/DiskSpaceAnalyzer.exe`, copied to the repo root (`DiskSpaceAnalyzer.exe`, git-ignored) |
| Version | `src/Program.cs` (`AssemblyVersion` / `AssemblyFileVersion`) |

## App identity

- Name: Disk Space Analyzer. Repo: `sphrd/DiskSpaceAnalyzer`. License: MIT.
- Icon: the standard design-language motif (ACCENT tile, folded document, three content lines with dots).
  It reads as a size list; no domain-specific motif was requested.
- Primary action: **Scan**. Destructive action: **Delete permanently** (warn kind, amber).
- Domain status strings: `Scanning: <n> folders, <n> files, <size> so far` (no known total, so no `i/n`),
  `Cancelling... (finishing current folder)`, `Deleting: <n> files removed`, detail line = current path (MUTED).

## User requirements

- 2026-10-27: "I wanna creat a new free, open source, fast windows disk space analyzer and cleaner. to analyze
  the sizes of dirs and subdirs fastest possible way, and give the option to permenantly delete files to open
  up space." ... "can be a simple small size app with a gui. a new project to be committed to my sphrd github account."
- 2026-10-27: "use the app-engineer.agent.md from my sphrd/Agents github repo. restyle the app's theme without
  changing functionality."

## WinForms mapping of the design language

- Palette names and fonts live only in `Theme.cs`; widgets use those names, no inline hex.
- Rounded shapes: GDI+ anti-aliased paths (`Theme.FillRound`; outline = outer fill + 1 px inset fill) instead of
  4x-drawn Lanczos corner tiles. Icon is drawn at 4x and downscaled (HighQualityBicubic).
- Popups and context menu: `DarkRenderer`; on Windows 11 DWM gives anti-aliased rounded corners with a BORDER
  outline (`DWMWA_WINDOW_CORNER_PREFERENCE` / `DWMWA_BORDER_COLOR`); Windows 10 falls back to square + BORDER line.
- Title bar: DWM immersive dark mode (attribute 20, fallback 19).
- Scrollbars: native ListView/TreeView scrollbars cannot be styled. `ScrollHost` sizes the native control so its
  scrollbars fall outside a clipping panel and shows `PillScrollBar`s (6 px thumb in a 10 px gutter) that mirror
  `GetScrollInfo` and scroll via `LVM_SCROLL` (list) / `WM_xSCROLL SB_THUMBPOSITION` (tree).
- Busy overlay covers the whole client area (anchored, not docked), spinner per §2.6, Cancel for scans.
- Message boxes and the folder picker stay OS-styled (accepted by §2.1).

## Deviations from Part 2 (kept to preserve existing behavior)

- Window size stays 1240x780 (min 760x460): the layout is a tree + list explorer, not the single-input form
  of §2.5. Layout order is toolbar (path row + action row), tree | list panels, status line.
- The path box is editable (type a path + Enter), so only its chevron opens the drive list, not the whole box.
- Deletion has no Cancel on the overlay: the deleter never had cancellation; adding it would change behavior.
- No splash screen: the exe is ~100 KB and starts instantly (the splash exists to cover PyInstaller unpacking).
- Folder/file icons are MUTED glyphs instead of shell icons (yellow shell folders would add a hue).
- The former toolbar **Stop** button became the overlay's **Cancel** (same action; Stop was only usable while scanning).

## Decisions and gotchas

- The hosted list/tree must not be docked: docking undoes `ScrollHost`'s bounds and causes a
  layout -> paint -> sync loop (window showed "Not Responding").
- `PathInput.OnLayout` runs from the base constructor (setting Padding) before children exist: null-guard it.
- Header area right of the last column is not owner-drawn; `HeaderPainter` fills it after `WM_PAINT`.
- Testing: real mouse input on a shared desktop is unsafe (focus can move to another app). Use `PrintWindow`
  (flag 2) for captures and post messages to specific window handles (`WM_KEYDOWN` to the tree,
  `WM_LBUTTONDOWN/UP` to a button, `WM_COMMAND IDYES` to the confirm dialog).
- Driving `MainForm` in-process from PowerShell is fine for synchronous checks, but `await` continuations stall
  there (no WinForms message loop); test async flows (scan/cancel/rescan/delete) on the real exe.
- Headless check: `Start-Process DiskSpaceAnalyzer.exe -ArgumentList '--scan','"<dir>"' -Wait -NoNewWindow
  -RedirectStandardOutput <file>`; a plain call does not capture a winexe's stdout.

## Releases

- v1.0.0 (2026-10-27): first release, light WinForms UI, ~66 KB.
- v1.1.0 (2026-10-27): design-language dark theme restyle, ~96 KB. Verified: Program Files scan
  matches pre-restyle counts; cancel, rescan and permanent delete on a sandbox folder work on the real exe.
