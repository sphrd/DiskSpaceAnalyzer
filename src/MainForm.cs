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

        readonly float scale;
        readonly bool isAdmin;

        DirNode root, current;
        List<FileEntry> topFiles = new List<FileEntry>();
        Scanner scanner;
        DeleteJob deleteJob;
        bool busy;
        Control lastView;

        readonly ToolStripComboBox pathBox;
        readonly ToolStripButton btnBrowse, btnScan, btnStop, btnUp, btnRescan, btnExplorer, btnDelete, btnAdmin;
        readonly SplitContainer split;
        readonly TreeView tree;
        readonly TabControl tabs;
        readonly SizeListView contents, largest;
        readonly ToolStripStatusLabel statusLabel, driveLabel;
        readonly ToolStripProgressBar progress;
        readonly System.Windows.Forms.Timer uiTimer;
        readonly ContextMenuStrip menu;
        ToolStripMenuItem miOpen, miExplorer, miCopy, miRescan, miDelete;

        public MainForm(string initialPath)
        {
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

            Text = AppName + (isAdmin ? " (Administrator)" : "");
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(S(1240), S(780));
            MinimumSize = new Size(S(700), S(420));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            var icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(S(16), S(16)) };
            var folderIcon = Native.StockIcon(true);
            var fileIcon = Native.StockIcon(false);
            if (folderIcon != null) icons.Images.Add("folder", folderIcon);
            if (fileIcon != null) icons.Images.Add("file", fileIcon);

            // Toolbar
            var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(S(6), S(4), S(6), S(4)), ImageScalingSize = new Size(S(16), S(16)) };
            pathBox = new ToolStripComboBox { AutoSize = false, Width = S(340), DropDownStyle = ComboBoxStyle.DropDown, ToolTipText = "Drive or folder to analyze (you can also drag a folder onto the window)" };
            foreach (var d in DriveInfo.GetDrives())
                if (d.IsReady) pathBox.Items.Add(d.Name);
            pathBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartScan(); } };
            pathBox.ComboBox.SelectionChangeCommitted += (s, e) => BeginInvoke(new Action(StartScan));

            btnBrowse = Btn("Browse...", "Choose a folder", Browse);
            btnScan = Btn("Scan", "Analyze the drive or folder (Enter)", StartScan);
            btnScan.Font = new Font(Font, FontStyle.Bold);
            btnStop = Btn("Stop", "Stop the scan and show partial results", () => { if (scanner != null) scanner.Cancel(); });
            btnUp = Btn("Up", "Go to the parent folder (Backspace)", GoUp);
            btnRescan = Btn("Rescan folder", "Rescan the selected folder (F5)", RescanSelected);
            btnExplorer = Btn("Show in Explorer", "Open the selected item in Windows Explorer", ShowInExplorer);
            btnDelete = Btn("Delete permanently", "Permanently delete the selected items, bypassing the Recycle Bin (Del)", DeleteSelected);
            btnDelete.ForeColor = Color.FromArgb(180, 30, 30);
            btnAdmin = Btn("Run as administrator", "Restart elevated to read folders that need admin rights", RestartAsAdmin);
            btnAdmin.Alignment = ToolStripItemAlignment.Right;
            btnAdmin.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            try { btnAdmin.Image = new Icon(SystemIcons.Shield, S(16), S(16)).ToBitmap(); } catch (Exception) { }
            btnAdmin.Visible = !isAdmin;

            tool.Items.Add(new ToolStripLabel("Drive / folder:"));
            tool.Items.Add(pathBox);
            tool.Items.Add(btnBrowse);
            tool.Items.Add(btnScan);
            tool.Items.Add(btnStop);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(btnUp);
            tool.Items.Add(btnRescan);
            tool.Items.Add(btnExplorer);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(btnDelete);
            tool.Items.Add(btnAdmin);

            // Context menu
            menu = new ContextMenuStrip();
            miOpen = new ToolStripMenuItem("Open folder", null, (s, e) => OpenSelected());
            miExplorer = new ToolStripMenuItem("Show in Explorer", null, (s, e) => ShowInExplorer());
            miCopy = new ToolStripMenuItem("Copy path", null, (s, e) => CopyPath());
            miRescan = new ToolStripMenuItem("Rescan folder", null, (s, e) => RescanSelected()) { ShortcutKeyDisplayString = "F5" };
            miDelete = new ToolStripMenuItem("Delete permanently...", null, (s, e) => DeleteSelected()) { ShortcutKeyDisplayString = "Del", ForeColor = btnDelete.ForeColor };
            menu.Items.AddRange(new ToolStripItem[] { miOpen, miExplorer, miCopy, miRescan, new ToolStripSeparator(), miDelete });
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

            // Folder tree
            tree = new TreeView
            {
                Dock = DockStyle.Fill, HideSelection = false, ShowLines = false, FullRowSelect = true,
                ImageList = icons, ImageKey = "folder", SelectedImageKey = "folder", ItemHeight = S(22),
                ContextMenuStrip = menu, BorderStyle = BorderStyle.None
            };
            tree.HandleCreated += (s, e) => Native.SetWindowTheme(tree.Handle, "explorer", null);
            tree.BeforeExpand += (s, e) => Populate(e.Node);
            tree.AfterSelect += (s, e) => { var d = e.Node.Tag as DirNode; if (d != null) ShowContents(d); };
            tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };
            tree.Enter += (s, e) => lastView = tree;

            // Folder contents
            contents = new SizeListView { Dock = DockStyle.Fill, SmallImageList = icons, ContextMenuStrip = menu, BorderStyle = BorderStyle.None };
            contents.SetColumns(new[] { "Name", "Size", "% of folder", "Files", "Folders", "Modified" },
                new[] { "name", "size", "size", "files", "dirs", "mod" }, new[] { 320, 90, 170, 90, 80, 130 }, scale);
            contents.BarColumn = 2;
            contents.ColumnClick += (s, e) => contents.SortBy(e.Column);
            contents.DoubleClick += (s, e) => OpenSelected();
            contents.Enter += (s, e) => lastView = contents;

            // Largest files
            largest = new SizeListView { Dock = DockStyle.Fill, SmallImageList = icons, ContextMenuStrip = menu, BorderStyle = BorderStyle.None };
            largest.SetColumns(new[] { "Name", "Size", "Folder", "Modified" },
                new[] { "name", "size", "folder", "mod" }, new[] { 260, 90, 520, 130 }, scale);
            largest.ColumnClick += (s, e) => largest.SortBy(e.Column);
            largest.DoubleClick += (s, e) => OpenSelected();
            largest.Enter += (s, e) => lastView = largest;

            tabs = new TabControl { Dock = DockStyle.Fill };
            var p1 = new TabPage("Folder contents");
            p1.Controls.Add(contents);
            var p2 = new TabPage("Largest files (top " + TopCount + ")");
            p2.Controls.Add(largest);
            tabs.TabPages.Add(p1);
            tabs.TabPages.Add(p2);

            split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = S(5) };
            split.Panel1.Controls.Add(tree);
            split.Panel2.Controls.Add(tabs);

            // Status bar
            var status = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel("Choose a drive or folder and press Scan.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            progress = new ToolStripProgressBar { Style = ProgressBarStyle.Marquee, Visible = false, Width = S(140) };
            driveLabel = new ToolStripStatusLabel("") { BorderSides = ToolStripStatusLabelBorderSides.Left };
            status.Items.AddRange(new ToolStripItem[] { statusLabel, progress, driveLabel });

            Controls.Add(split);
            Controls.Add(tool);
            Controls.Add(status);

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
                    pathBox.Focus();
                }
            };
            FormClosing += (s, e) => { if (scanner != null) scanner.Cancel(); };
            UpdateButtons();
        }

        int S(int v) { return (int)Math.Round(v * scale); }

        static ToolStripButton Btn(string text, string tip, Action click)
        {
            var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip };
            b.Click += (s, e) => click();
            return b;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (pathBox.Focused) return;
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
            progress.Visible = value;
            if (value) uiTimer.Start(); else uiTimer.Stop();
            split.Enabled = !value;
            UseWaitCursor = false;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            bool scanning = busy && scanner != null;
            btnScan.Enabled = !busy;
            btnBrowse.Enabled = !busy;
            pathBox.Enabled = !busy;
            btnStop.Enabled = scanning;
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
                statusLabel.Text = string.Format("Scanning... {0:N0} folders, {1:N0} files, {2}    {3}",
                    Interlocked.Read(ref s.DirsSeen), Interlocked.Read(ref s.FilesSeen), Fmt.Size(Interlocked.Read(ref s.BytesSeen)),
                    Fmt.Shorten(s.Current, 90));
            else if (j != null)
                statusLabel.Text = string.Format("Deleting... {0:N0} files removed    {1}", Interlocked.Read(ref j.FilesDeleted), Fmt.Shorten(j.Current, 90));
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
            var tn = new TreeNode(TreeText(d)) { Tag = d, ImageKey = "folder", SelectedImageKey = "folder" };
            if (d.Denied) tn.ForeColor = Color.Gray;
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
                }) { Tag = it, ImageKey = it.IsDir ? "folder" : "file" };
                if (it.IsSummary) { lvi.ForeColor = Color.Gray; lvi.ImageKey = null; }
                else if (it.IsDir && it.Dir.Denied) lvi.ForeColor = Color.Gray;
                return lvi;
            }).ToArray());
            contents.EndUpdate();

            tabs.TabPages[0].Text = "Folder contents: " + Fmt.Shorten(d.Parent == null ? d.Name : d.Name, 40) + "  (" + Fmt.Size(d.Size) + ")";
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
                Tag = it, ImageKey = "file"
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
