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
| Self-update (GitHub latest release, download, verify, swap) and the update flow (`UpdateManager`) | `src/Updater.cs` |
| About window and update window, both on the shared `AppDialog` frame (logo left, content column, buttons bottom right) | `src/About.cs` |
| Pie chart pane (donut linked to the contents list) | `src/Chart.cs` |
| Scanner / deleter / interop (domain, not UI) | `src/Scanner.cs`, `src/Deleter.cs`, `src/Native.cs` |
| Asset generator (icon `.ico`, 256 px `app.png` for the README, 1280x640 `social-preview.png` for GitHub) | `tools/make_icon.ps1` (run by every build) |
| macOS app (Swift + AppKit, one target) | `macos/Sources/DiskSpaceAnalyzer/*.swift`; `Tokens.swift` is generated from `src/Theme.cs` by `macos/gen_tokens.py` (git-ignored) |
| macOS build | `macos/build.sh` -> universal `Disk Space Analyzer.app` (arm64 + x86_64), ad-hoc signed, packed into `DiskSpaceAnalyzer-macOS.dmg` at the repo root; version from `src/Program.cs`, icon from `assets/app-1024.png` |
| Build script | `build.ps1` -> compiles into `bin/`, moves `DiskSpaceAnalyzer.exe` to the repo root (git-ignored), deletes `bin/` |
| Version | `src/Program.cs` (`AssemblyVersion` / `AssemblyFileVersion`), `src/app.manifest` |

## App identity

- Name: Disk Space Analyzer. Repo: `sphrd/DiskSpaceAnalyzer`. License: MIT.
- AppUserModelID: `sphrd.DiskSpaceAnalyzer`.
- Icon concept: **a pie chart shaped like Pac-Man facing left = disk usage analyzer that frees space.** No tile
  (transparent background) everywhere: exe/window/taskbar icon, README logo, social preview and the in-app logo.
  One circle split by colour, shading dark -> light from top left via the right half to bottom left: upper-left
  `#3d6bcc`, right half `#5e88e6`, lower-left `#81a2eb` (tuned by the user from Tailwind CSS blue 800 / 600 / 400), mouth on the left; the left
  sections run a few degrees under the right half, so there are no seams. Checked at 16 / 24 / 32 / 48 / 256 px on dark
  and light backgrounds.
- **Logo colours are edited in one place: the `LOGO_TOP_LEFT` / `LOGO_RIGHT` / `LOGO_BOTTOM_LEFT` lines in
  `src/Theme.cs`.** `tools/make_icon.ps1` reads them (and all other colours it uses) from `Theme.cs`, so `build.ps1`
  regenerates the exe icon, `assets/app.png` and `assets/social-preview.png` to match the in-app logo.
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
- 2026-10-28: "the right pane should only have the pie chart and not the list below it. thats redundant. instead when
  hovering on the pie chart sectors the app should highlight the path in the middle pane, and vice versa. the pie chart
  should represent the middle pane exactly by its order. when categorizing them as others (small files) it should
  highlight multiple items that belong there."
- 2026-10-28: "make the free space section fo the chart, empty. align it always in a way that the mid line of the free pie
  section, is aligned with the vertical line and is at the top."
- 2026-10-28: "remove the dashed line. there should be nothing. dont show the chart before scan is done. or show the app
  logo and then transform animation into the chart."
- 2026-10-28: "put the logo wihtout th ebackground, and the circle of the logo should be aligned with the charts position and
  size. put the free space section where it was, on the left of the top vertical line."
- 2026-10-28: "change the colors of the logo's circle. make them darker blue. put the color of thebottom left arc into the
  right hald circle, and the color of the icon background to the two left side arcs. remove the icon backgroun as well
  everywhere (only the reverse pacman)"
- 2026-10-28: "no, make the right one a darker blue, top left one darker than that, and bottom left one, lighter than the
  right one. so like a shade going from top left to bottom left from dark blue to light blue. use mainstream good
  looking shades. point out where i can adjust colors manually"
- 2026-10-28: "when someone has the app downloaded, and a newer release is available, how to add an update manager section?
  to prompt them and take them to the download page. and then download and replace current version."
- 2026-10-28: "also add about, and check for updates menu to the app. about shows version and author as sphrd. after that
  commit and push all, replace the release."
- 2026-10-28: "do the macos dmg. but before that, why did you put the about and update in the help button? make it a menu bar
  item on the top. make two buttons About, Check for updates. Also update the design language and choices into the App
  design agent file on github."
- 2026-10-28 (macOS): user asked for the macOS dmg in the same repository and the same release flow; Mac available for
  final hands-on checks.
- 2026-10-28: "the update prompt, whether its to say you have the latest version, or else should be in the same looking
  window as the about window. fix that, and update app design agent."
- 2026-10-28: "why the menu bar is there? it should be next to the apps logo and name. on that bar. like every other app."
- 2026-10-28: "reduce the default app size. it doesnt fit in my screen." (1920x1080 at 125 %: 1500x880 became
  1875x995 px and `CenterScreen` pushed it below the taskbar.)
- 2026-10-28: "What other functionalities we can add to the app to make it competetive in its area?" Chosen: master file
  table reading (WizTree speed), multi-ring chart, cleanup suggestions, collect-then-delete list with Recycle Bin option,
  largest files view, file-type breakdown, duplicate finder, old files filter, search and filter, compare with the last
  scan, CSV/HTML export, size on disk vs file size (compressed, sparse, hard links, OneDrive online-only), refresh after
  changes (live updates).

- 2026-10-28: "14-check self-test runs on both platforms in CI. what does it mean? do you save every scan? that builds up in
  size, save only last one. in about section mention that running as admin will increase speed. also mention the keyboard
  shortcuts." + crash report: PathTooLongException in ApplyChanges (live updates) when run as administrator.
- 2026-10-28: "in the chart, when not hovering on pies, instead of writing total in the middle right the name of hte current dir.
  also make it clickable so that i can jump one dir above by clicking. (as a way to reverse the click on one of the pies).
  default to Size on disk instead of Size. The pie chart is not animating transforming from the logo now, why?"

- 2026-10-29: "the mac app takes very long on some files, they were logicx files on my icloud ... it shouldnt be downloading
  the files ... in the dmg make the arrow to show u need to drag the app into Applications folder. on first run, prompt the user to
  give full disk access ... as the default make the size of the right pane bigger so that it is a square when opened up.
  (maximum chart radius) on the Readme and github page, re-read the whole doc and redo it. everything that was for earlier
  versions should be deleted. "Why" section is pointless ... some mentions of keyboard shortcuts are just for Windows version."
- 2026-10-29: "after full disk access or run as admin has been done, the button should disappear. I noticed a bug, when i had a
  large folder in the delete list and deleted it but some files were in use so they didnt delete, in the delete list the size was
  shown as the initial size, not the one after partial delete. Also change the shortcuts to cmd+D/ctrl+D for add to delte list, and
  cmd+shift+D/ctrl+shift+D for permanent delete. Also in the suggestion tab, ther is no button to delete all the suggested
  permanently. there should also be a selector for the suggested files that has also the tree browser with select all deselect
  all buttons, so that user can customize seletion and delete direvtly form there. also give a disinguishing color to the
  suggestion tab."

- 2026-10-29: "after deleting files outside the program, in the suggestion tab i see still the old suggestions even after re
  scanning. Also when hovering show full file name an path."

- 2026-10-29: "suggestions tab: only refresh, select all, deselect all, permanent delete buttons. that it. make these buttons
  smaller. for the refresh and the rescan put a circular arrow button. app should open in suggestions, put it in the first tab.
  make the font of the maount that can be freed larger and mke it bold so it pops out more. when hovering on items (both left and
  middle pane) i still dont see the preview that shows full path and name of the file next to the cursor (or bundled paths for
  suggestions in different dirs)"

- 2026-10-29: "make the chart window a bit smaller by default. make the Contents tab the default tab again. then u can commit and
  release" (asked: Contents first and opens there, Suggestions second).

- 2026-10-29: "lets add application uninstaller. with fully removing all dependancies of the app. later we can add a registry file
  checker." Chosen: Apps tab after Suggestions; official uninstaller first, then leftovers; Windows registry leftovers with a .reg
  backup; Microsoft Store apps included; macOS too. Registry checker: later.

- 2026-10-29: "not same as windows, i want it to show the app when u open th etree to show all the files that have been added by that
  app, after official uninnstaller runs, it should recheck the remainder of files and ask to delete them as well." Confirmed: both
  platforms; macOS: one confirmation, app and all its files to the Trash together.

- 2026-10-29: "show the app logos for the apps found." Both platforms.

- 2026-10-29: "there are lots of bugs now when im trying to open the arrow of each app, i dont understand where the app goes. it
  doesnt expand, some apps that show up as folders move around... check it." Causes (Windows): apps with found items were sorted
  to the top, so expanding moved the app away; nothing showed while searching and the arrow vanished when nothing was found;
  apps sharing a folder had no arrow; double-click opened Explorer. Fixed: order is partly-removed first, then size (expanding
  never moves a row); every app has an arrow; `Looking for its files and settings...` / `No files or settings found.` child line;
  double-click on an app row opens/closes it; scroll position kept by row identity.

- 2026-10-29: "for the pie chart what is the current limit to display the file/folders or to put them in others? make a new rule
  that the size limit that makes them appear as others to be 5% of the current total circle, meaning 7.2 degrees of the circle.
  above that should be displayed." Was: 12 largest rows of at least 1.5 %. Confirmed: 2 % (7.2 degrees), no 12-slice cap,
  outer rings unchanged (arcs down to 1.5 degrees).

- 2026-10-29: "ok, as is 2% of the total circle, but put a minimum number of slices. (max is already set) minimum should be 10
  slices before going to others. except when number of files are less obviously. which then all files should get the slice."
  (Files view on C: showed one slice + a huge grey arc: only pagefile was over 2 %.) Rule: own slice for every row of at least 2 %
  AND always the 10 largest rows (all rows when fewer), at most 50.

- 2026-10-29: "then why the charts are not following the rules??????????" (README pictures: the 10-slice minimum gave slices of
  a few KB next to GB ones, invisible; and the "[N not listed]" row took one of the 10 places.) Asked about a minimum slice width:
  "keep proportions intact, but if smaller than 2% group all of them together." Final rule: own slice for every row of at least
  2 % (true proportions, at most 50); ALL smaller rows form ONE grey slice (at the first small row's place), no minimum count.

- 2026-10-30: (after a Suggestions delete listed 5,000+ failures in C:\Program Files\Microsoft VS Code\...\extensions\copilot\node_modules
  and C:\Program Files\WindowsApps\MicrosoftWindows.Client.WebExperience...\node_modules) "why? i have VSCode open but for the rest?
  Also change the deletion page. make it similar to the scanning page, but in a more alerting color (maybe red) and the app should update
  the view live while deletion is happening. whichever tab it is, whether suggestions or else. i should see the files leaving the list as
  theyre being deleted. Also the prompt that this cant be undone when deleting permanently has a windows shell default look, change it into
  apps theme, with an alert sign and the logged detailes in a scrollable box inside." + "all other prompts like the one for apps own
  uninstaller running, should also be changed into apps theme."
  Cause: the "node_modules folders" suggestion took every node_modules in the scan, including those shipped inside installed programs
  (VS Code's built-in Copilot extension lost its node_modules; Windows protected WindowsApps: access denied, nothing removed there).
  Fixed: only a project's node_modules (package.json beside it), never under Windows, Program Files (incl. WindowsApps), ProgramData,
  AppData, hidden dot-folders, "resources" or "extensions" folders. General rule: a cleanup suggestion never points inside installed
  programs or tools.

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
- Pie chart pane (right, 330 px, fixed) holds only the title (selection + path, or used/total/free for a drive) and the
  donut; the centre shows the hovered slice (name, size, share) or the total. Slices mirror the folder contents list
  exactly, in its current row order (rebuilt after every fill and re-sort): every row of at least 2 % of the total
  (7.2 degrees) gets its own slice in true proportion (at most 50; accent shades); all smaller rows together form one grey "N smaller items"
  slice. A drive root appends "System / not readable" (used - scanned) and "Free space". Free space is an empty
  gap (nothing drawn; a faint `border` fill only while hovered). Slices start at 12 o'clock and run clockwise, so the
  free gap (last) always ends at 12 o'clock, just left of the top vertical line.
- No chart before a scan: an unscanned or scanning drive shows the app logo (only the Pac-Man pie, `LOGO_*` colours) on exactly the chart's circle, plus a muted note. When the scan finishes, the logo fades out on
  that circle while the chart sweeps in clockwise from 12 o'clock and the hole opens (650 ms, ease-out). Hover is off during the morph. This is the only animation besides the
  spinners (allowed by §2.1 as a one-off transition; recorded here as a project decision). Hover is linked both ways:
  slice -> its row(s) get an accent outline in the list (scrolled into view if none is visible); list row -> its slice
  pops out. Chart colours are data-only tokens in `Theme.CHART*` (accent shades + greys), allowed by §2.1.
- Message boxes and the folder picker stay OS-styled (accepted by §2.1).

## Deviations from the agent file (kept to preserve existing behavior)

- Window opens at 1280x760 (at most 85 % of the screen's working area, centred in it; min 900x520). The app is a
  three-pane explorer, not the single-column form of §2.5. Layout: toolbar (path row + action row), scan bar (while
  scanning), tree | list | chart panels, status line.
- The path box is editable (type a path + Enter), so only its chevron opens the drive list, not the whole box.
- Deletion has no Cancel on the overlay: the deleter never had cancellation; adding it would change behavior.
- No splash screen: §2.9 asks for one only when startup takes noticeable time; this ~100 KB exe has no unpack step
  and the window appears immediately.
- Folder/file icons are muted glyphs instead of shell icons (yellow shell folders would add a hue).
- The former toolbar **Stop** button became the overlay's **Cancel** (same action; Stop was only usable while scanning).

## Decisions and gotchas

- **macOS version:** native Swift/AppKit app, same behaviour and look as Windows (three panels, linked chart, scan bar,
  update window, About, logo morph), built and checked only on GitHub's macOS runners (this machine is Windows): the
  `macos` CI job runs `build.sh`, a headless `--cli scan /usr/share` and `--cli screenshot` (views render themselves
  into PNGs, no screen-recording permission needed), and attaches `DiskSpaceAnalyzer-macOS.dmg` to the same release as
  the exe. Scanner: `getattrlistbulk` on 8-32 threads, does not follow symlinks and skips every other mount point
  (`getmntinfo`), so on `/` the data volume under `/System/Volumes/Data` is not counted twice (its content is reached
  through the firmlinks `/Users`, `/Applications`...). Drives: `mountedVolumeURLs` with `skipHiddenVolumes`, local and
  browsable only; refreshed on mount/unmount. Free space uses `volumeAvailableCapacityForImportantUsage`.
  macOS-only differences: "Choose..." instead of "Browse...", "Show in Finder", "Full Disk Access..." instead of "Run
  as administrator" (opens the privacy setting), About and Check for Updates in the app menu of the system menu bar,
  Cmd+R rescan, Cmd+Backspace delete, Cmd+Up up. Unsigned (no Developer ID): first launch needs right-click > Open.
  The updater downloads `DiskSpaceAnalyzer-macOS.dmg`, verifies it, mounts it, copies the app next to the running one
  and swaps the bundles (`.old` removed at next start); if the app runs from the disk image or a read-only place it
  asks to move it to Applications or links to the release page.
- Windows CI first, then macOS (`needs: build`), so a tag produces both artefacts on one release. Branches `dev/**`
  also build (no release), which is how the macOS app is compiled and checked before merging.
- **Updates:** 3 s after start the app asks `api.github.com/repos/sphrd/DiskSpaceAnalyzer/releases/latest` (no token;
  drafts and pre-releases are never offered). If the tag (`vX.Y.Z`) is newer than the exe's `AssemblyVersion`, an
  update window (same frame as About: `AppDialog`) offers Update now / What's new / Later; automatic checks are silent
  otherwise. Every step uses that window: Checking..., You're up to date (Close / What's new), Update available,
  Downloading (progress pill + Cancel), Update installed (Restart now / Later), Update check failed / Update failed /
  Can't update here (Open download page). Replaced the amber banner on the user's request (2026-10-28), in both apps.
  Manual check: Check for updates in the title bar, the About window's button, or the version label at the bottom right.
- Custom title bar (`TitleBar` in `Widgets.cs`) replaces the native caption: app logo, app name, then the menu items
  **About** (F1) and **Check for updates** right next to the name, and drawn minimise / maximise / close buttons (close
  hover `#c42b1c`, the Windows convention). `WM_NCCALCSIZE` removes the caption but keeps the native resize borders,
  shadow and Snap; `WM_NCHITTEST` returns HTCAPTION for empty title-bar space (drag, double-click to maximise, system
  menu) and HTTOP* in the top resize strip. Maximised, the client top is moved below the off-screen frame part. History:
  a Help button in the toolbar, then a separate menu bar row under the caption; the user wanted the items on the title
  bar "like every other app". The logo drawing lives in `Theme.DrawLogo` (chart pane + About). Update now: download `DiskSpaceAnalyzer.exe` to
  `<exe>.download` with progress and Cancel, check its size and the SHA-256 `digest` GitHub reports for the asset,
  rename the running exe to `<exe>.old` (Windows allows renaming a running exe), move the download into place, then
  Restart now. The next start deletes `.old` / `.download`, retrying for ~15 s because the previous process may still
  be closing. If the folder is not writable (probed by creating a file), the update window links to the release page instead.
  .NET 4.x needs TLS 1.2 switched on explicitly for GitHub; GitHub needs a User-Agent header.
- **Releasing for the updater:** the tag must be `v` + the new `AssemblyVersion` (higher than before) and the asset must
  be named exactly `DiskSpaceAnalyzer.exe`; otherwise the update window only links to the release page. Installs older than
  v1.5.0 have no updater: those users must download v1.5.0 once by hand.
- Tested end to end with a local build labelled 1.3.9 against the published v1.4.0: banner shown, download, checksum,
  swap, restart into 1.4.0; installed file identical to the release asset.

- Window placement: `FormStartPosition.CenterScreen` opened the window off-centre with its bottom under the taskbar
  (y 224..1083 on a 1020 px working area). The form now sets `StartPosition = Manual` and centres itself in the working
  area of the screen under the cursor. Check placement with `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`.

- Mapped network drives are not listed: `DriveInfo.IsReady` on a disconnected share can block for seconds. A network
  path can still be typed and scanned. Drives are re-read on `WM_DEVICECHANGE` (arrival / removal), deferred while a
  scan or delete runs.
- While a scan runs the user can navigate. When it ends, the view stays where the user is: a selection inside the old
  copy of the rescanned tree is mapped onto the new one by path (`FindByPath`); another drive stays selected.
  Model updates (`ReplaceNode` etc.) use the largest-files list of the root that owns the node (`TopOf`), not the
  active one, because the user may have switched drives during a background rescan.
- Chart hover state is two values: `hover` (what is drawn, also set from the list) and `reported` (last slice sent
  through `HoverChanged`). With one shared value, a list hover that set the slice first swallowed the chart's own
  hover event, and the list highlight never appeared.
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
  Since v1.7 this is built in: `.\tools\make_demo.ps1 C:\Demo` (sparse files, ~250 MB on disk, a few duplicate pairs),
  `DiskSpaceAnalyzer.exe --cli screenshot <out> C:\Demo --demo` (finds duplicates first, then shows the folder as
  `E:\ Data` with made-up `F:\`/`G:\` drives; skips Suggestions and Changes, which would show this PC), copy
  `1-contents.png` to `assets\screenshot.png` and `6-duplicates.png` to `assets\screenshot-duplicates.png`, then
  `.\tools\make_demo.ps1 C:\Demo -Remove` and delete `%LOCALAPPDATA%\DiskSpaceAnalyzer\history`.
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

### Views, index and cleanup (v1.7.0)

- **File index:** every scanned folder keeps a `FileRec[]` (name, size, size on disk, modified, attributes); the
  Files, File types, Duplicates and export work from memory. `DirNode.OnDisk` picks the measure for every view
  (`Size`/`LocalSize` are properties over `Bytes`/`Alloc`); switching re-sorts all child lists.
- **Size on disk (folder walk):** rounded up to clusters; compressed, sparse and reparse-point files (Compact OS/WOF)
  ask `GetCompressedFileSize`; cloud placeholders (OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS) count 0. Hard links
  are counted per name here (only the master file table can tell them apart).
- **Master file table (`Mft.cs`):** only for a drive root with admin rights; reads `\\.\C:` sequentially in 4 MB chunks
  from the runs of record 0, applies the update sequence fixups, merges extension records by base reference. File size
  = unnamed `$DATA`; size on disk = allocation of all `$DATA` streams (sparse/compressed: compressed size), except for
  NTFS's own records 0-15 (`$BadClus:$Bad` claims the whole volume). Extra hard links are listed in their folders with
  size on disk 0. On the GitHub runner: 1.2 M files in about 10 s (folder walk: over 7 minutes), size on disk within
  0.5 % of the drive's used space. Any failure falls back to the folder walk; `--cli scan C:\ --mft` prints why.
- **Gotcha:** picking the long name of each extra hard link by scanning the whole link list was quadratic (WinSxS has
  100k+ links) and looked like a hang: use a dictionary keyed by (record, parent).
- **View tabs** sit above the list and the chart (Contents, Files, File types, Duplicates, Suggestions, Changes, Delete
  list); each view is a `Page` with a caption line, an optional filter/action bar (wraps; a search box gets its own row
  when it wraps) and a `SizeListView`. `BuildSlices` turns any list into slices (rows of a duplicate group share their
  group's slice via `ItemInfo.GroupOf`).
- **Deletion** goes through `DeleteTarget` (file, folder, folder contents only, Recycle Bin) and `ConfirmAndDelete`;
  afterwards `ApplyDeleted` drops removed nodes and rescans folders that still exist. Contents-only targets skip files in
  use quietly (counted). Recycle uses `SHFileOperation` with ALLOWUNDO + WANTNUKEWARNING.
- **Default layout:** the chart pane is 4/5 as wide as the panes are tall (square in v1.7.2, a bit big), the list keeps at least 380 px.
- **Suggestions tree:** parent rows (chevron + three-state checkbox) and child rows per target (`ItemInfo.Check`,
  `Expandable`, `Data`; children use `GroupOf` so they share the parent's chart slice). Clicks on the chevron/checkbox are
  caught in `WM_LBUTTONDOWN` before the list selects the row. Safe suggestions start ticked, review-first ones unticked.
  The tab title is green (`Theme.SUGGEST`). Shortcuts: Ctrl+D add to delete list, Ctrl+Shift+D (or Del) delete permanently.
- **After every delete** `AfterDeleteRemeasure` measures each attempted target (and the same paths in the delete list and
  suggestions) from the updated scan: gone ones leave, the rest keep the remaining size with `partly deleted -- ...`.
  (v1.7.2 kept the original size after a partial delete.) **Run as administrator** is hidden when elevated.
- **Suggestions refresh:** found once and cached until Look again in v1.7.2. Now `suggestionsDirty` is set by every
  scan, rescan, delete, measure switch and live change; the tab finds them again when shown (live: at most every 20 s,
  temp folders change constantly). Ticks are kept by path, expanded suggestions by name.
- **Gotcha:** live updates ignored every event for 3 s (then 0.5 s) after our own delete, so outside changes in that window
  were lost (the fast CI runner caught it). Now filtered by path: `BeginOwnChanges(targets)` / `EndOwnChanges()`; events at or
  under the paths being deleted are ignored during the delete and 3 s after; all other events are always queued.
- **Hover:** `SetToolTip` on a ListView/TreeView shows once per control entry, not per row, and a TreeView takes over an
  attached tooltip and sizes it to the row (text cut off). Rows and nodes use `Hover()`: a separate owner-drawn ToolTip shown
  on the form 16/20 px from the cursor 450 ms after the row changes, hidden on leave/click/scroll. Suggestions in several places
  list up to 8 paths. Screenshot mode checks it with the real cursor (`*-hover.png`).
- **Suggestions tab** is second (after Contents, which the app opens on; v1.7.3); bar = circular-arrow refresh (icon button), Select all, Deselect all,
  Delete permanently (compact 9 pt buttons); headline `<size> can be freed` 15 pt bold in `SUGGEST`. Clicking a tree folder
  while on Suggestions switches to Contents. **Rescan folder** is a circular-arrow icon button (`RoundButton.Glyph`).
  Shots are numbered in tab order: `1-contents`, `2-suggestions`, ... `5-duplicates`.
- **Apps tab (`Apps.cs`):** uninstall entries from HKLM 64/32 and HKCU (skips SystemComponent, updates with ParentKeyName or
  ReleaseType Update/Hotfix), plus Store apps via `Get-AppxPackage` (SignatureKind Store, not framework/non-removable; name from
  AppxManifest DisplayName). Size = measured program folder (scan index first), else EstimatedSize; apps sharing one folder (Office
  and language packs) count it once (on the entry with the largest EstimatedSize). Uninstall: MSI `msiexec /x {code}`, else the
  UninstallString (shell execute so UAC works), Store `Remove-AppxPackage`; waits for the process, then until the uninstall key is
  gone (NSIS/Inno hand over to a temp copy), overlay button **Continue** to stop waiting. Leftovers: folders in Roaming, Local,
  LocalLow, Local\Programs, ProgramData, Program Files (x86), Documents, Store Packages; Start menu/desktop shortcuts; registry
  Software\<App> and Software\<Publisher>\<App> (HKCU, HKLM 64/32), the uninstall key if left. Names: normalised display name,
  name without version/bitness, folder name, key name (`_is1` stripped); generic words never match; a publisher folder only when
  all it holds is the app. Admin-only items start unchecked without elevation. Registry keys: `reg export` appended to
  `%LOCALAPPDATA%\DiskSpaceAnalyzer\registry-backups\<app> <date>.reg` before `DeleteSubKeyTree`; a key that can't be backed up is kept.
- **Apps flow (v1.8):** every app expands (first time: search in the background) into all its items, program folder first
  (child rows `<Kind>: <name>`, no checkboxes while installed). Uninstall: official uninstaller, then the same search again; if anything
  remains a dialog lists it and asks `Delete them too?` (Yes = delete with registry backup; No = keep as checkable leftovers).
  macOS: one confirmation moves the bundle and all its items to the Trash. `Look for leftovers` menu item removed.
- **App logos:** each app row draws the app's own icon (`ItemInfo.Icon`) instead of the folder, loaded with the list in the
  background at the row icon size. Windows order: `DisplayIcon` (`file,index`, via `SHDefExtractIconW` at the exact size) ->
  MSI `HKCR\Installer\Products\<packed code>\ProductIcon` -> main exe in the program folder (or one level down; named like the
  app, else the largest; skips unins/setup/update/helper) -> folder icon. Store apps: `Square44x44Logo` (or `<Logo>`) from
  AppxManifest.xml, the scaled file closest to the size, unplated preferred, no contrast variants. macOS: `NSWorkspace.icon(forFile:)`.
- **Deleting (v1.8.2):** red delete strip = `ScanBar(danger: true)` docked under the scan bar: DANGER_BG fill, DANGER_BORDER outline,
  DANGER spinner and progress bar (files done / files in the targets from the scan), DANGER_FG detail line (current path), **Stop**
  (`DeleteJob.Stop`: finishes the current file; the rest is untouched and rescanned). No overlay: the window stays browsable, actions
  that change the disk are disabled (`busy`). `DeleteJob.Gone` (every deleted file/folder) and `Finished` (targets) are drained on the
  150 ms UI timer; rows of the open list and of Contents whose path (or a parent folder) is gone, or whose targets are all finished,
  are removed and the chart rebuilt. `RunDelete` is shared by ConfirmAndDelete and RemoveLeftovers.
- **Prompts:** every prompt is `AlertForm` (About frame): warning sign (`Theme.DrawWarningSign`, DANGER for permanent deletes and
  uninstalls, WARN for warnings/errors) or the logo, heading (13 pt bold), body, amber note, `DetailsBox` (rounded SURFACE box, pill
  scrollbars both ways, path then muted size/reason after a tab, Ctrl+C copies, **Copy list** button), buttons bottom right; destructive
  primary is red (`ButtonKind.Danger`), Enter/Escape cancel on destructive prompts. Only the startup-crash box stays a MessageBox.
- **Gotcha:** `RoundButton.MinChars` replaced the text width (long primary labels were cut: "Delete perm..."); it is a minimum now.
- **Gotcha:** `Directory.GetDirectories` on .NET 4 (no 4.6.2 target) rejects `\\?\` paths; the leftover search silently found
  nothing until it used plain paths (these roots are short).
- **Chart centre** shows the shown folder's name and size (not "Total"); in Contents it is clickable and goes up one
  folder (the reverse of clicking a slice). **Size on disk** is the default measure.
- **Gotcha (morph not visible):** the logo-to-chart morph was timed by the wall clock; right after a big scan the UI
  thread is busy (tree rebuild, history save, garbage collection of the index), so the 650 ms passed before the first
  frame and the chart just appeared. The morph now advances per timer tick by at most 50 ms (Windows and macOS), and the
  self-test checks that it drew at least 8 frames.
- **History:** `%LOCALAPPDATA%\DiskSpaceAnalyzer\history\<root>.dsa` (gzip: folder names and sizes, no file names).
  One file per drive/folder, overwritten by each full scan; only the 10 most recently written files are kept.
- **Gotcha (v1.7.0 crash):** `Path.GetDirectoryName`/`GetFileName` throw `PathTooLongException` beyond 260 characters
  (the exe has no 4.6.2+ target framework, so .NET uses legacy path checks). The live-update timer hit it on a real drive.
  Paths from the disk go through `Native.ParentDir` / `Native.FileName` / `Native.IsDirectory`, the timer body is
  guarded, and the self-test now creates and removes a 347-character path while live updates run.
- **Live updates:** `FileSystemWatcher` per scanned root; changes are applied every 2 s on the UI thread (re-read a
  folder's own files, scan new folders, drop removed ones, rename in place); the contents list refreshes at most every
  4 s and not while rows are selected. Our own deletes are ignored for 3 s. Overflow shows "Changes missed - rescan".
- **Checks:** `--cli selftest <scratch>` (14 checks incl. live updates, Recycle Bin on CI, contents-only delete,
  duplicates, export, history) and `--cli screenshot <out> <folder>` (every view) run the real window; CI runs the
  self-test and the master file table check on the elevated Windows runner.

## Releases

- v1.0.0 (2026-10-27): first release, light WinForms UI, ~66 KB.
- v1.1.0 (2026-10-27): design-language dark theme restyle, ~96 KB. Verified: Program Files scan
  matches pre-restyle counts; cancel, rescan and permanent delete on a sandbox folder work on the real exe.
- v1.2.0 (2026-10-27): pie-chart icon, hover fix in the lists, aligned Size / % of parent columns in the tree,
  `--cli scan` headless mode, build moves the exe to the root. ~100 KB.
- v1.3.0 (2026-10-27): auto-scan of the system drive at startup, background scan with progress bar, all local drives
  in the tree (others scanned on demand), percentage bars in the tree, pie chart pane with free space, 1500x880 window,
  smooth Pac-Man icon. ~117 KB.
- v1.4.0 (2026-10-28): pie chart pane is only the donut, linked by hover to the contents list in both directions and
  ordered like the list (small rows merged); free space is an empty gap ending at 12 o'clock; app logo shown before a
  scan and morphing into the chart; tile-less shaded logo everywhere; default window 1280x760, centred in the working
  area. ~123 KB.
- v1.5.0 (2026-10-28): self-update from GitHub releases (banner, download, checksum, swap, restart), Help menu with
  Check for updates and About (version, author sphrd). ~140 KB.
- v1.6.0 (2026-10-28): About and Check for updates as items in the custom title bar next to the logo; every update
  step shown in the About-style app dialog (no banner); first native macOS version (universal .dmg, Swift/AppKit).
- v1.7.0 (2026-10-28): master file table scanning, file index with size on disk, view tabs (Files with search and
  filters, File types, Duplicates, Suggestions, Changes, Delete list with Recycle Bin), multi-ring chart, export to HTML/CSV,
  live updates, self-test and screenshot modes. macOS has the same views (no master file table; Trash instead of the
  Recycle Bin; FSEvents; iCloud dataless files count 0 on disk; shortcuts ⇧⌘D add to delete list, ⌘F search, ⇧⌘E export).
- v1.7.1 (2026-10-28): fix a crash in live updates on paths longer than 260 characters; About shows the speed tip and
  the keyboard shortcuts; scan history keeps only the last scan of the 10 most recent drives/folders; the chart centre
  names the folder and goes up on click; size on disk is the default; the logo morph is visible again after big scans.
- v1.7.2 (2026-10-29): macOS never downloads iCloud files or folders (dataless items are skipped, materialization off),
  first-run Full Disk Access prompt and no per-folder prompts without it, dmg with a drag-to-Applications arrow; square chart
  pane on both platforms; README rewritten.
- v1.7.3 (2026-10-29): Suggestions as a checkbox tree (Select all / Deselect all / Delete permanently, green tab, big headline);
  hover box with full name and path for every row and folder; circular-arrow Rescan; Ctrl/Cmd+D add to delete list,
  Ctrl/Cmd+Shift+D delete permanently; delete list keeps the real remaining size after a partial delete; suggestions refresh
  after any change; live updates never drop outside changes; permission buttons hide when granted; chart pane 4/5.
- v1.8.0 (2026-10-29): Apps tab (uninstaller): every installed app with its own logo and real size; expand an app to see all
  its files, settings and (Windows) registry keys in place; Windows runs the official uninstaller, checks again and asks to delete
  what is left (registry backed up to .reg); macOS moves the app and all its files to the Trash in one step. Chart: the 10
  largest items always get their own slice, plus every item of at least 2 % (7.2 degrees), at most 50.
- v1.8.1 (2026-10-29): chart: every item of at least 2 % (7.2 degrees) gets its own slice in true proportion; all smaller
  items together form one grey slice (no more invisible hairline slices); README shows Suggestions and Apps.
