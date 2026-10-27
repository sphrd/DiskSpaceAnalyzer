using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>A top-level entry of the tree: a drive (scanned or not) or a folder the user scanned.</summary>
    internal sealed class ScanRoot
    {
        public string Path = "";
        public bool IsDrive, IsSystem;
        public string Label = "";
        public long Total, Free;                 // capacity of the drive that holds Path (0 = unknown)
        public DirNode Node;                     // null until scanned
        public bool Scanning;
        public List<FileEntry> Top = new List<FileEntry>();

        public string DisplayName { get { return IsDrive && Label.Length > 0 ? Path + "  " + Label : Path; } }
    }

    internal sealed class MainForm : Form
    {
        const string AppName = "Disk Space Analyzer";
        const int TopCount = 1000;
        const int MaxFilesShown = 2000;

        readonly bool isAdmin;

        DirNode current;
        readonly List<ScanRoot> roots = new List<ScanRoot>();
        ScanRoot active;
        bool drivesChanged;
        bool scanning;
        ScanRoot scanRoot;
        ScanBar scanBar;
        /// <summary>Scanned tree of the root that holds the current selection (null when that root is not scanned).</summary>
        DirNode root { get { return active != null ? active.Node : null; } }
        List<FileEntry> topFiles = new List<FileEntry>();
        Scanner scanner;
        DeleteJob deleteJob;
        bool busy;
        Control lastView;

        readonly PathInput pathBox;
        readonly Label pathLabel;
        readonly RoundButton btnBrowse, btnScan, btnUp, btnRescan, btnExplorer, btnDelete, btnAdmin;
        readonly Panel toolbar;
        readonly SplitContainer split, split2;
        readonly PieChart chart;
        readonly DarkTreeView tree;
        readonly TabStrip tabs;
        readonly ScrollHost contentsHost, largestHost;
        readonly SizeListView contents, largest;
        readonly Label statusLabel, driveLabel;
        readonly BusyOverlay overlay;
        readonly System.Windows.Forms.Timer uiTimer;
        readonly ContextMenuStrip menu;
        readonly ToolTip tips = new ToolTip();
        ToolStripMenuItem miOpen, miExplorer, miCopy, miRescan, miDelete;

        public MainForm(string initialPath)
        {
            using (var g = CreateGraphics()) Theme.Scale = g.DpiX / 96f;
            isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

            Text = AppName + (isAdmin ? " (Administrator)" : "");
            Font = Theme.FONT;
            BackColor = Theme.BG;
            ForeColor = Theme.FG;
            AutoScaleMode = AutoScaleMode.None;
            // 1500x880 by default, shrunk to fit the working area of smaller screens.
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(S(1500), wa.Width - S(20)), Math.Min(S(880), wa.Height - S(20)));
            MinimumSize = new Size(S(900), S(520));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;
            Padding = new Padding(S(15), S(10), S(15), 0);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            // Toolbar: path row, then the actions that work on the selection
            pathLabel = new Label { Text = "Drive / folder:", AutoSize = true, ForeColor = Theme.FG, BackColor = Theme.BG, Font = Theme.FONT };
            pathBox = new PathInput();
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady) pathBox.Choices.Add(d.Name);
            pathBox.Submitted += (s, e) => StartScan();
            pathBox.ChoiceCommitted += (s, e) => StartScan();
            tips.SetToolTip(pathBox.Box, "Drive or folder to analyze (you can also drag a folder onto the window)");

            btnBrowse = Btn("Browse...", ButtonKind.Secondary, "Choose a folder", Browse);
            btnScan = Btn("Scan", ButtonKind.Primary, "Analyze the drive or folder (Enter)", StartScan);
            btnAdmin = Btn("Run as administrator", ButtonKind.Secondary, "Restart elevated to read folders that need admin rights", RestartAsAdmin);
            btnAdmin.Visible = !isAdmin;
            btnUp = Btn("Up", ButtonKind.Secondary, "Go to the parent folder (Backspace)", GoUp);
            btnRescan = Btn("Rescan folder", ButtonKind.Secondary, "Rescan the selected folder (F5)", RescanSelected);
            btnExplorer = Btn("Show in Explorer", ButtonKind.Secondary, "Open the selected item in Windows Explorer", ShowInExplorer);
            btnDelete = Btn("Delete permanently", ButtonKind.Warn, "Permanently delete the selected items, bypassing the Recycle Bin (Del)", DeleteSelected);

            toolbar = new Panel { Dock = DockStyle.Top, BackColor = Theme.BG };
            toolbar.Controls.AddRange(new Control[] { pathLabel, pathBox, btnBrowse, btnScan, btnAdmin, btnUp, btnRescan, btnExplorer, btnDelete });
            toolbar.Layout += (s, e) => LayoutToolbar();

            // Context menu
            menu = new ContextMenuStrip
            {
                Renderer = new DarkRenderer(), BackColor = Theme.SURFACE, ForeColor = Theme.FG, Font = Theme.FONT,
                ShowImageMargin = false, Padding = new Padding(0, S(4), 0, S(4)), DropShadowEnabled = !Theme.HasDwmCorners
            };
            miOpen = new ToolStripMenuItem("Open folder", null, (s, e) => OpenSelected());
            miExplorer = new ToolStripMenuItem("Show in Explorer", null, (s, e) => ShowInExplorer());
            miCopy = new ToolStripMenuItem("Copy path", null, (s, e) => CopyPath());
            miRescan = new ToolStripMenuItem("Rescan folder", null, (s, e) => RescanSelected()) { ShortcutKeyDisplayString = "F5" };
            miDelete = new ToolStripMenuItem("Delete permanently...", null, (s, e) => DeleteSelected()) { ShortcutKeyDisplayString = "Del" };
            menu.Items.AddRange(new ToolStripItem[] { miOpen, miExplorer, miCopy, miRescan, new ToolStripSeparator(), miDelete });
            foreach (ToolStripItem it in menu.Items) it.ForeColor = Theme.FG;
            miDelete.ForeColor = Theme.WARN_FG;
            menu.Opened += (s, e) => Theme.RoundPopup(menu.Handle);
            menu.Opening += (s, e) =>
            {
                if (menu.SourceControl != null) lastView = menu.SourceControl;
                var sel = GetSelection();
                bool any = sel.Count > 0 && !busy;
                miOpen.Enabled = any && sel.Count == 1;
                miExplorer.Enabled = any;
                miCopy.Enabled = any;
                miRescan.Enabled = any && !scanning && sel.Count == 1 && sel[0].IsDir;
                miDelete.Enabled = any && !scanning;
            };

            // Folder tree. The image list only reserves the icon slot; glyphs are drawn by the tree.
            tree = new DarkTreeView
            {
                Dock = DockStyle.None, ItemHeight = S(26), Indent = S(20), ContextMenuStrip = menu,
                ImageList = new ImageList { ImageSize = new Size(S(16), S(16)) }
            };
            tree.BeforeExpand += (s, e) => Populate(e.Node);
            tree.AfterSelect += (s, e) =>
            {
                var d = e.Node.Tag as DirNode;
                if (d != null) ShowContents(d);
                else if (e.Node.Tag is ScanRoot) ShowUnscanned((ScanRoot)e.Node.Tag);
            };
            tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };
            tree.NodeMouseDoubleClick += (s, e) => { var r = e.Node.Tag as ScanRoot; if (r != null) ScanPath(r.Path); };
            tree.Enter += (s, e) => lastView = tree;
            // Samples are the widest expected value or the header title, whichever is wider.
            tree.ColumnSamples = new[] { "999.9 MB", "100.0 %" };
            tree.ColumnMuted = new[] { false, true };
            tree.ColumnText = TreeColumns;
            tree.BarColumn = 1;
            tree.BarValue = TreeBar;
            var treePanel = new RoundedPanel(12, 8) { Dock = DockStyle.Fill, TrackFocus = true };
            treePanel.Controls.Add(new ScrollHost(tree) { Dock = DockStyle.Fill });
            treePanel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(4), BackColor = Theme.SURFACE });
            treePanel.Controls.Add(new TreeHeader(tree, "Name", "Size", "% of parent") { Dock = DockStyle.Top });

            // Folder contents
            contents = new SizeListView { ContextMenuStrip = menu };
            contents.SetColumns(new[] { "Name", "Size", "% of folder", "Files", "Folders", "Modified" },
                new[] { "name", "size", "size", "files", "dirs", "mod" }, new[] { 320, 90, 170, 90, 80, 140 });
            contents.BarColumn = 2;
            contents.ColumnClick += (s, e) => contents.SortBy(e.Column);
            contents.DoubleClick += (s, e) => OpenSelected();
            contents.Enter += (s, e) => lastView = contents;

            // Largest files
            largest = new SizeListView { ContextMenuStrip = menu };
            largest.SetColumns(new[] { "Name", "Size", "Folder", "Modified" },
                new[] { "name", "size", "folder", "mod" }, new[] { 260, 90, 520, 140 });
            largest.ColumnClick += (s, e) => largest.SortBy(e.Column);
            largest.DoubleClick += (s, e) => OpenSelected();
            largest.Enter += (s, e) => lastView = largest;

            contentsHost = new ScrollHost(contents) { Dock = DockStyle.Fill };
            largestHost = new ScrollHost(largest) { Dock = DockStyle.Fill, Visible = false };
            tabs = new TabStrip("Folder contents", "Largest files (top " + TopCount + ")") { Dock = DockStyle.Top };
            tabs.SelectedIndexChanged += (s, e) =>
            {
                contentsHost.Visible = tabs.SelectedIndex == 0;
                largestHost.Visible = tabs.SelectedIndex == 1;
            };
            var listPanel = new RoundedPanel(12, 8) { Dock = DockStyle.Fill, TrackFocus = true };
            var gap = new Panel { Dock = DockStyle.Top, Height = S(6), BackColor = Theme.SURFACE };
            listPanel.Controls.Add(contentsHost);
            listPanel.Controls.Add(largestHost);
            listPanel.Controls.Add(gap);
            listPanel.Controls.Add(tabs);

            split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = S(10), BackColor = Theme.BG };
            split.Panel1.BackColor = Theme.BG;
            split.Panel2.BackColor = Theme.BG;
            split.Panel1.Controls.Add(treePanel);

            // Pie chart pane (right). It is drawn from sizes already in memory, never from the disk.
            chart = new PieChart { Dock = DockStyle.Fill };
            chart.SliceClicked += sl => { var d = sl.Tag as DirNode; if (d != null && !busy) SelectDir(d); };
            var chartPanel = new RoundedPanel(12, 8) { Dock = DockStyle.Fill };
            chartPanel.Controls.Add(chart);
            split2 = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = S(10), BackColor = Theme.BG, FixedPanel = FixedPanel.Panel2 };
            split2.Panel1.BackColor = Theme.BG;
            split2.Panel2.BackColor = Theme.BG;
            split2.Panel1.Controls.Add(listPanel);
            split2.Panel2.Controls.Add(chartPanel);
            split.Panel2.Controls.Add(split2);

            // Status line
            var status = new Panel { Dock = DockStyle.Bottom, Height = Theme.FONT_9.Height + S(16), BackColor = Theme.BG };
            statusLabel = new Label
            {
                Text = "Choose a drive or folder and press Scan.", Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false,
                Font = Theme.FONT_9, ForeColor = Theme.MUTED, BackColor = Theme.BG, TextAlign = ContentAlignment.MiddleLeft
            };
            driveLabel = new Label
            {
                Dock = DockStyle.Right, AutoSize = true, Font = Theme.FONT_9, ForeColor = Theme.MUTED, BackColor = Theme.BG,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, S(8), 0, 0)
            };
            status.Controls.Add(statusLabel);
            status.Controls.Add(driveLabel);

            overlay = new BusyOverlay();
            overlay.CancelButton.Click += (s, e) =>
            {
                if (scanner == null) return;
                scanner.Cancel();
                overlay.CancelButton.Enabled = false;
            };

            // Docking runs from the last-added control back: status (bottom), toolbar (top), scan bar (top, under the
            // toolbar), then the panes fill the rest.
            scanBar = new ScanBar { Dock = DockStyle.Top };
            scanBar.CancelButton.Click += (s, e) =>
            {
                if (scanner == null) return;
                scanner.Cancel();
                scanBar.CancelButton.Enabled = false;
            };
            Controls.Add(split);
            Controls.Add(scanBar);
            Controls.Add(toolbar);
            Controls.Add(status);
            Controls.Add(overlay);
            // The overlay covers the whole client area (toolbar and status line too), so it is anchored, not docked.
            overlay.Bounds = ClientRectangle;
            overlay.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            overlay.BringToFront();

            uiTimer = new System.Windows.Forms.Timer { Interval = 150 };
            uiTimer.Tick += (s, e) => UpdateProgress();

            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0 || busy || scanning) return;
                string p = files[0];
                if (!Directory.Exists(p)) p = Path.GetDirectoryName(p);
                pathBox.Text = p;
                StartScan();
            };

            Shown += (s, e) =>
            {
                split.SplitterDistance = S(430);
                split2.SplitterDistance = Math.Max(S(200), split2.Width - S(330) - split2.SplitterWidth);
                LoadDrives();
                RefreshTree(null);
                // The system drive is analyzed automatically; other drives are listed and scanned on demand.
                if (!string.IsNullOrEmpty(initialPath)) ScanPath(initialPath);
                else ScanPath(Path.GetPathRoot(Environment.SystemDirectory));
            };
            FormClosing += (s, e) => { if (scanner != null) scanner.Cancel(); };
            UpdateButtons();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        static int S(int v) { return Theme.S(v); }

        RoundButton Btn(string text, ButtonKind kind, string tip, Action click)
        {
            var b = new RoundButton(text, kind);
            b.Click += (s, e) => click();
            tips.SetToolTip(b, tip);
            return b;
        }

        void LayoutToolbar()
        {
            int w = toolbar.ClientSize.Width, gap = S(6), groupGap = S(16);
            var bs = btnScan.GetPreferredSize(Size.Empty);
            int h = Math.Max(bs.Height, pathBox.PreferredHeight);

            int right = w;
            if (btnAdmin.Visible)
            {
                var a = btnAdmin.GetPreferredSize(Size.Empty);
                btnAdmin.Bounds = new Rectangle(w - a.Width, 0, a.Width, h);
                right = btnAdmin.Left - groupGap;
            }
            var sb = btnScan.GetPreferredSize(Size.Empty);
            btnScan.Bounds = new Rectangle(right - sb.Width, 0, sb.Width, h);
            var bb = btnBrowse.GetPreferredSize(Size.Empty);
            btnBrowse.Bounds = new Rectangle(btnScan.Left - gap - bb.Width, 0, bb.Width, h);
            pathLabel.Location = new Point(0, (h - pathLabel.Height) / 2);
            int px = pathLabel.Right + gap;
            pathBox.Bounds = new Rectangle(px, 0, Math.Max(S(120), btnBrowse.Left - gap - px), h);

            int x = 0, y = h + S(8);
            foreach (var b in new[] { btnUp, btnRescan, btnExplorer, btnDelete })
            {
                if (b == btnDelete) x += groupGap - gap;
                var p = b.GetPreferredSize(Size.Empty);
                b.Bounds = new Rectangle(x, y, p.Width, h);
                x += p.Width + gap;
            }
            int total = y + h + S(10);
            if (toolbar.Height != total) toolbar.Height = total;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (pathBox.Box.Focused) return;
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { RescanSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && lastView == tree && tree.SelectedNode != null && tree.SelectedNode.Tag is ScanRoot)
            { ScanPath(((ScanRoot)tree.SelectedNode.Tag).Path); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && lastView != tree) { OpenSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.C && e.Control) { CopyPath(); e.Handled = true; }
        }

        // ---------------------------------------------------------------- scanning

        static Task RunThread(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();
            var t = new Thread(() =>
            {
                try { action(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, 16 * 1024 * 1024) { IsBackground = true };
            t.Start();
            return tcs.Task;
        }

        string NormalizePath(string path)
        {
            path = path.Trim().Trim('"');
            if (path.Length == 2 && path[1] == ':') path += "\\";
            path = Path.GetFullPath(path);
            if (path.Length > 3 && path.EndsWith("\\") && !path.StartsWith(@"\\")) path = path.TrimEnd('\\');
            return path;
        }

        void ScanPath(string path)
        {
            if (busy || scanning) return;
            pathBox.Text = path;
            StartScan();
        }

        /// <summary>Scans in the background; the window stays usable and the scan bar shows progress.</summary>
        async void StartScan()
        {
            if (busy || scanning) return;
            string path;
            try { path = NormalizePath(pathBox.Text); }
            catch (Exception ex) { MessageBox.Show(this, "Invalid path: " + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, "Folder not found:\n" + path, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            pathBox.Text = path;
            var r = FindRoot(path);
            bool isNew = r == null;
            if (isNew)
            {
                r = new ScanRoot { Path = path };
                RefreshCapacity(r);
                roots.Add(r);
                SortRoots();
            }
            var s = new Scanner(TopCount);
            scanner = s;
            scanRoot = r;
            r.Scanning = true;
            var oldNode = r.Node;
            SetScanning(true, "Scanning " + r.Path + "...");
            // Show the root being scanned unless the user is already looking at something else that is scanned.
            RefreshTree(current);
            if (current == null || current.Top == oldNode) { if (oldNode == null) SelectRoot(r); }

            var sw = Stopwatch.StartNew();
            DirNode result = null;
            Exception error = null;
            try { await RunThread(() => result = s.Run(path)); }
            catch (Exception ex) { error = ex; }
            sw.Stop();
            r.Scanning = false;
            scanner = null;
            scanRoot = null;
            SetScanning(false, null);
            if (error != null)
            {
                if (isNew) roots.Remove(r);
                RefreshTree(current);
                MessageBox.Show(this, "Scan failed: " + error.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            r.Node = result;
            r.Top = s.TopFiles;
            RefreshCapacity(r);
            // Keep the user's place: map a selection inside the old tree onto the new one; otherwise stay put,
            // unless nothing scanned was selected (e.g. at startup), then open the new result.
            DirNode target;
            if (current != null && oldNode != null && current.Top == oldNode) target = FindByPath(result, current.FullPath) ?? result;
            else if (active != null && active != r) target = current;   // browsing another drive: stay there
            else target = result;
            if (active == r) { topFiles = r.Top; current = null; }
            RefreshTree(target);
            if (active == r) FillLargest();
            UpdateDriveLabel();
            statusLabel.Text = string.Format("{0}: {1} in {2:N0} files and {3:N0} folders - scanned in {4:0.00} s{5}",
                result.Name, Fmt.Size(result.Size), result.Files, result.Dirs, sw.Elapsed.TotalSeconds,
                s.Cancelled ? "  (stopped - partial results)" : "");
        }

        /// <summary>Deepest scanned folder under root that matches fullPath (root itself if nothing deeper matches).</summary>
        static DirNode FindByPath(DirNode root, string fullPath)
        {
            string rp = root.FullPath.TrimEnd('\\');
            if (!fullPath.StartsWith(rp, StringComparison.OrdinalIgnoreCase)) return null;
            var node = root;
            foreach (var part in fullPath.Substring(rp.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                DirNode next = null;
                if (node.Children != null)
                    foreach (var c in node.Children)
                        if (string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
                if (next == null) break;
                node = next;
            }
            return node;
        }

        // ---------------------------------------------------------------- drives and scan roots

        static bool SamePath(string a, string b)
        {
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        ScanRoot FindRoot(string path) { return roots.FirstOrDefault(r => SamePath(r.Path, path)); }

        ScanRoot RootOf(DirNode top) { return top == null ? null : roots.FirstOrDefault(r => r.Node == top); }

        void SortRoots()
        {
            roots.Sort((a, b) =>
            {
                if (a.IsSystem != b.IsSystem) return a.IsSystem ? -1 : 1;
                if (a.IsDrive != b.IsDrive) return a.IsDrive ? -1 : 1;
                return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
        }

        static void RefreshCapacity(ScanRoot r)
        {
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(r.Path));
                if (!d.IsReady) return;
                r.Total = d.TotalSize;
                r.Free = d.TotalFreeSpace;
            }
            catch (Exception) { }
        }

        /// <summary>Adds ready drives, drops drives that went away (e.g. an unplugged USB disk).</summary>
        void LoadDrives()
        {
            string sys = Path.GetPathRoot(Environment.SystemDirectory);
            var ready = new List<DriveInfo>();
            foreach (var d in DriveInfo.GetDrives())
            {
                // Mapped network drives are not listed: checking a disconnected share can block for seconds.
                // A network path can still be typed into the path box and scanned.
                if (d.DriveType == DriveType.Network) continue;
                try { if (d.IsReady) ready.Add(d); } catch (Exception) { }
            }
            roots.RemoveAll(r => r.IsDrive && !ready.Any(d => SamePath(d.Name, r.Path)));
            foreach (var d in ready)
            {
                var r = FindRoot(d.Name);
                if (r == null) { r = new ScanRoot { Path = d.Name }; roots.Add(r); }
                r.IsDrive = true;
                r.IsSystem = SamePath(d.Name, sys);
                string label = "";
                try { label = d.VolumeLabel; } catch (Exception) { }
                string kind = d.DriveType == DriveType.Removable ? "removable" : d.DriveType == DriveType.Network ? "network"
                    : d.DriveType == DriveType.CDRom ? "optical" : "";
                if (string.IsNullOrEmpty(label)) label = d.DriveType == DriveType.Fixed ? "Local disk" : "";
                r.Label = label + (kind.Length > 0 ? (label.Length > 0 ? " - " : "") + kind : "");
                RefreshCapacity(r);
            }
            if (active != null && !roots.Contains(active)) { active = null; current = null; }
            SortRoots();
            pathBox.Choices.Clear();
            foreach (var r in roots) if (r.IsDrive) pathBox.Choices.Add(r.Path);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_DEVICECHANGE: DBT_DEVICEARRIVAL / DBT_DEVICEREMOVECOMPLETE -> refresh the drive list.
            if (m.Msg == 0x219 && (m.WParam == (IntPtr)0x8000 || m.WParam == (IntPtr)0x8004))
            {
                if (busy || scanning) drivesChanged = true;
                else BeginInvoke(new Action(RefreshDrives));
            }
        }

        void RefreshDrives()
        {
            if (busy || scanning) { drivesChanged = true; return; }
            drivesChanged = false;
            LoadDrives();
            RefreshTree(current);
        }

        void ShowUnscanned(ScanRoot r)
        {
            current = null;
            active = r;
            topFiles = r.Top;
            RefreshCapacity(r);
            if (!busy) pathBox.Text = r.Path;
            contents.Items.Clear();
            contents.EmptyText = r.Scanning
                ? "Scanning " + r.DisplayName + "...\nThe results appear here when the scan finishes. You can browse other drives meanwhile."
                : r.DisplayName + " has not been scanned yet.\nPress Scan, or double-click the drive, to analyze it.";
            contents.Invalidate();
            FillLargest();
            tabs.SetTitle(0, "Folder contents: " + r.Path + (r.Scanning ? "  (scanning...)" : "  (not scanned)"));
            UpdateChartUnscanned(r);
            UpdateDriveLabel();
            UpdateButtons();
        }

        // ---------------------------------------------------------------- pie chart

        void UpdateChart(DirNode d)
        {
            var r = RootOf(d.Top);
            if (r != null) RefreshCapacity(r);
            var slices = new List<ChartSlice>();
            long dirsTotal = 0, shown = 0;
            if (d.Children != null)
                foreach (var c in d.Children)
                {
                    dirsTotal += c.Size;
                    if (slices.Count < 6 && c.Size > 0)
                    {
                        slices.Add(new ChartSlice { Label = c.Name, Size = c.Size, Color = Theme.CHART[slices.Count % Theme.CHART.Length], Tag = c });
                        shown += c.Size;
                    }
                }
            if (dirsTotal - shown > 0) slices.Add(new ChartSlice { Label = "Other folders", Size = dirsTotal - shown, Color = Theme.CHART_OTHER });
            if (d.LocalSize > 0) slices.Add(new ChartSlice { Label = "Files in this folder", Size = d.LocalSize, Color = Theme.CHART_FILES });

            bool driveRoot = d.Parent == null && r != null && r.IsDrive && r.Total > 0;
            string title = d.Parent == null && r != null ? r.DisplayName : d.Name, sub = d.FullPath;
            if (driveRoot)
            {
                long used = r.Total - r.Free;
                if (used - d.Size > 0)
                    slices.Add(new ChartSlice { Label = "System / not readable", Size = used - d.Size, Color = Theme.PILL });
                slices.Add(new ChartSlice { Label = "Free space", Size = r.Free, Color = Theme.CHART_FREE });
                sub = string.Format("{0} used of {1}", Fmt.Size(used), Fmt.Size(r.Total));
            }
            if (r != null && r.Total > 0)
                chart.SetData(title, sub, slices,
                    string.Format("{0}  {1} here  -  {2} free of {3}", Path.GetPathRoot(r.Path), Fmt.Size(d.Size), Fmt.Size(r.Free), Fmt.Size(r.Total)),
                    r.Total, r.Free, d.Size);
            else
                chart.SetData(title, sub, slices, "", 0, 0, 0);
        }

        void UpdateChartUnscanned(ScanRoot r)
        {
            if (r.Total <= 0) { chart.SetData(r.DisplayName, "Not scanned", new List<ChartSlice>(), "", 0, 0, 0); return; }
            long used = r.Total - r.Free;
            var slices = new List<ChartSlice>
            {
                new ChartSlice { Label = "Used (not scanned yet)", Size = used, Color = Theme.CHART[0] },
                new ChartSlice { Label = "Free space", Size = r.Free, Color = Theme.CHART_FREE }
            };
            chart.SetData(r.DisplayName, string.Format("{0} used of {1}  -  {2}", Fmt.Size(used), Fmt.Size(r.Total), r.Scanning ? "scanning..." : "not scanned"), slices,
                "", 0, 0, 0);
        }

        /// <summary>Blocking work (permanent delete): full-window overlay.</summary>
        void SetBusy(bool value)
        {
            busy = value;
            if (value)
            {
                overlay.ShowBusy("Starting...", false);
                uiTimer.Start();
            }
            else
            {
                if (!scanning) uiTimer.Stop();
                overlay.HideBusy();
            }
            split.Enabled = !value;
            UpdateButtons();
            if (!value && drivesChanged) BeginInvoke(new Action(RefreshDrives));
        }

        /// <summary>Background scan: the scan bar shows progress; browsing stays possible, changes to the disk do not.</summary>
        void SetScanning(bool value, string text)
        {
            scanning = value;
            if (value)
            {
                scanBar.Start(text);
                uiTimer.Start();
            }
            else
            {
                if (!busy) uiTimer.Stop();
                scanBar.Stop();
            }
            UpdateButtons();
            if (!value && drivesChanged) BeginInvoke(new Action(RefreshDrives));
        }

        void UpdateButtons()
        {
            bool idle = !busy && !scanning;
            btnScan.Enabled = idle;
            btnBrowse.Enabled = idle;
            pathBox.Enabled = !busy;
            btnUp.Enabled = !busy && current != null && current.Parent != null;
            btnRescan.Enabled = idle && root != null;
            btnExplorer.Enabled = !busy && root != null;
            btnDelete.Enabled = idle && root != null;
        }

        void UpdateProgress()
        {
            var s = scanner;
            var j = deleteJob;
            if (s != null)
            {
                var r = scanRoot;
                long bytes = Interlocked.Read(ref s.BytesSeen);
                string where = r != null ? r.Path : "";
                string detail = string.Format("{0:N0} files, {1:N0} folders  --  {2}",
                    Interlocked.Read(ref s.FilesSeen), Interlocked.Read(ref s.DirsSeen), Fmt.Shorten(s.Current, 90));
                // A whole drive has a known total (its used space), so the bar can show real progress.
                bool driveRoot = r != null && r.IsDrive && r.Total > 0 && SamePath(r.Path, s.RootPath ?? "");
                long used = driveRoot ? r.Total - r.Free : 0;
                if (s.Cancelled)
                    scanBar.SetProgress("Cancelling... (finishing current folder)", detail, -1);
                else if (used > 0)
                {
                    double frac = Math.Min(0.99, (double)bytes / used);
                    scanBar.SetProgress(string.Format("Scanning {0}: {1} of {2} done ({3:0} %)", where, Fmt.Size(bytes), Fmt.Size(used), frac * 100),
                        detail, frac);
                }
                else
                    scanBar.SetProgress(string.Format("Scanning {0}: {1} so far", Fmt.Shorten(s.RootPath ?? where, 60), Fmt.Size(bytes)), detail, -1);
            }
            if (j != null)
                overlay.SetStatus(string.Format("Deleting: {0:N0} files removed", Interlocked.Read(ref j.FilesDeleted)), Fmt.Shorten(j.Current, 80));
        }

        void UpdateDriveLabel()
        {
            driveLabel.Text = "";
            if (active == null) return;
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(active.Path));
                driveLabel.Text = string.Format("{0}  {1} free of {2} ({3:0}% free)", d.Name, Fmt.Size(d.AvailableFreeSpace),
                    Fmt.Size(d.TotalSize), 100.0 * d.AvailableFreeSpace / Math.Max(1, d.TotalSize));
            }
            catch (Exception) { }
        }

        void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose a folder to analyze", ShowNewFolderButton = false })
            {
                try { dlg.SelectedPath = NormalizePath(pathBox.Text); } catch (Exception) { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                pathBox.Text = dlg.SelectedPath;
            }
            StartScan();
        }

        void RestartAsAdmin()
        {
            string arg = "";
            string p = active != null ? active.Path : pathBox.Text.Trim();
            if (p.Length > 0) arg = "\"" + p.TrimEnd('\\') + "\"";
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, arg) { Verb = "runas", UseShellExecute = true });
                Close();
            }
            catch (Win32Exception) { }
        }

        // ---------------------------------------------------------------- tree and lists

        static string TreeText(DirNode d)
        {
            return d.Denied ? d.Name + "  [access denied]" : d.Name;
        }

        string[] TreeColumns(TreeNode tn)
        {
            var r = tn.Tag as ScanRoot;
            if (r != null)
                return r.Total > 0 ? new[] { Fmt.Size(r.Total - r.Free), string.Format("{0:0.0} %", 100.0 * (r.Total - r.Free) / r.Total) } : null;
            var d = tn.Tag as DirNode;
            if (d == null) return null;
            double pct = TreeBar(tn);
            return new[] { Fmt.Size(d.Size), pct >= 0 ? string.Format("{0:0.0} %", pct) : "" };
        }

        /// <summary>Percentage shown as a bar: share of the parent folder; for a scanned drive, share of the drive capacity.</summary>
        double TreeBar(TreeNode tn)
        {
            var r = tn.Tag as ScanRoot;
            if (r != null) return r.Total > 0 ? 100.0 * (r.Total - r.Free) / r.Total : -1;
            var d = tn.Tag as DirNode;
            if (d == null) return -1;
            if (d.Parent != null) return d.Parent.Size > 0 ? 100.0 * d.Size / d.Parent.Size : 0;
            var top = RootOf(d);
            return top != null && top.IsDrive && top.Total > 0 ? 100.0 * d.Size / top.Total : -1;
        }

        static TreeNode MakeTreeNode(DirNode d)
        {
            var tn = new TreeNode(TreeText(d)) { Tag = d, Name = d.Name };
            if (d.Denied) tn.ForeColor = Theme.MUTED;
            if (d.Children != null && d.Children.Count > 0) tn.Nodes.Add(new TreeNode("..."));
            return tn;
        }

        static TreeNode MakeRootNode(ScanRoot r)
        {
            if (r.Node != null)
            {
                var tn = MakeTreeNode(r.Node);
                tn.Text = r.DisplayName + (r.Scanning ? "  (rescanning...)" : "");
                return tn;
            }
            return new TreeNode(r.DisplayName + (r.Scanning ? "  (scanning...)" : "  (not scanned)")) { Tag = r, ForeColor = Theme.MUTED };
        }

        static void Populate(TreeNode tn)
        {
            if (tn.Nodes.Count != 1 || tn.Nodes[0].Tag != null) return;
            var d = (DirNode)tn.Tag;
            tn.Nodes.Clear();
            if (d.Children != null) tn.Nodes.AddRange(d.Children.Select(MakeTreeNode).ToArray());
        }

        bool IsAttached(DirNode d)
        {
            return d != null && RootOf(d.Top) != null;
        }

        /// <summary>Rebuilds the tree (one top-level node per drive or scanned folder), keeping expanded folders, then selects target.</summary>
        void RefreshTree(DirNode target)
        {
            var expanded = new HashSet<DirNode>();
            CollectExpanded(tree.Nodes, expanded);
            tree.BeginUpdate();
            tree.Nodes.Clear();
            foreach (var r in roots)
            {
                var rn = MakeRootNode(r);
                tree.Nodes.Add(rn);
                if (r.Node == null) continue;
                bool targetHere = target != null && target.Top == r.Node;
                if (targetHere || expanded.Contains(r.Node))
                {
                    rn.Expand();
                    foreach (TreeNode c in rn.Nodes) ReExpand(c, expanded);
                }
            }
            tree.EndUpdate();
            if (target != null && IsAttached(target)) SelectDir(target);
            else if (active != null && roots.Contains(active)) SelectRoot(active);
            else if (roots.Count > 0) SelectRoot(roots[0]);
        }

        void SelectRoot(ScanRoot r)
        {
            if (r.Node != null) { SelectDir(r.Node); return; }
            foreach (TreeNode tn in tree.Nodes)
                if (tn.Tag == r)
                {
                    if (tree.SelectedNode == tn) ShowUnscanned(r); else tree.SelectedNode = tn;
                    return;
                }
        }

        static void CollectExpanded(TreeNodeCollection nodes, HashSet<DirNode> set)
        {
            foreach (TreeNode n in nodes)
            {
                if (!n.IsExpanded) continue;
                var d = n.Tag as DirNode;
                if (d != null) set.Add(d);
                CollectExpanded(n.Nodes, set);
            }
        }

        static void ReExpand(TreeNode tn, HashSet<DirNode> set)
        {
            if (!set.Contains(tn.Tag as DirNode)) return;
            tn.Expand();
            foreach (TreeNode c in tn.Nodes) ReExpand(c, set);
        }

        void SelectDir(DirNode target)
        {
            if (tree.Nodes.Count == 0 || target == null) return;
            var chain = new List<DirNode>();
            for (var n = target; n != null; n = n.Parent) chain.Insert(0, n);
            TreeNode cur = null;
            foreach (TreeNode tn in tree.Nodes) if (tn.Tag == chain[0]) { cur = tn; break; }
            if (cur == null) return;
            for (int i = 1; i < chain.Count; i++)
            {
                cur.Expand();
                TreeNode next = null;
                foreach (TreeNode c in cur.Nodes) if (c.Tag == chain[i]) { next = c; break; }
                if (next == null) break;
                cur = next;
            }
            if (tree.SelectedNode == cur) ShowContents((DirNode)cur.Tag);
            else tree.SelectedNode = cur;
            cur.EnsureVisible();
        }

        void ShowContents(DirNode d)
        {
            current = d;
            var r = RootOf(d.Top);
            if (r != null && r != active)
            {
                active = r;
                topFiles = r.Top;
                FillLargest();
                UpdateDriveLabel();
                if (!busy) pathBox.Text = r.Path;
            }
            contents.EmptyText = null;
            UpdateChart(d);
            var items = new List<ItemInfo>();
            if (d.Children != null) foreach (var c in d.Children) items.Add(ItemInfo.ForDir(c, d.Size));
            var files = Scanner.ListFiles(d);
            files.Sort(FileEntry.BySizeDesc);
            for (int i = 0; i < files.Count && i < MaxFilesShown; i++) items.Add(ItemInfo.ForFile(files[i], d.Size));
            if (files.Count > MaxFilesShown)
            {
                long rest = 0;
                for (int i = MaxFilesShown; i < files.Count; i++) rest += files[i].Size;
                items.Add(new ItemInfo
                {
                    IsSummary = true, Name = string.Format("[{0:N0} smaller files not listed]", files.Count - MaxFilesShown),
                    Size = rest, Files = files.Count - MaxFilesShown, Percent = d.Size > 0 ? 100.0 * rest / d.Size : 0
                });
            }
            items.Sort(contents.Sorter.Compare);

            contents.BeginUpdate();
            contents.Items.Clear();
            contents.Items.AddRange(items.Select(it =>
            {
                var lvi = new ListViewItem(new[]
                {
                    it.Name, Fmt.Size(it.Size), it.Percent.ToString("0.0") + " %",
                    it.Files.ToString("N0"), it.IsDir ? it.Dirs.ToString("N0") : "", Fmt.Date(it.Modified)
                }) { Tag = it };
                if (it.IsSummary) lvi.ForeColor = Theme.MUTED;
                else if (it.IsDir && it.Dir.Denied) lvi.ForeColor = Theme.MUTED;
                return lvi;
            }).ToArray());
            contents.EndUpdate();

            tabs.SetTitle(0, "Folder contents: " + Fmt.Shorten(d.Name, 40) + "  (" + Fmt.Size(d.Size) + ")");
            UpdateButtons();
        }

        void FillLargest()
        {
            var items = topFiles.Select(f =>
            {
                var it = ItemInfo.ForFile(f, 0);
                it.Folder = f.Dir.FullPath;
                return it;
            }).ToList();
            items.Sort(largest.Sorter.Compare);
            largest.BeginUpdate();
            largest.Items.Clear();
            largest.Items.AddRange(items.Select(it => new ListViewItem(new[] { it.Name, Fmt.Size(it.Size), it.Folder, Fmt.Date(it.Modified) })
            {
                Tag = it
            }).ToArray());
            largest.EndUpdate();
        }

        List<ItemInfo> GetSelection()
        {
            var list = new List<ItemInfo>();
            if (root == null) return list;
            if (lastView == tree || lastView == null)
            {
                var d = tree.SelectedNode != null ? tree.SelectedNode.Tag as DirNode : null;
                if (d != null) list.Add(ItemInfo.ForDir(d, d.Parent != null ? d.Parent.Size : d.Size));
            }
            else
            {
                var lv = (ListView)lastView;
                foreach (ListViewItem lvi in lv.SelectedItems)
                {
                    var it = lvi.Tag as ItemInfo;
                    if (it != null && !it.IsSummary) list.Add(it);
                }
            }
            return list;
        }

        void OpenSelected()
        {
            var sel = GetSelection();
            if (sel.Count != 1 || busy) return;
            var it = sel[0];
            if (it.IsDir) { SelectDir(it.Dir); return; }
            if (lastView == largest && IsAttached(it.File.Dir))
            {
                SelectDir(it.File.Dir);
                tabs.SelectedIndex = 0;
                foreach (ListViewItem lvi in contents.Items)
                {
                    var c = (ItemInfo)lvi.Tag;
                    if (!c.IsDir && string.Equals(c.Name, it.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        contents.SelectedItems.Clear();
                        lvi.Selected = true;
                        lvi.EnsureVisible();
                        contents.Focus();
                        break;
                    }
                }
                return;
            }
            ShowInExplorer();
        }

        void GoUp()
        {
            if (busy || current == null || current.Parent == null) return;
            SelectDir(current.Parent);
        }

        void ShowInExplorer()
        {
            var sel = GetSelection();
            string path = sel.Count > 0 ? sel[0].FullPath : current != null ? current.FullPath : null;
            if (path == null) return;
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch (Exception) { }
        }

        void CopyPath()
        {
            var sel = GetSelection();
            if (sel.Count == 0) return;
            try { Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(i => i.FullPath))); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- model updates

        static void ResortUp(DirNode d)
        {
            for (var a = d; a != null; a = a.Parent)
                if (a.Children != null) a.Children.Sort(DirNode.BySizeDesc);
        }

        /// <summary>Largest-files list of the root that holds d (the active list if d is detached).</summary>
        List<FileEntry> TopOf(DirNode d)
        {
            var r = RootOf(d.Top);
            return r != null ? r.Top : topFiles;
        }

        void RemoveNode(DirNode dn)
        {
            TopOf(dn).RemoveAll(f => f.Dir.IsUnder(dn));
            var p = dn.Parent;
            if (p == null) return;
            p.Children.Remove(dn);
            for (var a = p; a != null; a = a.Parent) { a.Size -= dn.Size; a.Files -= dn.Files; a.Dirs -= dn.Dirs + 1; }
            ResortUp(p);
            dn.Parent = null;
        }

        void ReplaceNode(DirNode old, DirNode fresh, List<FileEntry> freshTop)
        {
            var list = TopOf(old);
            list.RemoveAll(f => f.Dir.IsUnder(old));
            var p = old.Parent;
            if (p == null) { var r = RootOf(old); if (r != null) r.Node = fresh; }
            else
            {
                fresh.Name = old.Name;
                fresh.Parent = p;
                p.Children[p.Children.IndexOf(old)] = fresh;
                long dS = fresh.Size - old.Size, dF = fresh.Files - old.Files, dD = fresh.Dirs - old.Dirs;
                for (var a = p; a != null; a = a.Parent) { a.Size += dS; a.Files += dF; a.Dirs += dD; }
                ResortUp(p);
                old.Parent = null;
            }
            list.AddRange(freshTop);
            list.Sort(FileEntry.BySizeDesc);
            if (list.Count > TopCount) list.RemoveRange(TopCount, list.Count - TopCount);
        }

        void RemoveFile(FileEntry f)
        {
            var d = f.Dir;
            if (!IsAttached(d)) return;
            d.LocalSize -= f.Size;
            d.LocalFiles--;
            for (var a = d; a != null; a = a.Parent) { a.Size -= f.Size; a.Files--; }
            ResortUp(d.Parent);
            TopOf(d).RemoveAll(t => t.Dir == d && string.Equals(t.Name, f.Name, StringComparison.OrdinalIgnoreCase));
        }

        async void RescanSelected()
        {
            if (busy || scanning || root == null) return;
            var sel = GetSelection();
            DirNode dir = sel.Count == 1 && sel[0].IsDir ? sel[0].Dir : current;
            if (dir == null) return;
            if (dir.Parent == null) { var rr = RootOf(dir); ScanPath(rr != null ? rr.Path : dir.FullPath); return; }

            string path = dir.FullPath;
            var owner = RootOf(dir.Top);
            var s = new Scanner(TopCount);
            scanner = s;
            scanRoot = owner;
            SetScanning(true, "Rescanning " + Fmt.Shorten(path, 80) + "...");
            DirNode fresh = null;
            var sw = Stopwatch.StartNew();
            try { await RunThread(() => fresh = s.Run(path)); }
            finally { scanner = null; scanRoot = null; SetScanning(false, null); }

            // Stay where the user is; if they are inside the rescanned folder, follow them into the new copy.
            bool inside = current != null && current.IsUnder(dir);
            string here = inside ? current.FullPath : null;
            DirNode target = current;
            if (!Native.Exists(path)) { if (inside) target = dir.Parent; RemoveNode(dir); }
            else
            {
                ReplaceNode(dir, fresh, s.TopFiles);
                if (inside) target = FindByPath(fresh, here) ?? fresh;
            }
            if (target == null || !IsAttached(target)) target = dir.Parent;
            current = null;
            RefreshTree(target);
            FillLargest();
            UpdateDriveLabel();
            statusLabel.Text = string.Format("Rescanned {0}: {1} in {2:0.00} s", path, Fmt.Size(fresh.Size), sw.Elapsed.TotalSeconds);
        }

        async void DeleteSelected()
        {
            if (busy || scanning || root == null) return;
            var sel = GetSelection();
            if (sel.Count == 0) return;
            if (sel.Any(i => i.IsDir && i.Dir.Parent == null))
            {
                MessageBox.Show(this, "The scanned root folder itself cannot be deleted here.\nSelect items inside it instead.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            long total = sel.Sum(i => i.Size);
            string list = string.Join("\n", sel.Take(12).Select(i => "    " + Fmt.Shorten(i.FullPath, 90) + "   (" + Fmt.Size(i.Size) + ")"));
            if (sel.Count > 12) list += string.Format("\n    ... and {0:N0} more", sel.Count - 12);
            string msg = string.Format("Permanently delete {0} item{1} ({2})?\n\n{3}\n\nThis bypasses the Recycle Bin and CANNOT be undone.",
                sel.Count, sel.Count == 1 ? "" : "s", Fmt.Size(total), list);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (sel.Any(i => IsWithin(i.FullPath, win) || IsWithin(i.FullPath, pf) || IsWithin(i.FullPath, pf86)))
                msg += "\n\nWARNING: the selection includes Windows or Program Files content. Deleting it can break Windows or installed programs.";
            if (MessageBox.Show(this, msg, AppName + " - confirm permanent delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            DirNode target = current;
            foreach (var it in sel)
                if (it.IsDir && current != null && current.IsUnder(it.Dir)) target = it.Dir.Parent;

            long before = root.Size;
            var job = new DeleteJob();
            deleteJob = job;
            SetBusy(true);
            var paths = sel.Select(i => i.FullPath).ToList();
            try
            {
                await RunThread(() =>
                {
                    for (int k = 0; k < sel.Count; k++)
                        if (sel[k].IsDir) job.DeleteDirectory(paths[k]); else job.DeleteFile(paths[k]);
                });
            }
            finally { deleteJob = null; SetBusy(false); }

            // Files first: removing folders detaches nodes, and file updates walk the parent chain.
            for (int k = 0; k < sel.Count; k++)
                if (!sel[k].IsDir && !Native.Exists(paths[k])) RemoveFile(sel[k].File);

            for (int k = 0; k < sel.Count; k++)
            {
                if (!sel[k].IsDir || !IsAttached(sel[k].Dir)) continue;
                var dn = sel[k].Dir;
                if (!Native.Exists(paths[k])) { RemoveNode(dn); continue; }
                var s = new Scanner(TopCount);
                DirNode fresh = null;
                string p = paths[k];
                await RunThread(() => fresh = s.Run(p));
                ReplaceNode(dn, fresh, s.TopFiles);
                if (target == dn) target = fresh;
            }

            RefreshTree(IsAttached(target) ? target : root);
            FillLargest();
            UpdateDriveLabel();
            if (lastView != null && lastView.Visible) lastView.Focus();
            statusLabel.Text = string.Format("Freed {0}. {1:N0} files deleted{2}.", Fmt.Size(before - root.Size), job.FilesDeleted,
                job.Errors.Count > 0 ? string.Format(", {0:N0} items could not be deleted", job.Errors.Count) : "");

            if (job.Errors.Count > 0)
            {
                string err = string.Join("\n", job.Errors.Take(20).Select(e => Fmt.Shorten(e, 160)));
                if (job.Errors.Count > 20) err += string.Format("\n... and {0:N0} more", job.Errors.Count - 20);
                MessageBox.Show(this, "Some items could not be deleted (in use, or access denied" + (isAdmin ? "" : " - try Run as administrator") + "):\n\n" + err,
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static bool IsWithin(string path, string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            folder = folder.TrimEnd('\\');
            return path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
