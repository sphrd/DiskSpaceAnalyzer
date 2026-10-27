using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    internal sealed class ChartSlice
    {
        public string Label;
        public long Size;
        public Color Color;
        public object Tag;        // DirNode to open on click, or null
    }

    /// <summary>
    /// Donut chart of one folder or drive, drawn from sizes that are already known (no disk access), with the centre
    /// showing the hovered slice or the total, a legend below, and a capacity bar for the drive (used / this item / free).
    /// </summary>
    internal sealed class PieChart : Control
    {
        List<ChartSlice> slices = new List<ChartSlice>();
        long total;
        string title = "", subtitle = "";
        string capLabel = "";
        long capTotal, capFree, capItem;
        int hover = -1;
        Rectangle pieRect;
        readonly List<Rectangle> legendRows = new List<Rectangle>();

        public event Action<ChartSlice> SliceClicked;

        public PieChart()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.SURFACE;
            Font = Theme.FONT;
        }

        /// <summary>capTotal = 0 hides the capacity bar.</summary>
        public void SetData(string title, string subtitle, List<ChartSlice> slices, string capLabel, long capTotal, long capFree, long capItem)
        {
            this.title = title ?? "";
            this.subtitle = subtitle ?? "";
            this.slices = slices ?? new List<ChartSlice>();
            total = 0;
            foreach (var s in this.slices) total += Math.Max(0, s.Size);
            this.capLabel = capLabel ?? "";
            this.capTotal = capTotal;
            this.capFree = capFree;
            this.capItem = capItem;
            hover = -1;
            Invalidate();
        }

        public void Clear() { SetData("", "", null, "", 0, 0, 0); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            legendRows.Clear();
            int pad = Theme.S(10);
            int y = pad;

            // Title (selected item) and subtitle
            const TextFormatFlags one = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            if (title.Length == 0) return;
            TextRenderer.DrawText(g, title, Theme.FONT_11, new Rectangle(pad, y, Width - 2 * pad, Theme.FONT_11.Height), Theme.FG, one);
            y += Theme.FONT_11.Height + Theme.S(2);
            if (subtitle.Length > 0)
            {
                TextRenderer.DrawText(g, subtitle, Theme.FONT_9, new Rectangle(pad, y, Width - 2 * pad, Theme.FONT_9.Height), Theme.MUTED, one);
                y += Theme.FONT_9.Height;
            }
            y += Theme.S(12);

            // Donut: up to 240 px, but small enough that every legend row fits below it.
            int capH = capTotal > 0 ? Theme.S(48) : 0;
            int rowH0 = Font.Height + Theme.S(8);
            int d = Math.Min(Width - 2 * pad - Theme.S(20), Theme.S(240));
            d = Math.Max(Theme.S(80), Math.Min(d, Height - y - capH - Theme.S(16) - slices.Count * rowH0 - pad));
            pieRect = new Rectangle((Width - d) / 2, y, d, d);
            DrawDonut(g);
            y += d + Theme.S(16);

            // Capacity bar for the drive
            if (capTotal > 0)
            {
                TextRenderer.DrawText(g, capLabel, Theme.FONT_9, new Rectangle(pad, y, Width - 2 * pad, Theme.FONT_9.Height), Theme.MUTED, one);
                y += Theme.FONT_9.Height + Theme.S(6);
                int bh = Theme.S(10);
                var bar = new Rectangle(pad, y, Width - 2 * pad, bh);
                Theme.FillRound(g, bar, bh / 2, Theme.CHART_FREE, Theme.CHART_FREE);
                long used = Math.Max(0, capTotal - capFree);
                int uw = (int)(bar.Width * (double)used / capTotal);
                if (uw > 0) Theme.FillRound(g, new Rectangle(bar.X, bar.Y, Math.Max(uw, bh), bh), bh / 2, Theme.CHART[1], Theme.CHART[1]);
                int iw = (int)(bar.Width * (double)Math.Min(capItem, used) / capTotal);
                if (iw > 0) Theme.FillRound(g, new Rectangle(bar.X, bar.Y, Math.Max(iw, bh), bh), bh / 2, Theme.ACCENT, Theme.ACCENT);
                y += bh + Theme.S(16);
            }

            // Legend
            int sw = Theme.S(10), rowH = Font.Height + Theme.S(8);
            const TextFormatFlags measure = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int sizeW = TextRenderer.MeasureText("999.9 MB", Font, Size.Empty, measure).Width;
            int pctW = TextRenderer.MeasureText("100.0 %", Font, Size.Empty, measure).Width;
            for (int i = 0; i < slices.Count && y + rowH <= Height; i++)
            {
                var s = slices[i];
                var row = new Rectangle(Theme.S(4), y, Width - Theme.S(8), rowH);
                legendRows.Add(row);
                if (i == hover) Theme.FillRound(g, row, Theme.S(6), Theme.BORDER, Theme.BORDER);
                Theme.FillRound(g, new Rectangle(pad, y + (rowH - sw) / 2, sw, sw), Theme.S(3), s.Color, s.Color);
                int textX = pad + sw + Theme.S(8);
                int pctX = Width - pad - pctW, sizeX = pctX - Theme.S(10) - sizeW;
                TextRenderer.DrawText(g, s.Label, Font, new Rectangle(textX, y, Math.Max(0, sizeX - Theme.S(8) - textX), rowH), Theme.FG, one | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, Fmt.Size(s.Size), Font, new Rectangle(sizeX, y, sizeW, rowH), Theme.FG, one | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                TextRenderer.DrawText(g, Pct(s.Size), Font, new Rectangle(pctX, y, pctW, rowH), Theme.MUTED, one | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                y += rowH;
            }
        }

        string Pct(long size) { return total > 0 ? string.Format("{0:0.0} %", 100.0 * size / total) : ""; }

        void DrawDonut(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var r = pieRect;
            if (total <= 0)
            {
                using (var b = new SolidBrush(Theme.CHART_FREE)) g.FillEllipse(b, r);
            }
            else
            {
                float start = -90;
                for (int i = 0; i < slices.Count; i++)
                {
                    float sweep = (float)(360.0 * slices[i].Size / total);
                    if (sweep <= 0) continue;
                    var rr = r;
                    if (i == hover) rr.Inflate(Theme.S(4), Theme.S(4));
                    using (var b = new SolidBrush(slices[i].Color)) g.FillPie(b, rr, start, Math.Max(sweep, 0.01f));
                    start += sweep;
                }
                // Thin separators between slices, in the panel colour (only when there is more than one slice).
                if (slices.Count > 1)
                {
                    start = -90;
                    var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
                    float rad = r.Width / 2f + Theme.S(5);
                    using (var p = new Pen(BackColor, Math.Max(1.5f, 1.5f * Theme.Scale)))
                        foreach (var s in slices)
                        {
                            double a = start * Math.PI / 180;
                            g.DrawLine(p, c, new PointF(c.X + rad * (float)Math.Cos(a), c.Y + rad * (float)Math.Sin(a)));
                            start += (float)(360.0 * s.Size / total);
                        }
                }
            }
            // Hole and centre text
            int hole = (int)(r.Width * 0.58);
            var hr = new Rectangle(r.X + (r.Width - hole) / 2, r.Y + (r.Height - hole) / 2, hole, hole);
            using (var b = new SolidBrush(BackColor)) g.FillEllipse(b, hr);
            string big, small;
            if (hover >= 0 && hover < slices.Count) { big = Fmt.Size(slices[hover].Size); small = slices[hover].Label + "  " + Pct(slices[hover].Size); }
            else { big = Fmt.Size(total); small = "total"; }
            var inner = Rectangle.Inflate(hr, -Theme.S(8), 0);
            int bigH = Theme.FONT_11.Height, smallH = Theme.FONT_9.Height;
            int ty = hr.Y + (hr.Height - bigH - smallH) / 2;
            const TextFormatFlags c1 = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(g, big, Theme.FONT_11, new Rectangle(inner.X, ty, inner.Width, bigH), Theme.FG, c1);
            TextRenderer.DrawText(g, small, Theme.FONT_9, new Rectangle(inner.X, ty + bigH, inner.Width, smallH), Theme.MUTED, c1);
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < legendRows.Count; i++) if (legendRows[i].Contains(p)) return i;
            if (total <= 0 || pieRect.Width <= 0) return -1;
            float cx = pieRect.X + pieRect.Width / 2f, cy = pieRect.Y + pieRect.Height / 2f;
            double dx = p.X - cx, dy = p.Y - cy, dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > pieRect.Width / 2.0 || dist < pieRect.Width * 0.29) return -1;
            double ang = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
            if (ang < 0) ang += 360;
            double acc = 0;
            for (int i = 0; i < slices.Count; i++)
            {
                acc += 360.0 * slices[i].Size / total;
                if (ang <= acc) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            bool clickable = h >= 0 && slices[h].Tag != null;
            Cursor = clickable ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1) { hover = -1; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h >= 0 && slices[h].Tag != null && SliceClicked != null) SliceClicked(slices[h]);
        }
    }
}
