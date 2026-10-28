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
        public PrevNode Previous;                // the scan before the current one (for the Changes view)
        public DateTime PreviousTime;

        public string DisplayName { get { return IsDrive && Label.Length > 0 ? Path + "  " + Label : Path; } }
    }

    internal sealed partial class MainForm : Form
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
        UpdateManager updates;
        /// <summary>Scanned tree of the root that holds the current selection (null when that root is not scanned).</summary>
        DirNode root { get { return active != null ? active.Node : null; } }
        Scanner scanner;
        DeleteJob deleteJob;
        bool busy;
        Control lastView;

        readonly PathInput pathBox;
        readonly Label pathLabel;
        readonly RoundButton btnBrowse, btnScan, btnUp, btnRescan, btnExplorer, btnAddList, btnDelete, btnAdmin, btnExport;
        readonly TitleBar titleBar;
        readonly Segmented measure;
        readonly Panel toolbar;
        readonly SplitContainer split, split2;
        readonly PieChart chart;
        readonly DarkTreeView tree;
        readonly SizeListView contents;
        readonly Label statusLabel, driveLabel;
        readonly BusyOverlay overlay;
        readonly System.Windows.Forms.Timer uiTimer;
        readonly ContextMenuStrip menu;
        readonly ToolTip tips = new ToolTip();
        ToolStripMenuItem miOpen, miExplorer, miCopy, miRescan, miAddList, miRecycle, miDelete;

        public MainForm(string initialPath)
        {
            using (var g = CreateGraphics()) Theme.Scale = g.DpiX / 96f;
            isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

            Text = AppName + (isAdmin ? " (Administrator)" : "");
            Font = Theme.FONT;
            BackColor = Theme.BG;
            ForeColor = Theme.FG;
            AutoScaleMode = AutoScaleMode.None;
            // 1280x760 by default, at most 85 % of the working area so it always fits with room around it.
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(S(1280), wa.Width * 85 / 100), Math.Min(S(760), wa.Height * 85 / 100));
            MinimumSize = new Size(Math.Min(S(900), wa.Width), Math.Min(S(520), wa.Height));
            // Placed explicitly in the working area (CenterScreen put the window partly below the taskbar).
            StartPosition = FormStartPosition.Manual;
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
            KeyPreview = true;
            AllowDrop = true;
            Padding = new Padding(S(15), S(36) + S(4), S(15), 0);   // top: the custom title bar (placed at 0,0 below)
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
            btnAddList = Btn("Add to delete list", ButtonKind.Secondary, "Collect the selected items in the delete list; review them there, then delete them together (Insert)", AddSelectionToBasket);
            btnDelete = Btn("Delete permanently", ButtonKind.Warn, "Permanently delete the selected items, bypassing the Recycle Bin (Del)", DeleteSelected);
            btnExport = Btn("Export...", ButtonKind.Secondary, "Save the scan as a CSV file or an HTML report", ExportScan);


            measure = new Segmented("Size", "Size on disk");
            measure.SelectedIndexChanged += (s, e) => SetMeasure(measure.SelectedIndex == 1);
            tips.SetToolTip(measure, "Size: file lengths, as Explorer shows them. Size on disk: the space really used (compression, clusters, online-only cloud files)");

            toolbar = new Panel { Dock = DockStyle.Top, BackColor = Theme.BG };
            toolbar.Controls.AddRange(new Control[] { pathLabel, pathBox, btnBrowse, btnScan, btnAdmin, btnUp, btnRescan, btnExplorer, btnAddList, btnDelete, btnExport, measure });
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
            miAddList = new ToolStripMenuItem("Add to delete list", null, (s, e) => AddSelectionToBasket()) { ShortcutKeyDisplayString = "Ins" };
            miRecycle = new ToolStripMenuItem("Move to Recycle Bin...", null, (s, e) => RecycleSelected());
            miDelete = new ToolStripMenuItem("Delete permanently...", null, (s, e) => DeleteSelected()) { ShortcutKeyDisplayString = "Del" };
            menu.Items.AddRange(new ToolStripItem[] { miOpen, miExplorer, miCopy, miRescan, new ToolStripSeparator(), miAddList, miRecycle, miDelete });
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
                bool deletable = any && !scanning && sel.All(i => i.CanDelete);
                miAddList.Enabled = any && sel.All(i => i.CanDelete);
                miAddList.Visible = lastView != (pBasket != null ? pBasket.List : null);
                miRecycle.Enabled = deletable;
                miDelete.Enabled = deletable;
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

            // Folder contents (the first view); the other views are built in Views.cs.
            contents = new SizeListView { ContextMenuStrip = menu };
            contents.SetColumns(new[] { "Name", "Size", "% of folder", "Change", "Files", "Folders", "Modified" },
                new[] { "name", "size", "size", "change", "files", "dirs", "mod" }, new[] { 300, 90, 170, 90, 80, 70, 140 });
            contents.BarColumn = 2;
            var listPanel = new RoundedPanel(12, 8) { Dock = DockStyle.Fill, TrackFocus = true };
            BuildViews(listPanel);
            split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = S(10), BackColor = Theme.BG };
            split.Panel1.BackColor = Theme.BG;
            split.Panel2.BackColor = Theme.BG;
            split.Panel1.Controls.Add(treePanel);

            // Pie chart pane (right). It is drawn from sizes already in memory, never from the disk.
            chart = new PieChart { Dock = DockStyle.Fill };
            chart.SliceClicked += sl => { if (sl.Dir != null && !busy) SelectDir(sl.Dir); };
            chart.HoverChanged += i => ActivePage.List.SetHighlight(i >= 0 && i < chart.Slices.Count ? chart.Slices[i].Rows : null);
            var chartPanel = new RoundedPanel(12, 8) { Dock = DockStyle.Fill };
            chartPanel.Controls.Add(chart);
            split2 = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = S(10), BackColor = Theme.BG, FixedPanel = FixedPanel.Panel2 };
            split2.Panel1.BackColor = Theme.BG;
            split2.Panel2.BackColor = Theme.BG;
            split2.Panel1.Controls.Add(listPanel);
            split2.Panel2.Controls.Add(chartPanel);
            split.Panel2.Controls.Add(split2);
            split.Panel2.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(6), BackColor = Theme.BG });
            split.Panel2.Controls.Add(viewTabs);

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
            liveLabel = new Label
            {
                Dock = DockStyle.Right, AutoSize = true, Font = Theme.FONT_9, ForeColor = Theme.MUTED, BackColor = Theme.BG,
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(S(12), S(8), S(12), 0)
            };
            status.Controls.Add(statusLabel);
            status.Controls.Add(liveLabel);
            status.Controls.Add(driveLabel);

            // Version in the status line; click it to check for updates by hand.
            var versionLink = new Label
            {
                Text = "v" + Updater.Short(Updater.Current), Dock = DockStyle.Right, AutoSize = true, Font = Theme.FONT_9,
                ForeColor = Theme.MUTED, BackColor = Theme.BG, Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(S(16), S(8), S(12), 0)
            };
            tips.SetToolTip(versionLink, "Check for updates");
            versionLink.MouseEnter += (s, e) => versionLink.ForeColor = Theme.ACCENT;
            versionLink.MouseLeave += (s, e) => versionLink.ForeColor = Theme.MUTED;
            versionLink.Click += (s, e) => updates.Check(true);
            status.Controls.Add(versionLink);
            updates = new UpdateManager(this) { CanRestart = () => !busy };

            overlay = new BusyOverlay();
            overlay.CancelButton.Click += (s, e) =>
            {
                if (scanner == null) return;
                scanner.Cancel();
                overlay.CancelButton.Enabled = false;
            };

            // Docking runs from the last-added control back: status (bottom), toolbar (top), scan bar (top, under those), then the panes fill the rest.
            scanBar = new ScanBar { Dock = DockStyle.Top };
            scanBar.CancelButton.Click += (s, e) =>
            {
                if (scanner == null) return;
                scanner.Cancel();
                scanBar.CancelButton.Enabled = false;
            };
            // Custom title bar (logo, name, menu items, window buttons) replacing the native caption. It spans the full
            // width, so it is placed at (0, 0) and anchored instead of docked (docking would respect the side padding).
            titleBar = new TitleBar();
            titleBar.AddItem("About", ShowAbout);
            titleBar.AddItem("Check for updates", () => updates.Check(true));
            Activated += (s, e) => titleBar.Active = true;
            Deactivate += (s, e) => titleBar.Active = false;
            Resize += (s, e) => titleBar.Invalidate();
            TextChanged += (s, e) => titleBar.Invalidate();

            Controls.Add(split);
            Controls.Add(scanBar);
            Controls.Add(toolbar);
            Controls.Add(status);
            Controls.Add(titleBar);
            titleBar.Bounds = new Rectangle(0, 0, ClientSize.Width, titleBar.Height);
            titleBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(overlay);
            // The overlay covers the client area below the title bar (toolbar and status line too), so it is anchored,
            // not docked; the title bar stays usable (move, minimise, close) while it shows.
            overlay.Bounds = new Rectangle(0, titleBar.Height, ClientSize.Width, ClientSize.Height - titleBar.Height);
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
                // Tree and chart keep their preferred widths but give way on narrow windows, so the list stays usable.
                split.SplitterDistance = Math.Min(S(420), split.Width * 29 / 100);
                split2.SplitterDistance = Math.Max(S(200), split2.Width - Math.Min(S(300), split2.Width * 34 / 100) - split2.SplitterWidth);
                LoadDrives();
                RefreshTree(null);
                // The system drive is analyzed automatically; other drives are listed and scanned on demand.
                if (!string.IsNullOrEmpty(initialPath)) ScanPath(initialPath);
                else ScanPath(Path.GetPathRoot(Environment.SystemDirectory));
                // Look for a newer release in the background; silent unless there is one.
                if (ShotDir == null && SelfTestDir == null)
                {
                    var later = new System.Windows.Forms.Timer { Interval = 3000 };
                    later.Tick += (s2, e2) => { later.Dispose(); updates.Check(false); };
                    later.Start();
                }
            };
            FormClosing += (s, e) => { if (scanner != null) scanner.Cancel(); };
            UpdateButtons();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
            // Recalculate the frame now that WM_NCCALCSIZE removes the caption.
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);   // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        /// <summary>
        /// Removes the native caption but keeps the resize borders: after the default calculation, the client area's top
        /// edge is put back where the window's top is (when maximised, below the part that sticks out of the screen).
        /// </summary>
        bool CustomFrame(ref Message m)
        {
            const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84;
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                int top = System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, 4);
                base.WndProc(ref m);
                if (WindowState == FormWindowState.Maximized)
                    top += GetSystemMetrics(33) + GetSystemMetrics(92);   // SM_CYSIZEFRAME + SM_CXPADDEDBORDER
                System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, 4, top);
                m.Result = IntPtr.Zero;
                return true;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result != 1) return true;                           // not HTCLIENT: borders etc. stay native
                var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
                int edge = GetSystemMetrics(33) + GetSystemMetrics(92);
                if (WindowState != FormWindowState.Maximized && p.Y < edge)
                    m.Result = (IntPtr)(p.X < edge * 2 ? 13 : p.X > ClientSize.Width - edge * 2 ? 14 : 12);   // HTTOPLEFT / HTTOPRIGHT / HTTOP
                else if (titleBar != null && p.Y < titleBar.Height)
                    m.Result = (IntPtr)2;                                      // HTCAPTION: drag, double-click, Snap, system menu
                return true;
            }
            return false;
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
            foreach (var b in new[] { btnUp, btnRescan, btnExplorer, btnAddList, btnDelete })
            {
                if (b == btnDelete) x += groupGap - gap;
                var p = b.GetPreferredSize(Size.Empty);
                b.Bounds = new Rectangle(x, y, p.Width, h);
                x += p.Width + gap;
            }
            var ms = measure.GetPreferredSize(Size.Empty);
            measure.Bounds = new Rectangle(w - ms.Width, y, ms.Width, h);
            var es = btnExport.GetPreferredSize(Size.Empty);
            btnExport.Bounds = new Rectangle(measure.Left - groupGap - es.Width, y, es.Width, h);
            int total = y + h + S(10);
            if (toolbar.Height != total) toolbar.Height = total;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F1) { ShowAbout(); e.Handled = true; return; }
            if (pathBox.Box.Focused) return;
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { RescanSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && lastView == tree && tree.SelectedNode != null && tree.SelectedNode.Tag is ScanRoot)
            { ScanPath(((ScanRoot)tree.SelectedNode.Tag).Path); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && lastView != tree) { OpenSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.C && e.Control) { CopyPath(); e.Handled = true; }
            else if (e.KeyCode == Keys.Insert) { AddSelectionToBasket(); e.Handled = true; }
            else if (e.KeyCode == Keys.F && e.Control) { ShowFilesSearch(); e.Handled = true; }
        }

        void ShowAbout()
        {
            using (var f = new AboutForm(() => updates.Check(true))) f.ShowDialog(this);
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
            var s = new Scanner();
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
            try { await RunThread(() => result = s.Run(path, true)); }
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
            RefreshCapacity(r);
            RememberScan(r, result, !s.Cancelled);
            StartWatching(r);
            // Keep the user's place: map a selection inside the old tree onto the new one; otherwise stay put,
            // unless nothing scanned was selected (e.g. at startup), then open the new result.
            DirNode target;
            if (current != null && oldNode != null && current.Top == oldNode) target = FindByPath(result, current.FullPath) ?? result;
            else if (active != null && active != r) target = current;   // browsing another drive: stay there
            else target = result;
            if (active == r) current = null;
            RefreshTree(target);
            ViewsChanged();
            UpdateDriveLabel();
            statusLabel.Text = string.Format("{0}: {1} in {2:N0} files and {3:N0} folders - scanned in {4:0.00} s, {5}{6}",
                result.Name, Fmt.Size(result.Size), result.Files, result.Dirs, sw.Elapsed.TotalSeconds, s.Method,
                s.Cancelled ? "  (stopped - partial results)" : "");
            if (ShotDir != null && !shotsStarted) { shotsStarted = true; RunShots(); }
            if (SelfTestDir != null && !shotsStarted) { shotsStarted = true; RunSelfTest(); }
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
            if (CustomFrame(ref m)) return;
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
            RefreshCapacity(r);
            if (!busy) pathBox.Text = r.Path;
            contents.ResetHover();
            contents.Items.Clear();
            contents.EmptyText = r.Scanning
                ? "Scanning " + r.DisplayName + "...\nThe results appear here when the scan finishes. You can browse other drives meanwhile."
                : r.DisplayName + " has not been scanned yet.\nPress Scan, or double-click the drive, to analyze it.";
            contents.Invalidate();
            ViewsChanged();
            pContents.Caption.Text = r.DisplayName + (r.Scanning ? "  -  scanning..." : "  -  not scanned");
            if (ActivePage == pContents) UpdateChartUnscanned(r);
            UpdateDriveLabel();
            UpdateButtons();
        }

        // ---------------------------------------------------------------- pie chart

        /// <summary>Folder contents row -> index of the pie slice that contains it.</summary>
        readonly Dictionary<ListViewItem, int> rowSlice = new Dictionary<ListViewItem, int>();
        const int MaxOwnSlices = 12;          // at most this many rows get a slice of their own...
        const double MinOwnShare = 0.015;     // ...and only if they hold at least 1.5 % of the folder

        /// <summary>
        /// Slices for a list exactly as displayed: one slice per row in row order, except that runs of consecutive small
        /// rows are merged into one "smaller items" slice. Rows of a group (duplicates) join their group's slice.
        /// Fills rowSlice so hover links rows and slices in both directions.
        /// </summary>
        List<ChartSlice> BuildSlices(SizeListView lv, long total, string scope)
        {
            rowSlice.Clear();
            var rows = lv.Items.Cast<ListViewItem>().Where(it => it.Tag is ItemInfo).ToList();
            total = Math.Max(1, total);
            var own = new HashSet<ListViewItem>(rows
                .Where(it => ((ItemInfo)it.Tag).GroupOf == null && ((ItemInfo)it.Tag).ChartSize > 0 && ((ItemInfo)it.Tag).ChartSize >= MinOwnShare * total)
                .OrderByDescending(it => ((ItemInfo)it.Tag).ChartSize)
                .Take(MaxOwnSlices));

            var slices = new List<ChartSlice>();
            var runs = new List<ChartSlice>();
            var headSlice = new Dictionary<ItemInfo, int>();
            ChartSlice run = null;
            int colour = 0;
            foreach (var it in rows)
            {
                var info = (ItemInfo)it.Tag;
                int head;
                if (info.GroupOf != null && headSlice.TryGetValue(info.GroupOf, out head))
                {
                    slices[head].Rows.Add(it);
                    rowSlice[it] = head;
                    continue;
                }
                long size = info.ChartSize;
                if (own.Contains(it))
                {
                    run = null;
                    var s = new ChartSlice
                    {
                        Label = info.Name, Size = size, Color = Theme.CHART[colour++ % Theme.CHART.Length],
                        Detail = string.Format("{0:0.0} % {1}", 100.0 * size / total, scope),
                        Dir = info.IsDir && IsAttached(info.Dir) ? info.Dir : null
                    };
                    s.Rows.Add(it);
                    slices.Add(s);
                }
                else
                {
                    if (run == null) { run = new ChartSlice { Color = Theme.CHART_OTHER }; slices.Add(run); runs.Add(run); }
                    run.Rows.Add(it);
                    run.Size += Math.Max(0, size);
                }
                rowSlice[it] = slices.Count - 1;
                headSlice[info] = slices.Count - 1;
            }
            foreach (var s in runs)
            {
                int n = s.Rows.Count(r => ((ItemInfo)r.Tag).GroupOf == null);
                s.Label = n == 1 ? ((ItemInfo)s.Rows[0].Tag).Name : string.Format("{0:N0} smaller items", n);
                s.Detail = string.Format("{0:0.0} % {1}", 100.0 * s.Size / total, scope);
            }
            return slices;
        }

        /// <summary>Folder contents chart; a drive root also gets "System / not readable" and "Free space" at the end.</summary>
        void UpdateChart(DirNode d)
        {
            var r = RootOf(d.Top);
            if (r != null) RefreshCapacity(r);
            var slices = BuildSlices(contents, d.Size, "of folder");
            string title = d.Parent == null && r != null ? r.DisplayName : d.Name, sub = d.FullPath;
            if (d.Parent == null && r != null && r.IsDrive && r.Total > 0)
            {
                long used = r.Total - r.Free;
                if (used - d.Size > 0)
                    slices.Add(new ChartSlice
                    {
                        Label = "System / not readable", Size = used - d.Size, Color = Theme.PILL,
                        Detail = string.Format("{0:0.0} % of drive", 100.0 * (used - d.Size) / r.Total)
                    });
                slices.Add(new ChartSlice
                {
                    Label = "Free space", Size = r.Free, Color = Theme.CHART_FREE, Empty = true,
                    Detail = string.Format("{0:0.0} % of drive", 100.0 * r.Free / r.Total)
                });
                sub = string.Format("{0} used of {1}  -  {2} free", Fmt.Size(used), Fmt.Size(r.Total), Fmt.Size(r.Free));
            }
            chart.SetData(title, sub, slices, ContentsRings(d));
        }

        /// <summary>Outer rings around the folder contents chart: subfolders and their subfolders.</summary>
        static int ContentsRings(DirNode d) { return 2; }

        /// <summary>Nothing scanned yet: the chart pane shows the app logo; it morphs into the chart once the scan is done.</summary>
        void UpdateChartUnscanned(ScanRoot r)
        {
            rowSlice.Clear();
            string sub = r.Total > 0
                ? string.Format("{0} used of {1}  -  {2} free", Fmt.Size(r.Total - r.Free), Fmt.Size(r.Total), Fmt.Size(r.Free))
                : r.Path;
            chart.ShowLogo(r.DisplayName, sub, r.Scanning
                ? "Scanning... the chart appears when the scan is done."
                : "Not scanned yet. Press Scan, or double-click the drive.");
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
            btnAddList.Enabled = !busy && root != null;
            btnExport.Enabled = !busy && root != null;
            measure.Enabled = !busy;
            if (btnBasketRecycle != null) btnBasketRecycle.Enabled = btnBasketDelete.Enabled = idle && basket.Count > 0;
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
                overlay.SetStatus(j.Recycled > 0 ? string.Format("Moving to the Recycle Bin: {0:N0} items moved", Interlocked.Read(ref j.Recycled))
                    : string.Format("Deleting: {0:N0} files removed", Interlocked.Read(ref j.FilesDeleted)), Fmt.Shorten(j.Current, 80));
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
                UpdateDriveLabel();
                if (!busy) pathBox.Text = r.Path;
            }
            contents.EmptyText = null;
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
            SetChanges(d, items);
            items.Sort(contents.Sorter.Compare);

            contents.BeginUpdate();
            contents.ResetHover();
            contents.Items.Clear();
            contents.Items.AddRange(items.Select(it =>
            {
                var lvi = new ListViewItem(new[]
                {
                    it.Name, Fmt.Size(it.Size), it.Percent.ToString("0.0") + " %", ChangeText(it),
                    it.Files.ToString("N0"), it.IsDir ? it.Dirs.ToString("N0") : "", Fmt.Date(it.Modified)
                }) { Tag = it };
                if (it.IsSummary) lvi.ForeColor = Theme.MUTED;
                else if (it.IsDir && it.Dir.Denied) lvi.ForeColor = Theme.MUTED;
                return lvi;
            }).ToArray());
            contents.EndUpdate();
            pContents.Caption.Text = string.Format("{0}  -  {1} in {2:N0} files and {3:N0} folders{4}", Fmt.Shorten(d.FullPath, 90), Fmt.Size(d.Size),
                d.Files, d.Dirs, ChangeCaption(d));
            ContentsChanged();
            UpdateButtons();
        }

        List<ItemInfo> GetSelection()
        {
            var list = new List<ItemInfo>();
            if (lastView == tree || lastView == null)
            {
                if (root == null) return list;
                var d = tree.SelectedNode != null ? tree.SelectedNode.Tag as DirNode : null;
                if (d != null) list.Add(ItemInfo.ForDir(d, d.Parent != null ? d.Parent.Size : d.Size));
            }
            else
            {
                var lv = (ListView)lastView;
                foreach (ListViewItem lvi in lv.SelectedItems)
                {
                    var it = lvi.Tag as ItemInfo;
                    if (it != null && !it.IsSummary && !it.IsGroup && (it.CanDelete || it.FullPath != null)) list.Add(it);
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
            if (it.File == null) { ShowInExplorer(); return; }
            if (lastView != contents && IsAttached(it.File.Dir))
            {
                SelectDir(it.File.Dir);
                viewTabs.SelectedIndex = 0;
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

        void RemoveNode(DirNode dn)
        {
            var p = dn.Parent;
            if (p == null) return;
            p.Children.Remove(dn);
            p.AddUp(-dn.Bytes, -dn.Alloc, -dn.Files, -(dn.Dirs + 1));
            dn.Parent = null;
        }

        void ReplaceNode(DirNode old, DirNode fresh)
        {
            var p = old.Parent;
            if (p == null) { var r = RootOf(old); if (r != null) r.Node = fresh; return; }
            fresh.Name = old.Name;
            fresh.Modified = old.Modified;
            fresh.Parent = p;
            p.Children[p.Children.IndexOf(old)] = fresh;
            p.AddUp(fresh.Bytes - old.Bytes, fresh.Alloc - old.Alloc, fresh.Files - old.Files, fresh.Dirs - old.Dirs);
            old.Parent = null;
        }

        void RemoveFile(FileEntry f)
        {
            var d = f.Dir;
            if (!IsAttached(d)) return;
            d.RemoveFileRec(f.Name);
        }

        /// <summary>Switches every view between file size and size on disk.</summary>
        void SetMeasure(bool onDisk)
        {
            if (DirNode.OnDisk == onDisk) return;
            DirNode.OnDisk = onDisk;
            foreach (var r in roots)
                if (r.Node != null) r.Node.ResortAll();
            var keep = current;
            current = null;
            if (keep != null && IsAttached(keep)) RefreshTree(keep);
            else if (active != null) { RefreshTree(null); }
            ViewsChanged();
            statusLabel.Text = onDisk
                ? "Showing size on disk: the space files really take (compressed and sparse files less, small files a whole cluster, online-only cloud files nothing)."
                : "Showing file size: the length of each file, as Explorer's Size column shows it.";
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
            var s = new Scanner();
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
                ReplaceNode(dir, fresh);
                if (inside) target = FindByPath(fresh, here) ?? fresh;
            }
            if (target == null || !IsAttached(target)) target = dir.Parent;
            IgnoreOwnChanges();
            current = null;
            RefreshTree(target);
            ViewsChanged();
            UpdateDriveLabel();
            statusLabel.Text = string.Format("Rescanned {0}: {1} in {2:0.00} s", path, Fmt.Size(fresh.Size), sw.Elapsed.TotalSeconds);
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
