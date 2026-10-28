using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DiskSpaceAnalyzer
{
    /// <summary>Design language: palette, fonts and shared drawing helpers. New UI code uses only these names.</summary>
    internal static class Theme
    {
        public static readonly Color BG = Hex(0x222831);
        public static readonly Color SURFACE = Hex(0x2d333d);
        public static readonly Color BORDER = Hex(0x3a414c);
        public static readonly Color SECONDARY_HOVER = Hex(0x4a525e);
        public static readonly Color DISABLED_BG = Hex(0x333a45);
        public static readonly Color FG = Hex(0xffffff);
        public static readonly Color MUTED = Hex(0xaab4c0);
        public static readonly Color DISABLED_FG = Hex(0x6b7480);
        public static readonly Color ACCENT = Hex(0x4da3ff);
        public static readonly Color ACCENT_ACTIVE = Hex(0x7bbcff);
        public static readonly Color ACCENT_LIGHT = Hex(0xcfe4ff);
        // ---- LOGO COLOURS: edit here. This is the only place; tools/make_icon.ps1 reads these three lines for the
        // exe icon, README logo and social preview, so rebuild (build.ps1) after changing them.
        // Shade runs dark -> light: top left, right half, bottom left (Tailwind CSS blue 800 / 600 / 400).
        public static readonly Color LOGO_TOP_LEFT = Hex(0x3d6bcc);
        public static readonly Color LOGO_RIGHT = Hex(0x5e88e6);
        public static readonly Color LOGO_BOTTOM_LEFT = Hex(0x81a2eb);
        public static readonly Color WARN = Hex(0xe0a030);
        public static readonly Color WARN_FG = Hex(0xffd27a);
        public static readonly Color PILL = Hex(0x5c6470);
        public static readonly Color PILL_HOVER = Hex(0x7a828e);

        // Data visualisation only (pie chart): shades of the accent, alternating light/dark so neighbours separate,
        // plus greys for "free space" and catch-all slices.
        public static readonly Color[] CHART = { Hex(0x4da3ff), Hex(0xa6cfff), Hex(0x2f78cc), Hex(0x7bbcff), Hex(0x1f5aa0), Hex(0xcfe4ff), Hex(0x5b95d6), Hex(0x8fb8e8) };
        public static readonly Color CHART_FREE = Hex(0x4a525e);
        public static readonly Color CHART_OTHER = Hex(0x6b7480);

        public static readonly Font FONT = new Font("Segoe UI", 10f);
        public static readonly Font FONT_11 = new Font("Segoe UI", 11f);
        public static readonly Font FONT_9 = new Font("Segoe UI", 9f);

        public static float Scale = 1f;

        public static int S(int v) { return (int)Math.Round(v * Scale); }

        static Color Hex(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d < 1f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Anti-aliased rounded rectangle with a 1 px outline (pass outline == fill for none).</summary>
        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill, Color outline)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            var sm = g.SmoothingMode;
            var pm = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (outline != fill)
            {
                using (var p = RoundRect(r, radius))
                using (var b = new SolidBrush(outline)) g.FillPath(b, p);
                r.Inflate(-1, -1);
                radius = Math.Max(0, radius - 1);
            }
            using (var p = RoundRect(r, radius))
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            g.SmoothingMode = sm;
            g.PixelOffsetMode = pm;
        }

        public static void DrawFolder(Graphics g, Rectangle r, Color c)
        {
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using (var b = new SolidBrush(c))
            using (var tab = RoundRect(new RectangleF(x + w * 0.08f, y + h * 0.16f, w * 0.40f, h * 0.24f), w * 0.07f))
            using (var body = RoundRect(new RectangleF(x + w * 0.08f, y + h * 0.28f, w * 0.84f, h * 0.56f), w * 0.10f))
            {
                g.FillPath(b, tab);
                g.FillPath(b, body);
            }
            g.SmoothingMode = sm;
        }

        public static void DrawFile(Graphics g, Rectangle r, Color c)
        {
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using (var pen = new Pen(c, Math.Max(1.2f, w * 0.09f)) { LineJoin = LineJoin.Round })
            {
                g.DrawPolygon(pen, new[]
                {
                    new PointF(x + w * 0.24f, y + h * 0.10f), new PointF(x + w * 0.58f, y + h * 0.10f),
                    new PointF(x + w * 0.78f, y + h * 0.30f), new PointF(x + w * 0.78f, y + h * 0.90f),
                    new PointF(x + w * 0.24f, y + h * 0.90f)
                });
                g.DrawLines(pen, new[]
                {
                    new PointF(x + w * 0.58f, y + h * 0.10f), new PointF(x + w * 0.58f, y + h * 0.30f),
                    new PointF(x + w * 0.78f, y + h * 0.30f)
                });
            }
            g.SmoothingMode = sm;
        }

        /// <summary>Chevron in a size x size box: pointing down when open, right when closed.</summary>
        public static void DrawChevron(Graphics g, Rectangle r, Color c, bool down)
        {
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, a = r.Width * 0.22f;
            using (var pen = new Pen(c, Math.Max(1.5f, 2 * Scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (down) g.DrawLines(pen, new[] { new PointF(cx - 1.6f * a, cy - 0.8f * a), new PointF(cx, cy + 0.8f * a), new PointF(cx + 1.6f * a, cy - 0.8f * a) });
                else g.DrawLines(pen, new[] { new PointF(cx - 0.8f * a, cy - 1.6f * a), new PointF(cx + 0.8f * a, cy), new PointF(cx - 0.8f * a, cy + 1.6f * a) });
            }
            g.SmoothingMode = sm;
        }

        // ------------------------------------------------------------------ window manager

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static readonly bool HasDwmCorners = Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            }
            catch (Exception) { }
        }

        /// <summary>Windows 11: anti-aliased rounded corners and a BORDER outline on popup windows.</summary>
        public static void RoundPopup(IntPtr hwnd)
        {
            if (!HasDwmCorners) return;
            try
            {
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);
                int border = BORDER.R | (BORDER.G << 8) | (BORDER.B << 16);
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ scroll interop

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ScrollInfo
        {
            public int Size;
            public uint Mask;
            public int Min, Max;
            public uint Page;
            public int Pos, TrackPos;
        }

        [DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        public static ScrollInfo GetScroll(IntPtr hwnd, bool vertical)
        {
            var si = new ScrollInfo { Size = Marshal.SizeOf(typeof(ScrollInfo)), Mask = 0x17 };
            GetScrollInfo(hwnd, vertical ? 1 : 0, ref si);
            return si;
        }
    }
}
