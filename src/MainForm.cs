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
    internal sealed class MainForm : Form
    {
        const string AppName = "Disk Space Analyzer";
        const int TopCount = 1000;
        const int MaxFilesShown = 2000;

        readonly bool isAdmin;

        DirNode root, current;
        List<FileEntry> topFiles = new List<FileEntry>();
        Scanner scanner;
        DeleteJob deleteJob;
        bool busy;
        Control lastView;

        readonly PathInput pathBox;
        readonly Label pathLabel;
        readonly RoundButton btnBrowse, btnScan, btnUp, btnRescan, btnExplorer, btnDelete, btnAdmin;
        readonly Panel toolbar;
        readonly SplitContainer split;
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
            Size = new Size(S(1240), S(780));
            MinimumSize = new Size(S(760), S(460));
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
                miRescan.Enabled = any && sel.Count == 1 && sel[0].IsDir;
                miDelete.Enabled = any;
            };

            // Folder tree. The image list only reserves the icon slot; glyphs are drawn by the tree.
            tree = new DarkTreeView
            {
                Dock = DockStyle.None, ItemHeight = S(26), Indent = S(20), ContextMenuStrip = menu,
                ImageList = new ImageList { ImageSize = new Size(S(16), S(16)) }
            };
            tree.BeforeExpand += (s, e) => Populate(e.Node);
            tree.AfterSelect += (s, e) => { var d = e.Node.Tag as DirNode; if (d != null) ShowContents(d); };
            tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };
            tree.Enter += (s, e) => lastView = tree;
            var treePanel = new RoundedPanel(12, 8) { Dock = DockStyle.Fill, TrackFocus = true };
            treePanel.Controls.Add(new ScrollHost(tree) { Dock = DockStyle.Fill });

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
            split.Panel2.Controls.Add(listPanel);

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

            Controls.Add(split);
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
                if (files == null || files.Length == 0 || busy) return;
                string p = files[0];
                if (!Directory.Exists(p)) p = Path.GetDirectoryName(p);
                pathBox.Text = p;
                StartScan();
            };

            Shown += (s, e) =>
            {
                split.SplitterDistance = S(380);
                if (!string.IsNullOrEmpty(initialPath)) { pathBox.Text = initialPath; StartScan(); }
                else
                {
                    string sys = Path.GetPathRoot(Environment.SystemDirectory);
                    pathBox.Text = sys;
                    pathBox.Box.Focus();
                }
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

        async void StartScan()
        {
            if (busy) return;
            string path;
            try { path = NormalizePath(pathBox.Text); }
            catch (Exception ex) { MessageBox.Show(this, "Invalid path: " + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, "Folder not found:\n" + path, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var s = new Scanner(TopCount);
            scanner = s;
            SetBusy(true);
            pathBox.Text = path;
            tree.Nodes.Clear();
            contents.Items.Clear();
            largest.Items.Clear();
            root = current = null;
            var sw = Stopwatch.StartNew();
            DirNode result = null;
            try { await RunThread(() => result = s.Run(path)); }
            catch (Exception ex)
            {
                SetBusy(false);
                MessageBox.Show(this, "Scan failed: " + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            sw.Stop();
            root = result;
            topFiles = s.TopFiles;
            SetBusy(false);
            RefreshTree(root);
            FillLargest();
            UpdateDriveLabel();
            statusLabel.Text = string.Format("{0}: {1} in {2:N0} files and {3:N0} folders - scanned in {4:0.00} s{5}",
                root.Name, Fmt.Size(root.Size), root.Files, root.Dirs, sw.Elapsed.TotalSeconds,
                s.Cancelled ? "  (stopped - partial results)" : "");
            scanner = null;
        }

        void SetBusy(bool value)
        {
            busy = value;
            if (value)
            {
                overlay.ShowBusy("Starting...", scanner != null);
                uiTimer.Start();
            }
            else
            {
                uiTimer.Stop();
                overlay.HideBusy();
            }
            split.Enabled = !value;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            btnScan.Enabled = !busy;
            btnBrowse.Enabled = !busy;
            pathBox.Enabled = !busy;
            btnUp.Enabled = !busy && current != null && current.Parent != null;
            btnRescan.Enabled = !busy && root != null;
            btnExplorer.Enabled = !busy && root != null;
            btnDelete.Enabled = !busy && root != null;
        }

        void UpdateProgress()
        {
            var s = scanner;
            var j = deleteJob;
            if (s != null)
            {
                string text = s.Cancelled
                    ? "Cancelling... (finishing current folder)"
                    : string.Format("Scanning: {0:N0} folders, {1:N0} files, {2} so far",
                        Interlocked.Read(ref s.DirsSeen), Interlocked.Read(ref s.FilesSeen), Fmt.Size(Interlocked.Read(ref s.BytesSeen)));
                overlay.SetStatus(text, Fmt.Shorten(s.Current, 80));
            }
            else if (j != null)
                overlay.SetStatus(string.Format("Deleting: {0:N0} files removed", Interlocked.Read(ref j.FilesDeleted)), Fmt.Shorten(j.Current, 80));
        }

        void UpdateDriveLabel()
        {
            driveLabel.Text = "";
            if (root == null) return;
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(root.FullPath));
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
            string p = root != null ? root.FullPath : pathBox.Text.Trim();
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
            string text = d.Name + "   " + Fmt.Size(d.Size);
            if (d.Parent != null && d.Parent.Size > 0) text += string.Format("   {0:0.0}%", 100.0 * d.Size / d.Parent.Size);
            if (d.Denied) text += "   [access denied]";
            return text;
        }

        static TreeNode MakeTreeNode(DirNode d)
        {
            var tn = new TreeNode(TreeText(d)) { Tag = d, Name = d.Name };
            if (d.Denied) tn.ForeColor = Theme.MUTED;
            if (d.Children != null && d.Children.Count > 0) tn.Nodes.Add(new TreeNode("..."));
            return tn;
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
            return d != null && root != null && d.Top == root;
        }

        /// <summary>Rebuilds the tree from the model, keeping expanded folders, then selects target.</summary>
        void RefreshTree(DirNode target)
        {
            var expanded = new HashSet<DirNode>();
            CollectExpanded(tree.Nodes, expanded);
            tree.BeginUpdate();
            tree.Nodes.Clear();
            if (root != null)
            {
                var rn = MakeTreeNode(root);
                tree.Nodes.Add(rn);
                rn.Expand();
                foreach (TreeNode c in rn.Nodes) ReExpand(c, expanded);
            }
            tree.EndUpdate();
            if (root != null) SelectDir(IsAttached(target) ? target : root);
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
            TreeNode cur = tree.Nodes[0];
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

        void RemoveNode(DirNode dn)
        {
            topFiles.RemoveAll(f => f.Dir.IsUnder(dn));
            var p = dn.Parent;
            if (p == null) return;
            p.Children.Remove(dn);
            for (var a = p; a != null; a = a.Parent) { a.Size -= dn.Size; a.Files -= dn.Files; a.Dirs -= dn.Dirs + 1; }
            ResortUp(p);
            dn.Parent = null;
        }

        void ReplaceNode(DirNode old, DirNode fresh, List<FileEntry> freshTop)
        {
            topFiles.RemoveAll(f => f.Dir.IsUnder(old));
            var p = old.Parent;
            if (p == null) root = fresh;
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
            topFiles.AddRange(freshTop);
            topFiles.Sort(FileEntry.BySizeDesc);
            if (topFiles.Count > TopCount) topFiles.RemoveRange(TopCount, topFiles.Count - TopCount);
        }

        void RemoveFile(FileEntry f)
        {
            var d = f.Dir;
            if (!IsAttached(d)) return;
            d.LocalSize -= f.Size;
            d.LocalFiles--;
            for (var a = d; a != null; a = a.Parent) { a.Size -= f.Size; a.Files--; }
            ResortUp(d.Parent);
            topFiles.RemoveAll(t => t.Dir == d && string.Equals(t.Name, f.Name, StringComparison.OrdinalIgnoreCase));
        }

        async void RescanSelected()
        {
            if (busy || root == null) return;
            var sel = GetSelection();
            DirNode dir = sel.Count == 1 && sel[0].IsDir ? sel[0].Dir : current;
            if (dir == null) return;
            if (dir == root) { pathBox.Text = root.FullPath; StartScan(); return; }

            string path = dir.FullPath;
            var s = new Scanner(TopCount);
            scanner = s;
            SetBusy(true);
            DirNode fresh = null;
            var sw = Stopwatch.StartNew();
            try { await RunThread(() => fresh = s.Run(path)); }
            finally { scanner = null; SetBusy(false); }

            DirNode target;
            if (!Native.Exists(path)) { target = dir.Parent; RemoveNode(dir); }
            else { ReplaceNode(dir, fresh, s.TopFiles); target = fresh; }
            RefreshTree(target);
            FillLargest();
            UpdateDriveLabel();
            statusLabel.Text = string.Format("Rescanned {0}: {1} in {2:0.00} s", path, Fmt.Size(fresh.Size), sw.Elapsed.TotalSeconds);
        }

        async void DeleteSelected()
        {
            if (busy || root == null) return;
            var sel = GetSelection();
            if (sel.Count == 0) return;
            if (sel.Any(i => i.IsDir && i.Dir == root))
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
