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
                await Task.Delay(1500);
                if (ShotDemo) ClearHover();
                SaveShot("1-contents");
                for (int i = 1; i < pages.Count; i++)
                {
                    var p = pages[i];
                    if (ShotDemo && (p == pSuggest || p == pChanges)) continue;   // they would show this computer
                    if (p == pBasket)
                        foreach (var it in pFiles.List.Items.Cast<ListViewItem>().Select(x => x.Tag as ItemInfo).Where(x => x != null && x.CanDelete).Take(4))
                            foreach (var t in DeleteTarget.From(it)) AddTarget(t);
                    viewTabs.SelectedIndex = i;
                    if (p == pDupes && !(ShotDemo && dupeGroups != null))
                    {
                        FindDuplicates();
                        for (int k = 0; k < 600 && dupeFinder != null; k++) await Task.Delay(200);
                    }
                    if (p == pSuggest) for (int k = 0; k < 300 && suggestions == null; k++) await Task.Delay(200);
                    await Task.Delay(2000);
                    if (ShotDemo) ClearHover();
                    SaveShot((i + 1) + "-" + p.Title.Split(' ')[0].ToLowerInvariant());
                }
                viewTabs.SelectedIndex = 0;
                measure.SelectedIndex = 1;
                await Task.Delay(1500);
                SaveShot((pages.Count + 1) + "-size-on-disk");
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
                check(top != null && top.Files == 9 && top.Dirs == 8, string.Format("scan: {0} files, {1} folders (expected 9, 8)", top != null ? top.Files : -1, top != null ? top.Dirs : -1));

                // Live updates: a new file and a new folder appear without a rescan.
                File.WriteAllBytes(P(@"A\new.txt"), new byte[5000]);
                Directory.CreateDirectory(P(@"E\deep"));
                File.WriteAllBytes(P(@"E\deep\e.txt"), new byte[7000]);
                for (int i = 0; i < 40 && (FindScanned(P("E"), true) == null || FindScanned(P("A"), true).LocalFiles != 3); i++) await Task.Delay(250);
                var a = FindScanned(P("A"), true);
                var e = FindScanned(P(@"E\deep"), true);
                check(a != null && a.LocalFiles == 3, "live: new file in A noticed");
                check(e != null && e.Files == 1 && root.Files == 11, "live: new folder E\\deep with its file noticed (total " + root.Files + " files)");

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

                // Contents only: A stays, its files and subfolder go.
                var aNode = FindScanned(P("A"), true);
                job = await ConfirmAndDelete(new List<DeleteTarget> { new DeleteTarget { Path = aNode.FullPath, IsDir = true, ContentsOnly = true, Dir = aNode, Label = "A" } }, false);
                aNode = FindScanned(P("A"), true);
                check(Directory.Exists(P("A")) && Directory.GetFileSystemEntries(P("A")).Length == 0 && aNode != null && aNode.Files == 0 && aNode.Dirs == 0,
                    "contents only: A kept and emptied, in the scan too");

                // A single file from the index.
                var c = FindScanned(P("C"), true);
                var other = Scanner.ListFiles(c).First(f => f.Name == "other.bin");
                job = await ConfirmAndDelete(new List<DeleteTarget> { new DeleteTarget { Path = other.FullPath, File = other, Label = other.Name, Size = other.Size } }, false);
                check(!File.Exists(P(@"C\other.bin")) && c.FileList.All(f => f.Name != "other.bin") && c.LocalFiles == 1, "file delete: other.bin gone from disk and index");
                check(root.Files == 4, "totals after deletes: " + root.Files + " files (expected 4)");

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
            }
            catch (Exception ex) { check(false, "exception: " + ex); }
            try { File.WriteAllText(dir + ".log", log.ToString()); } catch (Exception) { }
            try { Console.Write(log.ToString()); } catch (Exception) { }
            Close();
        }

        DateTime _when;
    }

    /// <summary>Builds the self-test folder: 9 files in 8 folders, two identical files, a node_modules folder.</summary>
    internal static class SelfTest
    {
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
            make(@"P\node_modules\x\index.js", 4000);
            return dir;
        }
    }
}
