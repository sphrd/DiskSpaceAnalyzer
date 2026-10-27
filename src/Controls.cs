using System;
using System.Collections;
using System.Drawing;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    internal static class Fmt
    {
        static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
            return v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "0") + " " + Units[u];
        }

        public static string Date(long fileTime)
        {
            if (fileTime <= 0) return "";
            try { return DateTime.FromFileTime(fileTime).ToString("yyyy-MM-dd HH:mm"); }
            catch (ArgumentException) { return ""; }
        }

        public static string Shorten(string s, int max)
        {
            if (s == null || s.Length <= max) return s;
            int half = (max - 3) / 2;
            return s.Substring(0, half) + "..." + s.Substring(s.Length - half);
        }
    }

    /// <summary>One row in a list view: either a folder (Dir) or a file (File).</summary>
    internal sealed class ItemInfo
    {
        public bool IsDir, IsSummary;
        public DirNode Dir;
        public FileEntry File;
        public string Name, Folder;
        public long Size, Files, Dirs, Modified;
        public double Percent;

        public string FullPath { get { return IsDir ? Dir.FullPath : File.FullPath; } }

        public static ItemInfo ForDir(DirNode d, long parentSize)
        {
            return new ItemInfo
            {
                IsDir = true, Dir = d, Name = d.Name, Size = d.Size, Files = d.Files, Dirs = d.Dirs,
                Percent = parentSize > 0 ? 100.0 * d.Size / parentSize : 0
            };
        }

        public static ItemInfo ForFile(FileEntry f, long parentSize)
        {
            return new ItemInfo
            {
                File = f, Name = f.Name, Size = f.Size, Files = 1, Modified = f.Modified,
                Percent = parentSize > 0 ? 100.0 * f.Size / parentSize : 0
            };
        }
    }

    internal sealed class ItemComparer : IComparer
    {
        public string Key = "size";
        public bool Ascending;

        public int Compare(object x, object y)
        {
            return Compare((ItemInfo)((ListViewItem)x).Tag, (ItemInfo)((ListViewItem)y).Tag);
        }

        public int Compare(ItemInfo a, ItemInfo b)
        {
            if (a.IsSummary != b.IsSummary) return a.IsSummary ? 1 : -1;
            int r;
            switch (Key)
            {
                case "name": r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case "folder": r = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase); break;
                case "files": r = a.Files.CompareTo(b.Files); break;
                case "dirs": r = a.Dirs.CompareTo(b.Dirs); break;
                case "mod": r = a.Modified.CompareTo(b.Modified); break;
                default: r = a.Size.CompareTo(b.Size); break;
            }
            if (r == 0 && Key != "size") r = b.Size.CompareTo(a.Size);
            return Ascending ? r : -r;
        }
    }

    /// <summary>Details list view that draws a percentage bar in one column.</summary>
    internal sealed class SizeListView : ListView
    {
        public int BarColumn = -1;
        public string[] Keys;
        public string[] Titles;
        public readonly ItemComparer Sorter = new ItemComparer();
        int sortColumn = 1;
        static readonly Color BarColor = Color.FromArgb(58, 125, 214);
        static readonly Color BarBack = Color.FromArgb(228, 232, 238);

        public SizeListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            OwnerDraw = true;
            AllowColumnReorder = true;
        }

        public void SetColumns(string[] titles, string[] keys, int[] widths, float scale)
        {
            Titles = titles;
            Keys = keys;
            Columns.Clear();
            for (int i = 0; i < titles.Length; i++)
            {
                bool right = keys[i] == "size" || keys[i] == "files" || keys[i] == "dirs";
                Columns.Add(titles[i], (int)(widths[i] * scale), right ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            }
            UpdateHeaders();
        }

        public void SortBy(int column)
        {
            string key = Keys[column];
            sortColumn = column;
            if (Sorter.Key == key) Sorter.Ascending = !Sorter.Ascending;
            else { Sorter.Key = key; Sorter.Ascending = key == "name" || key == "folder"; }
            BeginUpdate();
            ListViewItemSorter = Sorter;
            ListViewItemSorter = null;
            EndUpdate();
            UpdateHeaders();
        }

        void UpdateHeaders()
        {
            for (int i = 0; i < Columns.Count; i++)
                Columns[i].Text = Titles[i] + (i == sortColumn ? (Sorter.Ascending ? "  \u25B2" : "  \u25BC") : "");
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = true; }

        protected override void OnDrawItem(DrawListViewItemEventArgs e) { }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != BarColumn) { e.DrawDefault = true; return; }
            var info = e.Item.Tag as ItemInfo;
            double pct = info != null ? info.Percent : 0;
            var g = e.Graphics;
            var r = e.Bounds;
            bool sel = e.Item.Selected;
            Color back = sel ? (Focused ? SystemColors.Highlight : SystemColors.Control) : BackColor;
            using (var b = new SolidBrush(back)) g.FillRectangle(b, r);

            int pad = Math.Max(2, r.Height / 5);
            int textW = TextRenderer.MeasureText("100.0 %", Font).Width;
            var bar = new Rectangle(r.X + pad, r.Y + pad, Math.Max(0, r.Width - textW - 3 * pad), r.Height - 2 * pad);
            if (bar.Width > 4 && info != null && !info.IsSummary)
            {
                using (var b = new SolidBrush(BarBack)) g.FillRectangle(b, bar);
                int w = (int)Math.Round(bar.Width * Math.Min(100, Math.Max(0, pct)) / 100);
                if (w > 0) using (var b = new SolidBrush(BarColor)) g.FillRectangle(b, bar.X, bar.Y, w, bar.Height);
            }
            var tr = new Rectangle(r.Right - textW - pad, r.Y, textW, r.Height);
            Color fore = sel && Focused ? SystemColors.HighlightText : e.Item.ForeColor;
            TextRenderer.DrawText(g, e.SubItem.Text, Font, tr, fore,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
