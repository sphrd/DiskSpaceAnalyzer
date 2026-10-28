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
        public string Detail;     // second line in the centre while hovered (size and share)
        public long Size;
        public Color Color;
        public DirNode Dir;       // folder opened on click (single-folder slices only)
        /// <summary>Drawn as an empty outlined section (free space) instead of a filled one.</summary>
        public bool Empty;
        /// <summary>Rows of the folder contents list this slice stands for (empty for free / system space).</summary>
        public readonly List<ListViewItem> Rows = new List<ListViewItem>();
    }

    /// <summary>
    /// Donut chart of one folder or drive. Slices follow the folder contents list row for row (same order);
    /// small consecutive rows are merged into one "smaller items" slice. Drawn from sizes already in memory.
    /// The centre shows the hovered slice, or the total. Hover is shared with the list in both directions.
    /// </summary>
    internal sealed class PieChart : Control
    {
        List<ChartSlice> slices = new List<ChartSlice>();
        long total;
        string title = "", subtitle = "";
        float startAngle = -90;  // where the first slice begins (12 o'clock)
        int hover = -1;          // slice drawn as hovered (by the mouse here, or set from the list)
        int reported = -1;       // slice last reported through HoverChanged (mouse on the chart only)
        Rectangle pieRect;

        // Logo state (drive not scanned yet / scan running) and the logo -> chart morph.
        bool logoMode;
        string logoNote = "";
        readonly Timer animTimer = new Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch animClock = new System.Diagnostics.Stopwatch();
        const double AnimMs = 650;
        float anim = 1;          // 0 = logo, 1 = chart

        /// <summary>Raised when the mouse moves onto another slice (or off the chart: -1).</summary>
        public event Action<int> HoverChanged;
        public event Action<ChartSlice> SliceClicked;

        public PieChart()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.SURFACE;
            Font = Theme.FONT;
            animTimer.Tick += (s, e) =>
            {
                anim = (float)Math.Min(1.0, animClock.Elapsed.TotalMilliseconds / AnimMs);
                if (anim >= 1) { animTimer.Stop(); animClock.Reset(); }
                Invalidate();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) animTimer.Dispose();
            base.Dispose(disposing);
        }

        bool Animating { get { return anim < 1; } }

        /// <summary>Shows the app logo instead of a chart (nothing scanned yet); the next SetData morphs it into the chart.</summary>
        public void ShowLogo(string title, string subtitle, string note)
        {
            this.title = title ?? "";
            this.subtitle = subtitle ?? "";
            logoNote = note ?? "";
            slices = new List<ChartSlice>();
            total = 0;
            hover = reported = -1;
            logoMode = true;
            animTimer.Stop();
            animClock.Reset();
            anim = 1;
            Invalidate();
        }

        public IList<ChartSlice> Slices { get { return slices; } }

        public void SetData(string title, string subtitle, List<ChartSlice> slices)
        {
            this.title = title ?? "";
            this.subtitle = subtitle ?? "";
            this.slices = slices ?? new List<ChartSlice>();
            total = 0;
            foreach (var s in this.slices) total += Math.Max(0, s.Size);
            // Slices run clockwise from 12 o'clock; free space is last, so it ends at the top, left of the vertical line.
            startAngle = -90;
            hover = -1;
            reported = -1;
            // Coming from the logo: morph it into the chart instead of switching at once.
            if (logoMode && total > 0 && Visible && IsHandleCreated)
            {
                anim = 0;
                animClock.Restart();
                animTimer.Start();
            }
            logoMode = false;
            Invalidate();
        }

        /// <summary>Highlights a slice from outside (the list) without raising HoverChanged.</summary>
        public void SetHover(int index)
        {
            if (index >= slices.Count || Animating) index = -1;
            if (index == hover) return;
            hover = index;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (title.Length == 0) return;
            int pad = Theme.S(10);
            int y = pad;
            const TextFormatFlags one = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, title, Theme.FONT_11, new Rectangle(pad, y, Width - 2 * pad, Theme.FONT_11.Height), Theme.FG, one);
            y += Theme.FONT_11.Height + Theme.S(2);
            if (subtitle.Length > 0)
            {
                TextRenderer.DrawText(g, subtitle, Theme.FONT_9, new Rectangle(pad, y, Width - 2 * pad, Theme.FONT_9.Height), Theme.MUTED, one);
                y += Theme.FONT_9.Height;
            }
            y += Theme.S(16);

            // The donut takes the rest of the pane, centred, leaving room for the hover pop-out.
            int grow = Theme.S(6);
            int d = Math.Min(Width - 2 * pad - 2 * grow, Height - y - pad - 2 * grow);
            d = Math.Max(Theme.S(60), d);
            int top = y + Math.Max(0, (Height - y - pad - d) / 3);
            pieRect = new Rectangle((Width - d) / 2, top, d, d);
            if (logoMode) { DrawLogo(g, pieRect, 1f); DrawLogoNote(g); }
            else if (Animating) DrawMorph(g);
            else DrawDonut(g);
        }

        // ---- logo and morph

        static float Ease(float t) { return 1f - (float)Math.Pow(1 - t, 3); }

        static Color Fade(Color c, float a) { return Color.FromArgb((int)Math.Round(255 * Math.Max(0, Math.Min(1, a))), c); }

        /// <summary>
        /// App logo (no tile), drawn on the chart's own circle (same centre and diameter): LOGO_TOP_LEFT upper-left,
        /// LOGO_RIGHT right half, LOGO_BOTTOM_LEFT lower-left section, mouth facing left.
        /// </summary>
        void DrawLogo(Graphics g, Rectangle circle, float alpha)
        {
            if (alpha <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            const float mouth = 30, under = 6;
            using (var b = new SolidBrush(Fade(Theme.LOGO_TOP_LEFT, alpha))) g.FillPie(b, circle, 180 + mouth, 90 - mouth + under);
            using (var b = new SolidBrush(Fade(Theme.LOGO_BOTTOM_LEFT, alpha))) g.FillPie(b, circle, 90 - under, 90 - mouth + under);
            using (var b = new SolidBrush(Fade(Theme.LOGO_RIGHT, alpha))) g.FillPie(b, circle, -90, 180);
        }
        void DrawLogoNote(Graphics g)
        {
            if (logoNote.Length == 0) return;
            var r = new Rectangle(Theme.S(10), pieRect.Bottom + Theme.S(14), Width - Theme.S(20), Theme.FONT_9.Height * 2 + Theme.S(4));
            TextRenderer.DrawText(g, logoNote, Theme.FONT_9, r, Theme.MUTED,
                TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }

        /// <summary>
        /// Logo -> chart on the same circle: the logo fades out while the chart sweeps in clockwise from 12 o'clock and
        /// the hole opens from the centre. Uses only data already in memory.
        /// </summary>
        void DrawMorph(Graphics g)
        {
            float e = Ease(anim);
            var r = pieRect;
            DrawLogo(g, r, 1 - e);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float reveal = 360f * e, start = startAngle, alpha = Math.Min(1f, anim * 2.5f);
            foreach (var s in slices)
            {
                float sweep = (float)(360.0 * s.Size / total);
                if (sweep <= 0) continue;
                float visible = Math.Min(sweep, reveal - (start - startAngle));
                if (visible <= 0) break;
                if (!s.Empty)
                    using (var b = new SolidBrush(Fade(s.Color, alpha))) g.FillPie(b, r, start, Math.Max(0.01f, visible));
                start += sweep;
            }
            int hole = (int)(r.Width * 0.58 * e);
            if (hole > 0)
                using (var b = new SolidBrush(BackColor))
                    g.FillEllipse(b, r.X + (r.Width - hole) / 2, r.Y + (r.Height - hole) / 2, hole, hole);
        }
        void DrawDonut(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var r = pieRect;
            if (total <= 0)
            {
                using (var b = new SolidBrush(Theme.BORDER)) g.FillEllipse(b, r);
            }
            else
            {
                float start = startAngle;
                for (int i = 0; i < slices.Count; i++)
                {
                    float sweep = (float)(360.0 * slices[i].Size / total);
                    if (sweep <= 0) continue;
                    if (!slices[i].Empty)
                    {
                        var rr = r;
                        if (i == hover) rr.Inflate(Theme.S(6), Theme.S(6));
                        using (var b = new SolidBrush(slices[i].Color)) g.FillPie(b, rr, start, Math.Max(sweep, 0.01f));
                    }
                    start += sweep;
                }
                // Thin separators in the panel colour between visible slices.
                int visible = 0;
                foreach (var s in slices) if (s.Size > 0) visible++;
                if (visible > 1)
                {
                    start = startAngle;
                    var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
                    float rad = r.Width / 2f + Theme.S(7);
                    using (var p = new Pen(BackColor, Math.Max(1.5f, 1.5f * Theme.Scale)))
                        foreach (var s in slices)
                        {
                            if (s.Size <= 0) continue;
                            double a = start * Math.PI / 180;
                            g.DrawLine(p, c, new PointF(c.X + rad * (float)Math.Cos(a), c.Y + rad * (float)Math.Sin(a)));
                            start += (float)(360.0 * s.Size / total);
                        }
                }
            }
            int hole = (int)(r.Width * 0.58);
            var hr = new Rectangle(r.X + (r.Width - hole) / 2, r.Y + (r.Height - hole) / 2, hole, hole);
            using (var b = new SolidBrush(BackColor)) g.FillEllipse(b, hr);
            if (total > 0) DrawEmptySlices(g, r, hr);
            // Centre: hovered slice (name, size and share) or the total.
            string l1, l2, l3;
            if (hover >= 0 && hover < slices.Count) { l1 = slices[hover].Label; l2 = Fmt.Size(slices[hover].Size); l3 = slices[hover].Detail ?? ""; }
            else { l1 = "Total"; l2 = Fmt.Size(total); l3 = ""; }
            var inner = Rectangle.Inflate(hr, -Theme.S(14), 0);
            int h1 = Theme.FONT_9.Height, h2 = Theme.FONT_11.Height, h3 = l3.Length > 0 ? Theme.FONT_9.Height : 0;
            int ty = hr.Y + (hr.Height - h1 - h2 - h3) / 2;
            const TextFormatFlags c1 = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(g, l1, Theme.FONT_9, new Rectangle(inner.X, ty, inner.Width, h1), Theme.MUTED, c1);
            TextRenderer.DrawText(g, l2, Theme.FONT_11, new Rectangle(inner.X, ty + h1, inner.Width, h2), Theme.FG, c1);
            if (h3 > 0) TextRenderer.DrawText(g, l3, Theme.FONT_9, new Rectangle(inner.X, ty + h1 + h2, inner.Width, h3), Theme.MUTED, c1);
        }

        /// <summary>Empty slices (free space) are not drawn; while hovered they get a faint fill so the target is visible.</summary>
        void DrawEmptySlices(Graphics g, Rectangle outer, Rectangle inner)
        {
            if (hover < 0 || hover >= slices.Count || !slices[hover].Empty) return;
            float start = startAngle;
            for (int i = 0; i < slices.Count; i++)
            {
                float sweep = (float)(360.0 * slices[i].Size / total);
                if (sweep <= 0) continue;
                if (i == hover)
                {
                    using (var path = new GraphicsPath())
                    {
                        path.AddArc(outer, start, sweep);
                        path.AddArc(inner, start + sweep, -sweep);
                        path.CloseFigure();
                        using (var b = new SolidBrush(Theme.BORDER)) g.FillPath(b, path);
                    }
                }
                start += sweep;
            }
        }

        int HitTest(Point p)
        {
            if (total <= 0 || pieRect.Width <= 0 || logoMode || Animating) return -1;
            float cx = pieRect.X + pieRect.Width / 2f, cy = pieRect.Y + pieRect.Height / 2f;
            double dx = p.X - cx, dy = p.Y - cy, dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > pieRect.Width / 2.0 + Theme.S(6) || dist < pieRect.Width * 0.29) return -1;
            double ang = Math.Atan2(dy, dx) * 180 / Math.PI - startAngle;
            ang = ((ang % 360) + 360) % 360;
            double acc = 0;
            for (int i = 0; i < slices.Count; i++)
            {
                if (slices[i].Size <= 0) continue;
                acc += 360.0 * slices[i].Size / total;
                if (ang <= acc) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            Cursor = h >= 0 && slices[h].Dir != null ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
            if (h != reported)
            {
                reported = h;
                if (HoverChanged != null) HoverChanged(h);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1) { hover = -1; Invalidate(); }
            if (reported != -1)
            {
                reported = -1;
                if (HoverChanged != null) HoverChanged(-1);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h >= 0 && slices[h].Dir != null && SliceClicked != null) SliceClicked(slices[h]);
        }
    }
}
