using System;
using System.Collections;
using System.Collections.Generic;
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

    /// <summary>
    /// One row in a list view: a folder (Dir), a file (File), a cleanup target (Targets, e.g. a suggestion or a delete-list
    /// entry), a group head (duplicates) or a plain summary row (file types, "N smaller files").
    /// </summary>
    internal sealed class ItemInfo
    {
        public bool IsDir, IsSummary, IsGroup, FolderIcon;
        public DirNode Dir;
        public FileEntry File;
        public List<DeleteTarget> Targets;
        public ItemInfo GroupOf;              // member of a group: shares the group's chart slice
        public string Name, Folder, Path, Ext, Category, Note;
        public long Size, Files, Dirs, Modified;
        public long Change = long.MinValue;   // compared with the previous scan; MinValue = unknown
        public long ChartValue = -1;          // size used by the chart when it differs from Size
        public double Percent;

        public long ChartSize { get { return ChartValue >= 0 ? ChartValue : Size; } }
        public bool CanDelete { get { return !IsSummary && !IsGroup && (IsDir || File != null || Targets != null); } }

        public string FullPath { get { return IsDir ? Dir.FullPath : File != null ? File.FullPath : Path; } }

        public static ItemInfo ForDir(DirNode d, long parentSize)
        {
            return new ItemInfo
            {
                IsDir = true, Dir = d, Name = d.Name, Size = d.Size, Files = d.Files, Dirs = d.Dirs, Modified = d.Modified,
                Percent = parentSize > 0 ? 100.0 * d.Size / parentSize : 0
            };
        }

        public static ItemInfo ForFile(FileEntry f, long parentSize)
        {
            return new ItemInfo
            {
                File = f, Name = f.OnlineOnly ? f.Name + "  (online-only)" : f.Name, Size = f.Size, Files = 1, Modified = f.Modified,
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
                case "change": r = a.Change.CompareTo(b.Change); break;
                case "cat": r = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase); break;
                case "note": r = string.Compare(a.Note, b.Note, StringComparison.OrdinalIgnoreCase); break;
                default: r = a.Size.CompareTo(b.Size); break;
            }
            if (r == 0 && Key != "size") r = b.Size.CompareTo(a.Size);
            return Ascending ? r : -r;
        }
    }

    /// <summary>Details list view drawn in the palette, with a percentage bar column and pill scrollbar support.</summary>
    internal sealed class SizeListView : ListView, IScrollNotify
    {
        public int BarColumn = -1;
        public string[] Keys;
        public string[] Titles;
        public readonly ItemComparer Sorter = new ItemComparer();
        public event EventHandler ScrollChanged;
        int sortColumn = 1;
        HeaderPainter header;
        const TextFormatFlags CellFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

        public SizeListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            OwnerDraw = true;
            AllowColumnReorder = true;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.SURFACE;
            ForeColor = Theme.FG;
            Font = Theme.FONT;
            // Rows are as tall as this (invisible) image list; icons are drawn as glyphs.
            SmallImageList = new ImageList { ImageSize = new Size(1, Theme.S(26)) };
        }

        public void SetColumns(string[] titles, string[] keys, int[] widths)
        {
            Titles = titles;
            Keys = keys;
            Columns.Clear();
            for (int i = 0; i < titles.Length; i++)
            {
                bool right = keys[i] == "size" || keys[i] == "files" || keys[i] == "dirs" || keys[i] == "change";
                Columns.Add(titles[i], Theme.S(widths[i]), right ? HorizontalAlignment.Right : HorizontalAlignment.Left);
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            IntPtr h = Theme.SendMessage(Handle, 0x101F, IntPtr.Zero, IntPtr.Zero); // LVM_GETHEADER
            if (h != IntPtr.Zero) header = new HeaderPainter(this, h);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (header != null) { header.ReleaseHandle(); header = null; }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            switch (m.Msg)
            {
                case 0x0F: case 0x114: case 0x115: case 0x20A: case 0x20E: case 0x05:
                    if (m.Msg == 0x0F && Items.Count == 0 && !string.IsNullOrEmpty(EmptyText)) DrawEmptyText();
                    if (ScrollChanged != null) ScrollChanged(this, EventArgs.Empty);
                    break;
            }
        }

        /// <summary>Hint shown in the empty list (e.g. for a drive that has not been scanned).</summary>
        public string EmptyText;

        void DrawEmptyText()
        {
            int top = Theme.S(28);
            if (header != null)
            {
                Theme.RECT hr;
                if (GetWindowRect(header.Handle, out hr)) top = hr.B - hr.T;
            }
            using (var g = Graphics.FromHwnd(Handle))
            {
                var r = new Rectangle(Theme.S(16), top + Theme.S(24), Math.Max(0, ClientSize.Width - Theme.S(32)), Theme.S(80));
                TextRenderer.DrawText(g, EmptyText, Font, r, Theme.MUTED, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out Theme.RECT rect);

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        // ---- hover linking with the pie chart

        readonly HashSet<ListViewItem> highlighted = new HashSet<ListViewItem>();
        ListViewItem hotItem;

        /// <summary>Raised when the mouse moves onto another row (null when it leaves the rows).</summary>
        public event Action<ListViewItem> HotItemChanged;

        /// <summary>Rows linked to the hovered pie slice; scrolls the first one into view if none is visible.</summary>
        public void SetHighlight(IEnumerable<ListViewItem> rows)
        {
            if (highlighted.Count == 0 && rows == null) return;
            foreach (var it in highlighted) InvalidateRow(it);
            highlighted.Clear();
            if (rows == null) return;
            ListViewItem first = null;
            bool anyVisible = false;
            foreach (var it in rows)
            {
                if (it.ListView != this) continue;
                highlighted.Add(it);
                if (first == null || it.Index < first.Index) first = it;
                var b = GetItemRect(it.Index);
                if (b.Bottom > 0 && b.Top < ClientSize.Height) anyVisible = true;
                InvalidateRow(it);
            }
            if (first != null && !anyVisible) first.EnsureVisible();
        }

        void InvalidateRow(ListViewItem it)
        {
            if (it.ListView != this || it.Index < 0) return;
            var b = GetItemRect(it.Index);
            if (b.Bottom > 0 && b.Top < ClientSize.Height) Invalidate(new Rectangle(0, b.Y, ClientSize.Width, b.Height));
        }

        void SetHot(ListViewItem it)
        {
            if (it == hotItem) return;
            var old = hotItem;
            hotItem = it;
            if (old != null) InvalidateRow(old);
            if (it != null) InvalidateRow(it);
            if (HotItemChanged != null) HotItemChanged(it);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHot(GetItemAt(Theme.S(4), e.Y));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHot(null);
        }

        /// <summary>Forgets hover and highlight state; call before the rows are replaced.</summary>
        public void ResetHover()
        {
            highlighted.Clear();
            hotItem = null;
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            var g = e.Graphics;
            var r = e.Bounds;
            using (var b = new SolidBrush(Theme.SURFACE)) g.FillRectangle(b, r);
            using (var p = new Pen(Theme.BORDER))
            {
                g.DrawLine(p, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);
                g.DrawLine(p, r.Right - 1, r.Y + Theme.S(6), r.Right - 1, r.Bottom - Theme.S(6));
            }
            var flags = CellFlags | (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            var tr = new Rectangle(r.X + Theme.S(6), r.Y, r.Width - Theme.S(12), r.Height);
            TextRenderer.DrawText(g, e.Header.Text, Font, tr, e.ColumnIndex == sortColumn ? Theme.ACCENT : Theme.MUTED, flags);
        }

        // The whole row is painted here. On hover the native list repaints a row by raising DrawItem
        // without DrawSubItem, so painting cells in OnDrawSubItem made them vanish until the next click.
        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            var g = e.Graphics;
            var row = e.Bounds;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, row);
            bool strong = e.Item.Selected && Focused;
            var rr = new Rectangle(row.X + Theme.S(2), row.Y + 1, row.Width - Theme.S(4), row.Height - 2);
            bool linked = highlighted.Contains(e.Item);
            if (e.Item.Selected)
            {
                Color fill = strong ? Theme.ACCENT : Theme.BORDER;
                Theme.FillRound(g, rr, Theme.S(6), fill, linked && !strong ? Theme.ACCENT : fill);
            }
            else if (linked)
                Theme.FillRound(g, rr, Theme.S(6), Theme.BORDER, Theme.ACCENT);   // highlighted from the pie chart
            else if (e.Item == hotItem)
                Theme.FillRound(g, rr, Theme.S(6), Theme.BORDER, Theme.BORDER);

            var order = new ColumnHeader[Columns.Count];
            foreach (ColumnHeader c in Columns) order[c.DisplayIndex] = c;
            int x = row.X;
            foreach (var c in order)
            {
                var cell = new Rectangle(x, row.Y, c.Width, row.Height);
                x += c.Width;
                if (cell.Right <= e.Bounds.Left || cell.Width <= 0) continue;
                string text = c.Index < e.Item.SubItems.Count ? e.Item.SubItems[c.Index].Text : "";
                var clip = g.Clip;
                g.SetClip(cell, System.Drawing.Drawing2D.CombineMode.Intersect);
                DrawCell(g, e.Item, c, cell, text, strong);
                g.Clip = clip;
            }
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e) { }

        void DrawCell(Graphics g, ListViewItem item, ColumnHeader col, Rectangle r, string text, bool strong)
        {
            var info = item.Tag as ItemInfo;
            Color fore = strong ? Theme.BG : item.ForeColor;
            int pad = Theme.S(6);

            if (col.Index == 0)
            {
                int icon = Theme.S(16);
                if (info != null && info.GroupOf != null) pad += Theme.S(18);
                if (info != null && !info.IsSummary && !info.IsGroup)
                {
                    var ir = new Rectangle(r.X + pad, r.Y + (r.Height - icon) / 2, icon, icon);
                    Color ic = strong ? Theme.BG : Theme.MUTED;
                    if (info.IsDir || info.FolderIcon) Theme.DrawFolder(g, ir, ic); else Theme.DrawFile(g, ir, ic);
                }
                var tr = new Rectangle(r.X + pad + icon + Theme.S(6), r.Y, Math.Max(0, r.Width - pad - icon - Theme.S(12)), r.Height);
                TextRenderer.DrawText(g, text, info != null && info.IsGroup ? Theme.FONT_BOLD : Font, tr, fore, CellFlags);
                return;
            }

            if (col.Index == BarColumn)
            {
                double pct = info != null ? info.Percent : 0;
                int textW = TextRenderer.MeasureText("100.0 %", Font).Width;
                int bh = Theme.S(8);
                var bar = new Rectangle(r.X + pad, r.Y + (r.Height - bh) / 2, Math.Max(0, r.Width - textW - 2 * pad), bh);
                if (bar.Width > 8 && info != null && !info.IsSummary)
                {
                    Color track = strong ? Theme.BG : Theme.BORDER;
                    Color fill = strong ? Theme.FG : Theme.ACCENT;
                    Theme.FillRound(g, bar, bh / 2, track, track);
                    int w = (int)Math.Round(bar.Width * Math.Min(100, Math.Max(0, pct)) / 100);
                    if (w > 0) Theme.FillRound(g, new Rectangle(bar.X, bar.Y, Math.Max(w, bh), bh), bh / 2, fill, fill);
                }
                TextRenderer.DrawText(g, text, Font, new Rectangle(r.Right - textW - pad, r.Y, textW, r.Height), fore,
                    CellFlags | TextFormatFlags.Right);
                return;
            }

            var flags = CellFlags | (col.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(g, text, Font, new Rectangle(r.X + pad, r.Y, Math.Max(0, r.Width - 2 * pad), r.Height), fore, flags);
        }

        /// <summary>Paints the header area to the right of the last column, which owner-draw does not cover.</summary>
        sealed class HeaderPainter : NativeWindow
        {
            readonly ListView owner;

            public HeaderPainter(ListView owner, IntPtr handle)
            {
                this.owner = owner;
                AssignHandle(handle);
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x0F && m.Msg != 0x14) return;
                int right = -Theme.GetScroll(owner.Handle, false).Pos;
                foreach (ColumnHeader c in owner.Columns) right += c.Width;
                using (var g = Graphics.FromHwnd(Handle))
                {
                    var rc = Rectangle.Truncate(g.VisibleClipBounds);
                    if (right >= rc.Right) return;
                    using (var b = new SolidBrush(Theme.SURFACE)) g.FillRectangle(b, right, 0, rc.Right - right, rc.Bottom);
                    using (var p = new Pen(Theme.BORDER)) g.DrawLine(p, right, rc.Bottom - 1, rc.Right, rc.Bottom - 1);
                }
            }
        }
    }
}
