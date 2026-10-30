using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>Screenshot mode (--cli screenshot): after the first scan, every view is opened and saved as a PNG, then the app closes.</summary>
    internal sealed partial class MainForm
    {
        public string ShotDir;
        public bool ShotDemo;
        bool shotsStarted;

        /// <summary>Demo pictures: the scanned folder becomes a made-up drive "E:\ Data"; this computer's drives disappear.</summary>
        void MakeDemoDrive()
        {
            foreach (var w in roots.ToList()) StopWatching(w);
            measure.SelectedIndex = 0;   // the demo files are sparse: they only look big by file size
            var r = roots.First(x => x.Node != null);
            roots.Clear();
            r.Path = @"E:\"; r.Node.Name = @"E:\"; r.Previous = null; r.IsDrive = true; r.IsSystem = false; r.Label = "Data";
            r.Total = 1000L * 1000 * 1000 * 1000; r.Free = r.Total - r.Node.Size - (14L << 30);
            roots.Add(r);
            roots.Add(new ScanRoot { Path = @"F:\", IsDrive = true, Label = "Backup - removable", Total = 2000L * 1000 * 1000 * 1000, Free = 1240L * 1000 * 1000 * 1000 });
            roots.Add(new ScanRoot { Path = @"G:\", IsDrive = true, Label = "Local disk", Total = 500L * 1000 * 1000 * 1000, Free = 310L * 1000 * 1000 * 1000 });
            active = r;
            current = null;
            dupeScope = @"E:\";
            pathBox.Text = @"E:\";
            pathBox.Choices.Clear();
            RefreshTree(r.Node);
            liveLabel.Text = "Live";
            statusLabel.Text = string.Format(@"E:\: {0} in {1:N0} files and {2:N0} folders - scanned in 0.41 s, read from the master file table",
                Fmt.Size(r.Node.Size), r.Node.Files, r.Node.Dirs);
            driveLabel.Text = string.Format(@"E:\  {0} free of {1} ({2:0}% free)", Fmt.Size(r.Free), Fmt.Size(r.Total), 100.0 * r.Free / r.Total);
        }

        void ClearHover()
        {
            chart.SetHover(-1);
            foreach (var p in pages) { p.List.ResetHover(); p.List.Invalidate(); }
        }

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        void SaveShot(string name)
        {
            using (var bmp = new Bitmap(Width, Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(Handle, hdc, 2);   // PW_RENDERFULLCONTENT
                    g.ReleaseHdc(hdc);
                }
                bmp.Save(Path.Combine(ShotDir, name + ".png"), ImageFormat.Png);
            }
        }

        /// <summary>Moves the real cursor onto a row (the first multi-location suggestion, else the first file, else row 2) and
        /// captures the screen around it, so the hover tooltip is checked the way a user sees it.</summary>
        async Task HoverShot(Control c, string name)
        {
            Point at;
            var lv = c as SizeListView;
            if (lv != null)
            {
                var items = lv.Items.Cast<ListViewItem>().ToList();
                var row = items.FirstOrDefault(x => x.Tag is ItemInfo && ((ItemInfo)x.Tag).Targets != null && ((ItemInfo)x.Tag).Targets.Count > 1)
                    ?? items.FirstOrDefault(x => x.Tag is ItemInfo && ((ItemInfo)x.Tag).File != null) ?? items.Skip(1).FirstOrDefault();
                if (row == null) return;
                var rb = lv.GetItemRect(row.Index);
                at = lv.PointToScreen(new Point(rb.X + S(90), rb.Y + rb.Height / 2));
            }
            else
            {
                var tv = (TreeView)c;
                var n = tv.SelectedNode != null && tv.SelectedNode.Nodes.Count > 0 ? tv.SelectedNode.Nodes[0] : tv.SelectedNode;
                if (n == null) return;
                at = tv.PointToScreen(new Point(n.Bounds.X + S(20), n.Bounds.Y + n.Bounds.Height / 2));
            }
            Activate();
            Cursor.Position = new Point(at.X - S(30), at.Y);
            await Task.Delay(150);
            Cursor.Position = at;
            await Task.Delay(1400);
            var area = new Rectangle(at.X - S(120), at.Y - S(40), S(760), S(240));
            using (var bmp = new Bitmap(area.Width, area.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.X, area.Y, 0, 0, bmp.Size);
                bmp.Save(Path.Combine(ShotDir, name + ".png"), ImageFormat.Png);
            }
            Cursor.Position = new Point(Left - S(20), Top - S(20));
            await Task.Delay(300);
        }

        async Task ShotDialog(Form dlg, string name)
        {
            using (dlg)
            {
                dlg.StartPosition = FormStartPosition.Manual;
                dlg.Location = new Point(Left + 80, Top + 80);
                dlg.Show(this);
                await Task.Delay(800);
                using (var bmp = new Bitmap(dlg.Width, dlg.Height))
                {
                    using (var g = Graphics.FromImage(bmp)) { IntPtr hdc = g.GetHdc(); PrintWindow(dlg.Handle, hdc, 2); g.ReleaseHdc(hdc); }
                    bmp.Save(Path.Combine(ShotDir, name + ".png"), ImageFormat.Png);
                }
                dlg.Close();
            }
        }

        async void RunShots()
        {
            try
            {
                Directory.CreateDirectory(ShotDir);
                if (ShotDemo)
                {
                    // Duplicates read the files, so they are found before the folder is shown as a made-up drive.
                    FindDuplicates();
                    for (int k = 0; k < 600 && dupeFinder != null; k++) await Task.Delay(200);
                    MakeDemoDrive();
                }
                // One shot per tab, in tab order (Suggestions first), plus hover shots with the real cursor.
                for (int i = 0; i < pages.Count; i++)
                {
                    var p = pages[i];
                    if (ShotDemo && (p == pSuggest || p == pChanges || p == pApps)) continue;   // they would show this computer
                    if (p == pBasket)
                        foreach (var it in pFiles.List.Items.Cast<ListViewItem>().Select(x => x.Tag as ItemInfo).Where(x => x != null && x.CanDelete).Take(4))
                            foreach (var t in DeleteTarget.From(it)) AddTarget(t);
                    viewTabs.SelectedIndex = i;
                    if (p == pDupes && !(ShotDemo && dupeGroups != null))
                    {
                        FindDuplicates();
                        for (int k = 0; k < 600 && dupeFinder != null; k++) await Task.Delay(200);
                    }
                    if (p == pSuggest)
                    {
                        for (int k = 0; k < 300 && (suggestions == null || suggestionsDirty); k++) await Task.Delay(200);
                        var first = suggestions.Where(x => x.Size > 0 && x.Targets.Count > 1).OrderByDescending(x => x.Size).FirstOrDefault()
                            ?? suggestions.Where(x => x.Size > 0).OrderByDescending(x => x.Size).FirstOrDefault();
                        if (first != null) { first.Expanded = true; ShowSuggestions(); }
                    }
                    if (p == pApps)
                    {
                        for (int k = 0; k < 600 && (apps == null || appsLoading); k++) await Task.Delay(200);
                        // Expand the first app with files outside its program folder (read only).
                        foreach (var a in apps.Where(x => !x.IsStore && x.SharedWith == null).Take(15).ToList())
                        {
                            await FindLeftovers(a);
                            if (a.Leftovers.Count > 1) { a.Expanded = true; ShowApps(); RevealApp(a); break; }
                        }
                    }
                    await Task.Delay(i == 0 ? 2500 : 2000);
                    if (ShotDemo) ClearHover();
                    string name = (i + 1) + "-" + p.Title.Split(' ')[0].ToLowerInvariant();
                    SaveShot(name);
                    if (!ShotDemo && (p == pContents || p == pSuggest)) await HoverShot(p.List, name + "-hover");
                }
                if (!ShotDemo) await HoverShot(tree, "tree-hover");                viewTabs.SelectedIndex = pages.IndexOf(pContents);
                measure.SelectedIndex = 0;
                await Task.Delay(1500);
                SaveShot((pages.Count + 1) + "-file-size");
                measure.SelectedIndex = 1;
                using (var about = new AboutForm(null))
                {
                    about.StartPosition = FormStartPosition.Manual;
                    about.Location = new Point(Left + 80, Top + 80);
                    about.Show(this);
                    await Task.Delay(800);
                    using (var bmp = new Bitmap(about.Width, about.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) { IntPtr hdc = g.GetHdc(); PrintWindow(about.Handle, hdc, 2); g.ReleaseHdc(hdc); }
                        bmp.Save(Path.Combine(ShotDir, (pages.Count + 2) + "-about.png"), ImageFormat.Png);
                    }
                    about.Close();
                }
                // Prompts and the delete strip (made-up items, nothing is deleted).
                var sample = Enumerable.Range(1, 40).Select(k => string.Format(@"E:\Projects\web-app-{0}\node_modules{1}{2}", k, "\t", Fmt.Size(180000000L - k * 3100000L))).ToList();
                await ShotDialog(new AlertForm(AlertIcon.Danger, "Delete 40 items permanently?",
                    Fmt.Size(sample.Count * 120000000L) + " will be deleted. This bypasses the Recycle Bin and cannot be undone.", null, sample, "Delete permanently", "Cancel"),
                    (pages.Count + 3) + "-delete-confirm");
                var errs = Enumerable.Range(1, 30).Select(k => string.Format(@"E:\Projects\web-app-{0}\node_modules\.bin\esbuild.exe{1}The process cannot access the file because it is being used by another process", k, "\t")).ToList();
                await ShotDialog(new AlertForm(AlertIcon.Warn, "30 items could not be deleted", "They are in use by a running program, or access was denied. Everything else was deleted.",
                    "Run as administrator (toolbar) to remove items that need it.", errs, "OK"), (pages.Count + 4) + "-delete-errors");
                if (ShotDemo) { measure.SelectedIndex = 0; await Task.Delay(1200); }
                deleteBar.Start("Deleting permanently...");
                deleteBar.SetProgress("Deleting permanently: 48,213 files removed, 6.42 GB freed", @"E:\Projects\web-app-7\node_modules\typescript\lib\lib.dom.d.ts", 0.42);
                await Task.Delay(800);
                SaveShot((pages.Count + 5) + "-deleting");
                deleteBar.Stop();
            }
            catch (Exception ex) { try { File.WriteAllText(Path.Combine(ShotDir, "error.txt"), ex.ToString()); } catch (Exception) { } }
            Close();
        }

        // ---------------------------------------------------------------- self-test (--cli selftest)

        public string SelfTestDir;
        public bool SelfTestFailed;

        async void RunSelfTest()
        {
            var log = new System.Text.StringBuilder();
            Action<bool, string> check = (ok, what) => { log.AppendLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) SelfTestFailed = true; };
            string dir = SelfTestDir;
            Func<string, string> P = rel => Path.Combine(dir, rel);
            try
            {
                var top = root;
                for (int i = 0; i < 20 && chart.MorphFrames < 8; i++) await Task.Delay(100);
                check(chart.Morphs == 1 && chart.MorphFrames >= 8, "chart: the logo turned into the chart after the scan (" + chart.MorphFrames + " frames)");
                check(top != null && top.Files == 10 && top.Dirs == 8, string.Format("scan: {0} files, {1} folders (expected 10, 8)", top != null ? top.Files : -1, top != null ? top.Dirs : -1));

                // Chart: every row of at least 2 % of the total (7.2 degrees) has a slice of its own, in true proportion;
                // all smaller rows together form one grey "smaller items" slice.
                {
                    var sl = BuildSlices(contents, top.Size, "of folder");
                    var bad = new List<string>();
                    int ownCount = 0;
                    foreach (ListViewItem r in contents.Items)
                    {
                        var ri = (ItemInfo)r.Tag;
                        var s = sl.FirstOrDefault(x => x.Rows.Contains(r));
                        bool own = s != null && s.Color != Theme.CHART_OTHER;
                        if (own) ownCount++;
                        if (own != (ri.ChartSize > 0 && ri.ChartSize >= 0.02 * top.Size)) bad.Add(ri.Name);
                    }
                    check(bad.Count == 0 && ownCount > 0 && sl.Count(x => x.Color == Theme.CHART_OTHER) <= 1,
                        "chart: rows of 2 % or more get their own slice, the rest one grey slice (" + ownCount + " of " + contents.Items.Count + ")" +
                        (bad.Count > 0 ? "; wrong: " + string.Join(", ", bad) : ""));

                    // A list like the Files view: a small row first (as if sorted by name), two rows over 2 %, 30 small rows
                    // and a big "[N smaller files not listed]" row: 3 own slices and exactly one grey slice with all 31 small rows.
                    var many = new SizeListView();
                    Action<string, long, bool> add = (n, sz, sum) => { var mi = new ItemInfo { Name = n, Size = sz, IsSummary = sum }; many.Items.Add(new ListViewItem(n) { Tag = mi }); };
                    add("a-small", 900, false);
                    add("big1", 5000, false);
                    add("big2", 3000, false);
                    for (int i = 0; i < 30; i++) add("f" + i, 1000 - i, false);
                    add("[smaller files not listed]", 50000, true);
                    var ms = BuildSlices(many, 100000, "");
                    var own3 = ms.Where(x => x.Color != Theme.CHART_OTHER).Select(x => x.Label).ToList();
                    var grey = ms.Where(x => x.Color == Theme.CHART_OTHER).ToList();
                    check(own3.SequenceEqual(new[] { "big1", "big2", "[smaller files not listed]" }) && grey.Count == 1 && grey[0].Rows.Count == 31,
                        "chart: 3 rows over 2 % own slices, all 31 smaller rows in one grey slice (" + string.Join(", ", own3) + "; " + grey.Count + " grey, " +
                        (grey.Count > 0 ? grey[0].Rows.Count : 0) + " rows)");
                    many.Dispose();
                    UpdateChart(top);
                }

                // Live updates: a new file and a new folder appear without a rescan.
                File.WriteAllBytes(P(@"A\new.txt"), new byte[5000]);
                Directory.CreateDirectory(P(@"E\deep"));
                File.WriteAllBytes(P(@"E\deep\e.txt"), new byte[7000]);
                for (int i = 0; i < 40 && (FindScanned(P("E"), true) == null || FindScanned(P("A"), true).LocalFiles != 3); i++) await Task.Delay(250);
                var a = FindScanned(P("A"), true);
                var e = FindScanned(P(@"E\deep"), true);
                check(a != null && a.LocalFiles == 3, "live: new file in A noticed");
                check(e != null && e.Files == 1 && root.Files == 12, "live: new folder E\\deep with its file noticed (total " + root.Files + " files)");

                // Live: a file in a folder whose path is longer than 260 characters (crashed v1.7.0).
                string longDir = P(@"L\" + new string('x', 120) + @"\" + new string('y', 120) + @"\" + new string('z', 40));
                string sofar = P("L");
                SelfTest.MakeDirLong(sofar);
                foreach (var part in longDir.Substring(sofar.Length + 1).Split('\\')) { sofar += @"\" + part; SelfTest.MakeDirLong(sofar); }
                await Task.Delay(2500);
                SelfTest.MakeFileLong(longDir + @"\long.txt");
                for (int i = 0; i < 40 && root.Files != 12; i++) await Task.Delay(250);
                check(root.Files == 12 && FindScanned(longDir, true) != null, "live: a file in a " + longDir.Length + "-character path noticed, no crash");
                new DeleteJob().DeleteDirectory(P("L"));
                for (int i = 0; i < 40 && root.Files != 11; i++) await Task.Delay(250);
                check(root.Files == 12, "live: the long folder removed again");

                // Duplicates: dup1.bin and dup2.bin are identical; other.bin has the same size but other content.
                SelectDir(root);
                dupeMin.SelectedIndex = 3;
                viewTabs.SelectedIndex = pages.IndexOf(pDupes);
                FindDuplicates();
                for (int i = 0; i < 100 && dupeFinder != null; i++) await Task.Delay(100);
                check(dupeGroups != null && dupeGroups.Count == 1 && dupeGroups[0].Count == 2 && dupeGroups[0].All(f => f.Name.StartsWith("dup")),
                    "duplicates: one group (dup1.bin, dup2.bin)");

                // Suggestions: the node_modules folder of project P.
                var sug = await Task.Factory.StartNew(() => FindSuggestions(roots.Where(r => r.Node != null).Select(r => r.Node).ToList()));
                var nm = sug.FirstOrDefault(s => s.Name == "node_modules folders");
                check(nm != null && nm.Targets.Count == 1 && SamePath(nm.Targets[0].Path, P(@"P\node_modules")), "suggestions: node_modules of P found");
                // Never the node_modules of installed programs or tools (VS Code's bundled extensions, Electron apps, editor extensions).
                {
                    Directory.CreateDirectory(P(@"Tool\resources\app"));
                    File.WriteAllText(P(@"Tool\resources\app\package.json"), "{}");
                    Directory.CreateDirectory(P(@"Home\.vscode\extensions\x"));
                    File.WriteAllText(P(@"Home\.vscode\extensions\x\package.json"), "{}");
                    Directory.CreateDirectory(P("NoPkg"));
                    string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    bool ok = IsProjectModules(P(@"P\node_modules")) && !IsProjectModules(P(@"Tool\resources\app\node_modules")) &&
                              !IsProjectModules(P(@"Home\.vscode\extensions\x\node_modules")) && !IsProjectModules(P(@"NoPkg\node_modules")) &&
                              !IsProjectModules(Path.Combine(pf, @"Microsoft VS Code\resources\app\extensions\copilot\node_modules"));
                    check(ok, "suggestions: node_modules only of projects (package.json), never in Program Files, resources\\app, .vscode, extensions");
                    Directory.Delete(P("Tool"), true);
                    Directory.Delete(P("Home"), true);
                    Directory.Delete(P("NoPkg"), true);
                }

                // Delete list: folder B to the Recycle Bin on CI (permanently here, to keep the user's bin clean).
                bool ci = Environment.GetEnvironmentVariable("CI") != null;
                var b = FindScanned(P("B"), true);
                basket.Clear();
                AddTarget(new DeleteTarget { Path = b.FullPath, Label = "B", IsDir = true, Dir = b, Size = b.Size });
                AddTarget(new DeleteTarget { Path = P(@"B\b1.bin"), Label = "b1.bin", Size = 1 });   // covered by B: not added
                check(basket.Count == 1, "delete list: a file inside a listed folder is not added twice");
                long before = root.Files;
                var job = await ConfirmAndDelete(basket.ToList(), ci);
                check(job != null && !Directory.Exists(P("B")) && FindScanned(P("B"), true) == null && root.Files == before - 2,
                    (ci ? "Recycle Bin" : "permanent delete") + ": folder B gone from disk and scan (" + root.Files + " files left)");

                // Contents only: A stays, its files and subfolder go. A is open in Contents: its rows leave the list while the job
                // runs (before the scan is updated), and the red delete strip goes away at the end.
                var aNode = FindScanned(P("A"), true);
                viewTabs.SelectedIndex = pages.IndexOf(pContents);
                SelectDir(aNode);
                await Task.Delay(300);
                int aRows = contents.Items.Count;
                job = await ConfirmAndDelete(new List<DeleteTarget> { new DeleteTarget { Path = aNode.FullPath, IsDir = true, ContentsOnly = true, Dir = aNode, Label = "A" } }, false);
                check(aRows >= 3 && LiveRowsRemoved >= 3 && !deleteBar.Visible && !busy,
                    string.Format("live delete: {0} of {1} rows left the open list as their files went; the delete strip closed", LiveRowsRemoved, aRows));
                aNode = FindScanned(P("A"), true);
                check(Directory.Exists(P("A")) && Directory.GetFileSystemEntries(P("A")).Length == 0 && aNode != null && aNode.Files == 0 && aNode.Dirs == 0,
                    "contents only: A kept and emptied, in the scan too");

                // A single file from the index.
                var c = FindScanned(P("C"), true);
                var other = Scanner.ListFiles(c).First(f => f.Name == "other.bin");
                job = await ConfirmAndDelete(new List<DeleteTarget> { new DeleteTarget { Path = other.FullPath, File = other, Label = other.Name, Size = other.Size } }, false);
                check(!File.Exists(P(@"C\other.bin")) && c.FileList.All(f => f.Name != "other.bin") && c.LocalFiles == 1, "file delete: other.bin gone from disk and index");
                check(root.Files == 5, "totals after deletes: " + root.Files + " files (expected 5)");

                // Prompts are the themed dialog: warning sign, the full list in a scrollable box, a red primary button.
                using (var dlg = new AlertForm(AlertIcon.Danger, "Delete 30 items permanently?", "Test", null,
                    Enumerable.Range(1, 30).Select(k => @"C:\x\item" + k + "\t1 KB").ToList(), "Delete permanently", "Cancel"))
                {
                    var primary = dlg.Buttons.FirstOrDefault(x => x.Text == "Delete permanently");
                    check(dlg.Details != null && dlg.Details.LineCount == 30 && primary != null && primary.Kind == ButtonKind.Danger,
                        "prompts: themed dialog with all 30 items in its details box and a red Delete permanently button");
                }

                // Size on disk: every non-empty file takes at least one cluster.
                SetMeasure(true);
                check(root.Size == root.Alloc && root.Alloc >= root.Bytes, string.Format("size on disk {0} >= file size {1}", root.Alloc, root.Bytes));
                SetMeasure(false);

                // Export.
                string html = HtmlReport(root, "test", "");
                check(html.Contains("dup1.bin") && html.Contains("<svg"), "export: HTML report lists files and has the chart");
                string csv = dir + ".csv";
                FoldersCsv(root, csv);
                check(File.ReadAllLines(csv).Length == root.Dirs + 2, "export: CSV has a line per folder");
                File.Delete(csv);

                // History: the scan was saved for comparison.
                for (int i = 0; i < 20 && History.Load(dir, out _when) == null; i++) await Task.Delay(250);
                check(History.Load(dir, out _when) != null, "history: scan saved for the Changes view");

                // Partial delete: a file in use stays; the delete list keeps the folder with what is left (bug in v1.7.2).
                File.WriteAllBytes(P(@"E\x.bin"), new byte[30000]);
                for (int i = 0; i < 40 && (FindScanned(P("E"), true) == null || FindScanned(P("E"), true).LocalFiles != 1); i++) await Task.Delay(250);
                var eNode = FindScanned(P("E"), true);
                basket.Clear();
                AddTarget(new DeleteTarget { Path = eNode.FullPath, Label = "E", IsDir = true, Dir = eNode, Size = 37000 });   // x.bin + e.txt
                using (new FileStream(P(@"E\deep\e.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await ConfirmAndDelete(basket.ToList(), false);
                    AfterDeleteRemeasure(basket.ToList());
                }
                check(basket.Count == 1 && basket[0].Size == 7000 && basket[0].Note.StartsWith("partly deleted") && !File.Exists(P(@"E\x.bin")),
                    "delete list: after a partial delete the entry shows what is left (" + (basket.Count > 0 ? basket[0].Size + " bytes, note: " + basket[0].Note : "none") + ", x.bin " + (File.Exists(P(@"E\x.bin")) ? "still there" : "gone") + ")");
                basket.Clear();
                BasketChanged();

                // Suggestions: tick boxes, mixed state, select all / deselect all.
                var saved = suggestions;
                var sg = new Suggestion { Name = "Test cache", Safe = true, Note = "test" };
                sg.Targets.Add(new DeleteTarget { Path = P("one"), Label = "one", IsDir = true, Size = 100 });
                sg.Targets.Add(new DeleteTarget { Path = P("two"), Label = "two", IsDir = true, Size = 200 });
                suggestions = new List<Suggestion> { sg };
                CheckAllSuggestions(false);
                bool none0 = sg.CheckState == 0 && CheckedSuggestionTargets().Count == 0;
                sg.Targets[1].Checked = true;
                bool mixed = sg.CheckState == 2 && CheckedSuggestionTargets().Sum(x => x.Size) == 200;
                CheckAllSuggestions(true);
                bool all1 = sg.CheckState == 1 && CheckedSuggestionTargets().Count == 2;
                check(none0 && mixed && all1, "suggestions: deselect all, one ticked = mixed, select all");
                suggestions = saved;

                await SelfTestApps(check);

                // Hover text: full name and full path.
                var dupFile = Scanner.ListFiles(FindScanned(P("C"), true)).First(f => f.Name == "dup1.bin");
                string ft = HoverText(ItemInfo.ForFile(dupFile, 0)), dt = HoverText(ItemInfo.ForDir(FindScanned(P("C"), true), 0));
                check(ft == "dup1.bin\n" + P(@"C\dup1.bin") && dt == "C\n" + P("C"), "hover: full name and path for files and folders");
                var bundle = new ItemInfo { Name = "Test cache", Targets = Enumerable.Range(1, 10).Select(k => new DeleteTarget { Path = P("c" + k), IsDir = true }).ToList(), Folder = "10 locations" };
                string bt = HoverText(bundle);
                check(bt.StartsWith("Test cache\n" + P("c1") + "\n" + P("c2")) && bt.EndsWith("... and 2 more"), "hover: a suggestion in several places lists its paths");

                // Suggestions are found again after a change outside the app (v1.7.2 kept the old list).
                suggestions = null;
                viewTabs.SelectedIndex = pages.IndexOf(pSuggest);
                FillSuggestions();
                for (int i = 0; i < 100 && suggestions == null; i++) await Task.Delay(100);
                bool hadNm = suggestions != null && suggestions.Any(s2 => s2.Name == "node_modules folders");
                Directory.Delete(P("P"), true);
                for (int i = 0; i < 40 && FindScanned(P(@"P\node_modules"), true) != null; i++) await Task.Delay(250);
                string diag = string.Format("P scanned: {0}, dirty: {1}, watchers {2}, missed {3}, queued {4}, raising {5}", FindScanned(P("P"), true) != null, suggestionsDirty, watchers.Count, missedChanges.Count, fsChanges.Count, watchers.Values.All(w => w.EnableRaisingEvents));
                var before2 = suggestions;
                FillSuggestions();
                for (int i = 0; i < 100 && suggestions == before2; i++) await Task.Delay(100);
                check(hadNm && suggestions != before2 && !suggestions.Any(s2 => s2.Name == "node_modules folders"),
                    "suggestions: found again after an outside delete (node_modules gone)" + string.Format(" [before {0}, new list {1}, still there {2}, {3}]",
                        hadNm, suggestions != before2, suggestions != null && suggestions.Any(s2 => s2.Name == "node_modules folders"), diag));
                viewTabs.SelectedIndex = pages.IndexOf(pContents);
            }
            catch (Exception ex) { check(false, "exception: " + ex); }
            try { File.WriteAllText(dir + ".log", log.ToString()); } catch (Exception) { }
            try { Console.Write(log.ToString()); } catch (Exception) { }
            Close();
        }

        DateTime _when;

        /// <summary>Apps tab: a fake per-user app (uninstall entry, program folder, leftovers in AppData and the registry).</summary>
        async Task SelfTestApps(Action<bool, string> check)
        {
            string dir = SelfTestDir;
            string appDir = Path.Combine(dir, @"Prog\DSA SelfTest App");
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSA SelfTest App");
            string roam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSA SelfTest App");
            string ukey = UninstallPath + @"\DSA-SelfTest-App";
            string backup = null;
            try
            {
                Directory.CreateDirectory(appDir);
                File.WriteAllBytes(Path.Combine(appDir, "app.exe"), new byte[50000]);
                Directory.CreateDirectory(local);
                File.WriteAllBytes(Path.Combine(local, "settings.ini"), new byte[20000]);
                Directory.CreateDirectory(roam);
                File.WriteAllBytes(Path.Combine(roam, "x.dat"), new byte[10000]);
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\DSA SelfTest App")) k.SetValue("Last", "1");
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ukey))
                {
                    k.SetValue("DisplayName", "DSA SelfTest App");
                    k.SetValue("DisplayVersion", "1.0");
                    k.SetValue("Publisher", "DSA Test");
                    k.SetValue("InstallLocation", appDir);
                    k.SetValue("DisplayIcon", "\"" + Application.ExecutablePath + "\",0");
                    k.SetValue("UninstallString", "cmd.exe /c rmdir /s /q \"" + appDir + "\" & reg delete \"HKCU\\" + ukey + "\" /f");
                }

                AppsTestMode = true;
                apps = null;
                viewTabs.SelectedIndex = pages.IndexOf(pApps);
                FillApps();
                for (int i = 0; i < 100 && (apps == null || appsLoading); i++) await Task.Delay(100);
                var app = apps != null ? apps.FirstOrDefault(x => x.Name == "DSA SelfTest App") : null;
                check(app != null && app.Version == "1.0" && app.Publisher == "DSA Test" && app.Size >= 50000 && SamePath(app.Location, appDir),
                    "apps: the installed app is listed with version, publisher, size and folder" + (app != null ? " (" + app.Size + " bytes)" : ""));
                if (app == null) return;
                check(app.Icon != null && app.Icon.Width == Theme.S(16), "apps: the app's own logo is read from its DisplayIcon" +
                    (app.Icon != null ? " (" + app.Icon.Width + "x" + app.Icon.Height + ")" : " (none)"));

                // Expanding shows everything that belongs to the app, without changing anything. A bigger app stays above it
                // (expanding never moves an app), and a line shows right away while its files are being looked for.
                var big = new InstalledApp { Name = "DSA Bigger App", Size = 1L << 40, Publisher = "", Version = "" };
                apps.Add(big);
                ShowApps();
                Func<List<string>> heads = () => pApps.List.Items.Cast<ListViewItem>().Select(r => (ItemInfo)r.Tag).Where(r => r.GroupOf == null).Select(r => r.Name).ToList();
                ExpandApp(app, true);
                var rowNames = pApps.List.Items.Cast<ListViewItem>().Select(r => ((ItemInfo)r.Tag).Name).ToList();
                check(rowNames.Contains("Looking for its files and settings..."), "apps: expanding shows \"Looking for its files and settings...\" at once");
                for (int i = 0; i < 50 && app.Leftovers == null; i++) await Task.Delay(100);
                var order = heads();
                check(order.IndexOf("DSA Bigger App") == 0 && order.IndexOf("DSA SelfTest App") == 1, "apps: the expanded app keeps its place (" + string.Join(", ", order) + ")");
                ExpandApp(big, true);
                for (int i = 0; i < 50 && big.Leftovers == null; i++) await Task.Delay(100);
                rowNames = pApps.List.Items.Cast<ListViewItem>().Select(r => ((ItemInfo)r.Tag).Name).ToList();
                check(big.Expanded && rowNames.IndexOf("No files or settings found.") == 1, "apps: an app with nothing found shows \"No files or settings found.\" under it");
                apps.Remove(big);
                ShowApps();
                var names = app.Leftovers != null ? string.Join(", ", app.Leftovers.Select(l => l.Kind + ": " + l.Label)) : "none";
                check(app.Leftovers != null && app.Leftovers.Count == 4 && SamePath(app.Leftovers[0].Path, appDir) &&
                      app.Leftovers.Any(l => SamePath(l.Path, local)) && app.Leftovers.Any(l => SamePath(l.Path, roam)) &&
                      app.Leftovers.Any(l => l.IsRegistry && l.SubKey == @"Software\DSA SelfTest App") && Directory.Exists(appDir),
                    "apps: expanding lists all its items: program folder, AppData folders, registry key (" + names + ")");

                // Uninstall: the official uninstaller removes the program folder; the rest is checked again and deleted (confirmed).
                await UninstallApp(app);
                backup = Directory.Exists(BackupFolder) ? Directory.GetFiles(BackupFolder, "DSA SelfTest App*.reg").OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;
                bool keyGone;
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\DSA SelfTest App")) keyGone = k == null;
                check(!Directory.Exists(appDir) && !Directory.Exists(local) && !Directory.Exists(roam) && keyGone && !apps.Contains(app),
                    "apps: after its uninstaller, the remaining 3 items were found again and deleted; the app leaves the list");
                check(backup != null && File.Exists(backup) && File.ReadAllText(backup, System.Text.Encoding.Unicode).Contains("DSA SelfTest App"),
                    "apps: the registry key was backed up to a .reg file first");
            }
            catch (Exception ex) { check(false, "apps: exception " + ex.Message); }
            finally
            {
                AppsTestMode = false;
                apps = null;
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(ukey, false); } catch (Exception) { }
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\DSA SelfTest App", false); } catch (Exception) { }
                foreach (var d in new[] { local, roam }) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception) { }
                try { if (backup != null) File.Delete(backup); } catch (Exception) { }
                viewTabs.SelectedIndex = pages.IndexOf(pContents);
            }
        }
    }

    /// <summary>Builds the self-test folder: 10 files in 8 folders, two identical files, a project (package.json) with node_modules.</summary>
    internal static class SelfTest
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool CreateDirectoryW(string path, IntPtr security);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        // .NET Framework's path checks stop at 260 characters; the Win32 calls with \\?\ do not.
        public static void MakeDirLong(string path) { CreateDirectoryW(Native.LongPath(path), IntPtr.Zero); }

        public static void MakeFileLong(string path)
        {
            using (var h = CreateFileW(Native.LongPath(path), 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero))
                if (h.IsInvalid) throw new IOException("cannot create " + path);
        }

        public static string Prepare(string dir)
        {
            dir = Path.GetFullPath(dir);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var rnd = new Random(1);
            Action<string, int> make = (rel, size) =>
            {
                string p = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                var data = new byte[size];
                rnd.NextBytes(data);
                File.WriteAllBytes(p, data);
            };
            make(@"A\a1.txt", 1000);
            make(@"A\a2.txt", 2000);
            make(@"A\sub\a3.txt", 3000);
            make(@"B\b1.bin", 50000);
            make(@"B\b2.bin", 60000);
            make(@"C\dup1.bin", 200000);
            Directory.CreateDirectory(Path.Combine(dir, "D"));
            File.Copy(Path.Combine(dir, @"C\dup1.bin"), Path.Combine(dir, @"D\dup2.bin"));
            make(@"C\other.bin", 200000);
            make(@"P\package.json", 100);
            make(@"P\node_modules\x\index.js", 4000);
            return dir;
        }
    }
}
