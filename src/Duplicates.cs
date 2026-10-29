using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace DiskSpaceAnalyzer
{
    /// <summary>
    /// Finds files with identical content: same size (from the index), then the same hash of the first 64 KB, then the
    /// same hash of the whole file. Several hard links to one file are one file, not duplicates. Online-only cloud files are
    /// skipped (reading them would download them).
    /// </summary>
    internal sealed class DuplicateFinder
    {
        public long BytesToRead, BytesRead, FilesChecked;
        public volatile string Current = "";
        public volatile bool Cancelled;
        public List<List<FileEntry>> Groups = new List<List<FileEntry>>();

        const int HeadSize = 64 * 1024;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint Attributes;
            public long Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

        sealed class Candidate
        {
            public FileEntry File;
            public string Path;
            public ulong Id;           // volume serial and file index: equal for hard links
            public string Head, Full;
        }

        public void Run(DirNode scope, long minSize)
        {
            // 1. Same size
            var bySize = new Dictionary<long, List<FileEntry>>();
            foreach (var d in scope.Descendants())
            {
                if (d.FileList == null) continue;
                for (int i = 0; i < d.FileList.Length; i++)
                {
                    var f = d.FileList[i];
                    if (f.Bytes < Math.Max(1, minSize) || f.OnlineOnly) continue;
                    List<FileEntry> l;
                    if (!bySize.TryGetValue(f.Bytes, out l)) bySize[f.Bytes] = l = new List<FileEntry>();
                    l.Add(FileEntry.From(d, ref d.FileList[i]));
                }
                if (Cancelled) return;
            }
            var sizeGroups = bySize.Values.Where(l => l.Count > 1).ToList();
            BytesToRead = sizeGroups.Sum(g => g.Count * Math.Min(HeadSize, g[0].Bytes));

            // 2. Same first 64 KB (one handle per file also gives its identity, to recognise hard links)
            var headGroups = new List<List<Candidate>>();
            var opts = new ParallelOptions { MaxDegreeOfParallelism = 3 };
            foreach (var g in sizeGroups)
            {
                if (Cancelled) return;
                var cands = g.Select(f => new Candidate { File = f, Path = f.FullPath }).ToList();
                Parallel.ForEach(cands, opts, c => { if (!Cancelled) c.Head = Hash(c, HeadSize, true); });
                var distinct = cands.Where(c => c.Head != null).GroupBy(c => c.Id).Select(x => x.First()).ToList();
                foreach (var hg in distinct.GroupBy(c => c.Head))
                    if (hg.Count() > 1) headGroups.Add(hg.ToList());
            }

            // 3. Same whole content (files no larger than 64 KB are already fully hashed)
            BytesToRead = BytesRead + headGroups.Sum(g => g[0].File.Bytes > HeadSize ? g.Count * g[0].File.Bytes : 0);
            var result = new List<List<FileEntry>>();
            foreach (var g in headGroups)
            {
                if (Cancelled) return;
                if (g[0].File.Bytes > HeadSize)
                    Parallel.ForEach(g, opts, c => { if (!Cancelled) c.Full = Hash(c, long.MaxValue, false); });
                else foreach (var c in g) c.Full = c.Head;
                foreach (var fg in g.Where(c => c.Full != null).GroupBy(c => c.Full))
                    if (fg.Count() > 1) result.Add(fg.Select(c => c.File).OrderByDescending(f => f.Modified).ToList());
            }
            result.Sort((a, b) => ((b.Count - 1) * b[0].Bytes).CompareTo((a.Count - 1) * a[0].Bytes));
            Groups = result;
        }

        string Hash(Candidate c, long limit, bool identify)
        {
            Current = c.Path;
            try
            {
                using (var h = CreateFileW(Native.LongPath(c.Path), 0x80000000, 7, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero))   // read, share all, sequential
                {
                    if (h.IsInvalid) return null;
                    if (identify)
                    {
                        BY_HANDLE_FILE_INFORMATION info;
                        if (GetFileInformationByHandle(h, out info))
                            c.Id = ((ulong)info.VolumeSerial << 48) ^ ((ulong)info.IndexHigh << 32) ^ info.IndexLow;
                        else c.Id = (ulong)c.Path.GetHashCode();
                    }
                    using (var fs = new System.IO.FileStream(h, System.IO.FileAccess.Read, 1 << 16))
                    using (var md5 = MD5.Create())
                    {
                        var buf = new byte[1 << 20];
                        long left = limit;
                        int n;
                        while (left > 0 && (n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
                        {
                            if (Cancelled) return null;
                            md5.TransformBlock(buf, 0, n, null, 0);
                            left -= n;
                            Interlocked.Add(ref BytesRead, n);
                        }
                        md5.TransformFinalBlock(buf, 0, 0);
                        Interlocked.Increment(ref FilesChecked);
                        return Convert.ToBase64String(md5.Hash);
                    }
                }
            }
            catch (Exception) { return null; }
        }
    }

    internal sealed partial class MainForm
    {
        RoundButton btnFindDupes, btnSelectExtra;
        ChoiceBox dupeMin;
        DuplicateFinder dupeFinder;
        List<List<FileEntry>> dupeGroups;
        string dupeScope = "";
        long dupeMinSize;
        System.Windows.Forms.Timer dupeTimer;
        static readonly long[] DupeSizes = { 1L << 20, 10L << 20, 100L << 20, 1 };

        Page BuildDuplicatesPage()
        {
            btnFindDupes = Btn("Find duplicates", ButtonKind.Primary, "Compare the files in the selected folder by content", FindDuplicates);
            dupeMin = new ChoiceBox("", "Files of 1 MB or more", "Files of 10 MB or more", "Files of 100 MB or more", "All files");
            btnSelectExtra = Btn("Select extra copies", ButtonKind.Secondary,
                "Select every copy except the newest one of each group; then add them to the delete list or delete them", SelectExtraCopies);
            btnSelectExtra.Tag = "right";
            var list = NewList(new[] { "Name", "Size", "Can free", "Folder", "Modified" },
                new[] { "name", "size", "files", "folder", "mod" }, new[] { 280, 90, 100, 380, 140 }, -1);
            var p = AddPage("Duplicates", list, MakeBar(btnFindDupes, dupeMin, btnSelectExtra));
            p.ChartTitle = "Duplicates";
            p.ChartScope = "of the duplicates";
            p.Empty = "Press Find duplicates to compare the files in the selected folder.";
            p.Fill = FillDuplicates;
            dupeTimer = new System.Windows.Forms.Timer { Interval = 200 };
            dupeTimer.Tick += (s, e) =>
            {
                var f = dupeFinder;
                if (f == null) return;
                double pct = f.BytesToRead > 0 ? 100.0 * Interlocked.Read(ref f.BytesRead) / f.BytesToRead : 0;
                pDupes.Caption.Text = string.Format("Comparing files in {0}...  {1:0} %  ({2:N0} files read)  {3}", Fmt.Shorten(dupeScope, 50),
                    Math.Min(99, pct), Interlocked.Read(ref f.FilesChecked), Fmt.Shorten(f.Current, 60));
            };
            return p;
        }

        async void FindDuplicates()
        {
            if (dupeFinder != null) { dupeFinder.Cancelled = true; return; }
            var scope = Scope();
            if (scope == null) { pDupes.Caption.Text = "Scan a drive or folder first."; return; }
            var finder = new DuplicateFinder();
            dupeFinder = finder;
            dupeScope = scope.FullPath;
            dupeMinSize = DupeSizes[dupeMin.SelectedIndex];
            btnFindDupes.Text = "Stop";
            btnFindDupes.Kind = ButtonKind.Secondary;
            dupeMin.Enabled = false;
            LayoutBar(btnFindDupes);
            dupeTimer.Start();
            long min = dupeMinSize;
            try { await RunThread(() => finder.Run(scope, min)); }
            catch (Exception) { }
            dupeTimer.Stop();
            dupeFinder = null;
            btnFindDupes.Text = "Find duplicates";
            btnFindDupes.Kind = ButtonKind.Primary;
            dupeMin.Enabled = true;
            LayoutBar(btnFindDupes);
            if (finder.Cancelled) { pDupes.Caption.Text = "Stopped."; return; }
            dupeGroups = finder.Groups;
            FillDuplicates();
        }

        static void LayoutBar(Control c) { if (c.Parent != null) c.Parent.PerformLayout(); c.Invalidate(); }

        /// <summary>True while the file is still in the scanned index (not deleted since).</summary>
        bool StillIndexed(FileEntry f)
        {
            if (!IsAttached(f.Dir) || f.Dir.FileList == null) return false;
            foreach (var r in f.Dir.FileList) if (string.Equals(r.Name, f.Name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        void FillDuplicates()
        {
            var p = pDupes;
            p.ChartTitle = "Duplicates";
            if (dupeGroups == null)
            {
                p.Caption.Text = dupeFinder != null ? p.Caption.Text : "Find files with the same content in the selected folder (" +
                    (Scope() != null ? Fmt.Shorten(Scope().FullPath, 70) : "nothing scanned yet") + ").";
                SetRows(p.List, new List<ListViewItem>(), "Press Find duplicates to compare the files in the selected folder by content.\nHard links and online-only cloud files are not counted.");
                p.ChartSub = "";
                if (ActivePage == p) RefreshChart();
                return;
            }
            foreach (var g in dupeGroups) g.RemoveAll(f => !StillIndexed(f));
            dupeGroups.RemoveAll(g => g.Count < 2);
            var rows = new List<ListViewItem>();
            long free = 0;
            foreach (var g in dupeGroups)
            {
                long each = g[0].Bytes, wasted = each * (g.Count - 1);
                free += wasted;
                var head = new ItemInfo
                {
                    IsGroup = true, Name = string.Format("{0} copies of {1}", g.Count, g[0].Name), Size = each, ChartValue = wasted,
                    Folder = g.Select(f => f.Dir.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? "same folder" : ""
                };
                rows.Add(Row(head, head.Name, Fmt.Size(each), Fmt.Size(wasted), head.Folder, ""));
                foreach (var f in g)
                {
                    var it = ItemInfo.ForFile(f, 0);
                    it.Folder = f.Dir.FullPath;
                    it.GroupOf = head;
                    rows.Add(Row(it, it.Name, Fmt.Size(f.Bytes), "", it.Folder, Fmt.Date(f.Modified)));
                }
            }
            SetRows(p.List, rows, "No duplicates found in " + dupeScope + ".");
            p.Caption.Text = dupeGroups.Count == 0 ? "No duplicates found in " + Fmt.Shorten(dupeScope, 80) + "."
                : string.Format("{0:N0} groups of identical files in {1}  -  {2} can be freed by keeping one copy of each. Newest copy first.",
                    dupeGroups.Count, Fmt.Shorten(dupeScope, 60), Fmt.Size(free));
            p.ChartSub = dupeGroups.Count > 0 ? Fmt.Size(free) + " in extra copies" : "";
            p.ChartTotal = 0;
            p.Empty = "No duplicates found.";
            if (ActivePage == p) RefreshChart();
        }

        void SelectExtraCopies()
        {
            var lv = pDupes.List;
            lv.BeginUpdate();
            lv.SelectedItems.Clear();
            ItemInfo head = null;
            bool first = false;
            foreach (ListViewItem lvi in lv.Items)
            {
                var it = (ItemInfo)lvi.Tag;
                if (it.IsGroup) { head = it; first = true; continue; }
                if (it.GroupOf != head) continue;
                if (first) { first = false; continue; }   // keep the newest copy (listed first)
                lvi.Selected = true;
            }
            lv.EndUpdate();
            lv.Focus();
            lastView = lv;
            statusLabel.Text = string.Format("Selected {0:N0} extra copies ({1}). Add them to the delete list (Ctrl+D) or delete them.",
                lv.SelectedItems.Count, Fmt.Size(lv.SelectedItems.Cast<ListViewItem>().Sum(i => ((ItemInfo)i.Tag).Size)));
        }
    }
}
