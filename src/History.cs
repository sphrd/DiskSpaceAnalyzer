using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>Folder sizes of an earlier scan (folders only, no file names), kept to show what changed since.</summary>
    internal sealed class PrevNode
    {
        public string Name;
        public long Bytes, Alloc, Files;
        public Dictionary<string, PrevNode> Children;

        public long Size { get { return DirNode.OnDisk ? Alloc : Bytes; } }

        public PrevNode Child(string name)
        {
            PrevNode c;
            return Children != null && Children.TryGetValue(name, out c) ? c : null;
        }
    }

    /// <summary>
    /// Saves a compact copy of every full scan (folder names and sizes, gzip) in %LOCALAPPDATA%\DiskSpaceAnalyzer\history,
    /// one file per scanned drive or folder, and loads the previous one for comparison. Nothing leaves the computer.
    /// </summary>
    internal static class History
    {
        const int Magic = 0x31415344;   // "DSA1"

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSpaceAnalyzer", "history"); }
        }

        static string FileFor(string rootPath)
        {
            var sb = new StringBuilder();
            foreach (char c in rootPath.TrimEnd('\\').ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return Path.Combine(Folder, sb + ".dsa");
        }

        public static PrevNode Load(string rootPath, out DateTime when)
        {
            when = DateTime.MinValue;
            string f = FileFor(rootPath);
            if (!File.Exists(f)) return null;
            try
            {
                using (var fs = File.OpenRead(f))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(gz, Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return null;
                    when = DateTime.FromFileTimeUtc(r.ReadInt64()).ToLocalTime();
                    r.ReadString();
                    return ReadNode(r);
                }
            }
            catch (Exception) { return null; }
        }

        static PrevNode ReadNode(BinaryReader r)
        {
            // Iterative: very deep trees would overflow the stack.
            var root = new PrevNode();
            var stack = new Stack<KeyValuePair<PrevNode, int>>();
            int children = Fill(r, root);
            stack.Push(new KeyValuePair<PrevNode, int>(root, children));
            while (stack.Count > 0)
            {
                var top = stack.Pop();
                if (top.Value == 0) continue;
                stack.Push(new KeyValuePair<PrevNode, int>(top.Key, top.Value - 1));
                var c = new PrevNode();
                int cc = Fill(r, c);
                if (top.Key.Children == null) top.Key.Children = new Dictionary<string, PrevNode>(StringComparer.OrdinalIgnoreCase);
                top.Key.Children[c.Name] = c;
                stack.Push(new KeyValuePair<PrevNode, int>(c, cc));
            }
            return root;
        }

        static int Fill(BinaryReader r, PrevNode n)
        {
            n.Name = r.ReadString();
            n.Bytes = r.ReadInt64();
            n.Alloc = r.ReadInt64();
            n.Files = r.ReadInt64();
            return r.ReadInt32();
        }

        public static void Save(string rootPath, DirNode root)
        {
            Directory.CreateDirectory(Folder);
            string f = FileFor(rootPath), tmp = f + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(gz, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(DateTime.UtcNow.ToFileTimeUtc());
                w.Write(rootPath);
                foreach (var d in root.Descendants())   // pre-order, children in list order
                {
                    w.Write(d.Name ?? "");
                    w.Write(d.Bytes);
                    w.Write(d.Alloc);
                    w.Write(d.Files);
                    w.Write(d.Children != null ? d.Children.Count : 0);
                }
            }
            if (File.Exists(f)) File.Delete(f);
            File.Move(tmp, f);
        }
    }

    internal sealed partial class MainForm
    {
        Page BuildChangesPage()
        {
            var list = NewList(new[] { "Folder", "Change", "Now", "Before" },
                new[] { "name", "change", "size", "files" }, new[] { 380, 100, 90, 90 }, -1);
            var p = AddPage("Changes", list, null);
            p.ChartTitle = "Growth";
            p.ChartScope = "of the growth";
            p.Empty = "Nothing grew since the previous scan.";
            p.Fill = FillChanges;
            return p;
        }

        /// <summary>After a full scan: keep the previous scan for comparison and save this one (in the background).</summary>
        void RememberScan(ScanRoot r, DirNode result, bool complete)
        {
            if (!complete) return;
            string path = r.Path;
            Task.Factory.StartNew(() =>
            {
                DateTime when;
                var prev = History.Load(path, out when);
                try { History.Save(path, result); } catch (Exception) { }
                return new KeyValuePair<PrevNode, DateTime>(prev, when);
            }).ContinueWith(t =>
            {
                if (IsDisposed || t.IsFaulted) return;
                if (r.Previous == null || t.Result.Key != null) { r.Previous = t.Result.Key; r.PreviousTime = t.Result.Value; }
                if (current != null && current.Top == r.Node && ActivePage == pContents) ShowContents(current);
                pChanges.Stale = true;
                if (ActivePage == pChanges) { pChanges.Stale = false; FillChanges(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>The previous scan's node for a scanned folder (null when unknown).</summary>
        PrevNode PrevOf(DirNode d)
        {
            var r = d != null ? RootOf(d.Top) : null;
            if (r == null || r.Previous == null) return null;
            var chain = new List<DirNode>();
            for (var n = d; n.Parent != null; n = n.Parent) chain.Add(n);
            var p = r.Previous;
            for (int i = chain.Count - 1; i >= 0 && p != null; i--) p = p.Child(chain[i].Name);
            return p;
        }

        /// <summary>Change of each child folder of d since the previous scan, for the Change column.</summary>
        void SetChanges(DirNode d, List<ItemInfo> items)
        {
            var prev = PrevOf(d);
            if (prev == null) return;
            foreach (var it in items)
            {
                if (!it.IsDir) continue;
                var pc = prev.Child(it.Dir.Name);
                it.Change = pc != null ? it.Dir.Size - pc.Size : it.Dir.Size;
                if (pc == null) it.Note = "new";
            }
        }

        static string ChangeText(ItemInfo it)
        {
            if (it.Change == long.MinValue) return "";
            if (it.Note == "new") return "new";
            if (it.Change == 0) return "-";
            return (it.Change > 0 ? "+" : "-") + Fmt.Size(Math.Abs(it.Change));
        }

        string ChangeCaption(DirNode d)
        {
            var r = RootOf(d.Top);
            var prev = PrevOf(d);
            if (r == null || prev == null) return "";
            long delta = d.Size - prev.Size;
            if (delta == 0) return string.Format("  -  no change since the scan of {0:yyyy-MM-dd HH:mm}", r.PreviousTime);
            return string.Format("  -  {0}{1} since the scan of {2:yyyy-MM-dd HH:mm}", delta >= 0 ? "+" : "-", Fmt.Size(Math.Abs(delta)), r.PreviousTime);
        }

        void FillChanges()
        {
            var p = pChanges;
            var r = active;
            p.ChartTitle = "Growth";
            if (r == null || r.Node == null || r.Previous == null)
            {
                p.Caption.Text = r == null || r.Node == null ? "Nothing scanned yet."
                    : "No earlier scan of " + r.Path + " to compare with yet. Every full scan is remembered; scan again later to see what changed.";
                p.ChartSub = "";
                p.Empty = "No earlier scan to compare with.";
                SetRows(p.List, new List<ListViewItem>(), "No earlier scan of this drive or folder to compare with yet.\nScan it again later to see which folders grew or shrank.");
                if (ActivePage == p) RefreshChart();
                return;
            }
            // A folder is listed when its change is big enough and not mostly explained by one of its subfolders.
            long threshold = Math.Max(10L << 20, r.Node.Size / 1000);
            var found = new List<KeyValuePair<DirNode, PrevNode>>();
            var removed = new List<KeyValuePair<string, PrevNode>>();
            var stack = new Stack<KeyValuePair<DirNode, PrevNode>>();
            stack.Push(new KeyValuePair<DirNode, PrevNode>(r.Node, r.Previous));
            while (stack.Count > 0)
            {
                var kv = stack.Pop();
                DirNode d = kv.Key;
                PrevNode pn = kv.Value;
                long delta = d.Size - (pn != null ? pn.Size : 0);
                if (Math.Abs(delta) < threshold) continue;
                bool explained = false;
                if (d.Children != null)
                    foreach (var c in d.Children)
                    {
                        var pc = pn != null ? pn.Child(c.Name) : null;
                        long cd = c.Size - (pc != null ? pc.Size : 0);
                        if (Math.Abs(cd) >= threshold) stack.Push(new KeyValuePair<DirNode, PrevNode>(c, pc));
                        if (Math.Abs(cd) >= Math.Abs(delta) * 0.8 && Math.Sign(cd) == Math.Sign(delta)) explained = true;
                    }
                if (pn != null && pn.Children != null)
                    foreach (var pc in pn.Children.Values)
                        if (d.Child(pc.Name) == null && pc.Size >= threshold)
                        {
                            removed.Add(new KeyValuePair<string, PrevNode>(Native.Join(d.FullPath, pc.Name), pc));
                            if (pc.Size >= Math.Abs(delta) * 0.8 && delta < 0) explained = true;
                        }
                if (!explained) found.Add(kv);
            }
            var rows = new List<ListViewItem>();
            long grew = 0, shrank = 0;
            foreach (var kv in found.OrderByDescending(x => Math.Abs(x.Key.Size - (x.Value != null ? x.Value.Size : 0))).Take(500))
            {
                long before = kv.Value != null ? kv.Value.Size : 0, delta = kv.Key.Size - before;
                var it = ItemInfo.ForDir(kv.Key, 0);
                it.Name = kv.Key.FullPath;
                it.Change = delta;
                it.Files = before;
                it.ChartValue = Math.Max(0, delta);
                if (kv.Value == null) it.Note = "new";
                if (delta > 0) grew += delta; else shrank -= delta;
                rows.Add(Row(it, it.Name, ChangeText(it), Fmt.Size(kv.Key.Size), kv.Value != null ? Fmt.Size(before) : "new"));
            }
            foreach (var kv in removed.OrderByDescending(x => x.Value.Size).Take(100))
            {
                var it = new ItemInfo { Name = kv.Key, Change = -kv.Value.Size, Files = kv.Value.Size, FolderIcon = true, ChartValue = 0, IsSummary = false };
                shrank += kv.Value.Size;
                var lvi = Row(it, it.Name, ChangeText(it), "removed", Fmt.Size(kv.Value.Size));
                lvi.ForeColor = Theme.MUTED;
                rows.Add(lvi);
            }
            rows.Sort((a, b) => { var x = (ItemInfo)a.Tag; var y = (ItemInfo)b.Tag; return Math.Abs(y.Change).CompareTo(Math.Abs(x.Change)); });
            long total = r.Node.Size - r.Previous.Size;
            SetRows(p.List, rows, "No folder changed by more than " + Fmt.Size(threshold) + " since the previous scan.");
            p.Caption.Text = string.Format("Since the scan of {0:yyyy-MM-dd HH:mm}: {1}{2} in total  -  folders that changed by {3} or more (grew {4}, shrank {5}).",
                r.PreviousTime, total >= 0 ? "+" : "-", Fmt.Size(Math.Abs(total)), Fmt.Size(threshold), Fmt.Size(grew), Fmt.Size(shrank));
            p.ChartSub = string.Format("+{0} since {1:yyyy-MM-dd}", Fmt.Size(grew), r.PreviousTime);
            p.ChartTotal = 0;
            p.Empty = "Nothing grew since the previous scan.";
            if (ActivePage == p) RefreshChart();
        }
    }
}
