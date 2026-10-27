using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    internal enum ButtonKind { Primary, Secondary, Warn }

    /// <summary>Flat rounded button (radius 8). Click fires on release inside the button only.</summary>
    internal sealed class RoundButton : Control
    {
        public ButtonKind Kind;
        public int MinChars;
        bool hover, pressed;

        public RoundButton(string text, ButtonKind kind)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            Text = text;
            Kind = kind;
            Font = Theme.FONT;
            Cursor = Cursors.Hand;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            string sample = MinChars > 0 ? new string('0', MinChars) : Text;
            int w = TextRenderer.MeasureText(sample, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            return new Size(w + Theme.S(32), Font.Height + Theme.S(12));
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            if (!Enabled) hover = pressed = false;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) pressed = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool fire = pressed && Enabled && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
            pressed = false;
            if (fire) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.BG);
            Color fill, fore;
            if (!Enabled) { fill = Theme.DISABLED_BG; fore = Theme.DISABLED_FG; }
            else if (Kind == ButtonKind.Primary) { fill = hover ? Theme.ACCENT_ACTIVE : Theme.ACCENT; fore = Theme.BG; }
            else if (Kind == ButtonKind.Warn) { fill = hover ? Theme.WARN_FG : Theme.WARN; fore = Theme.BG; }
            else { fill = hover ? Theme.SECONDARY_HOVER : Theme.BORDER; fore = Theme.FG; }
            Theme.FillRound(g, ClientRectangle, Theme.S(8), fill, fill);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Rounded SURFACE panel with a BORDER outline that turns ACCENT while it holds the focus.</summary>
    internal class RoundedPanel : Panel
    {
        public int Radius;
        public bool TrackFocus;
        bool highlight;

        public RoundedPanel(int radius, int padding)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Radius = radius;
            BackColor = Theme.SURFACE;
            ForeColor = Theme.FG;
            Padding = new Padding(Theme.S(padding));
        }

        public bool Highlight
        {
            get { return highlight; }
            set { if (highlight != value) { highlight = value; Invalidate(); } }
        }

        protected override void OnEnter(EventArgs e) { base.OnEnter(e); if (TrackFocus) Highlight = true; }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); if (TrackFocus) Highlight = false; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.BG);
            Theme.FillRound(e.Graphics, new Rectangle(0, 0, Width, Height), Theme.S(Radius), BackColor,
                highlight ? Theme.ACCENT : Theme.BORDER);
        }

        protected override void OnPaint(PaintEventArgs e) { }
    }

    /// <summary>Small chevron hit area; raises Pressed on mouse down.</summary>
    internal sealed class ChevronButton : Control
    {
        public bool Open;
        bool hover;
        public event EventHandler Pressed;

        public ChevronButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && Pressed != null) Pressed(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.SURFACE);
            int s = Theme.S(14);
            var r = new Rectangle((Width - s) / 2, (Height - s) / 2, s, s);
            Theme.DrawChevron(e.Graphics, r, Open || (hover && Enabled) ? Theme.ACCENT : Theme.MUTED, true);
        }
    }

    /// <summary>Editable path box with a chevron that opens a styled list of choices (drives).</summary>
    internal sealed class PathInput : RoundedPanel
    {
        public readonly TextBox Box;
        public readonly List<string> Choices = new List<string>();
        public event EventHandler Submitted;
        public event EventHandler ChoiceCommitted;
        readonly ChevronButton chevron;
        readonly ToolStripDropDown popup;
        DateTime closedAt = DateTime.MinValue;

        public PathInput() : base(8, 5)
        {
            Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.SURFACE, ForeColor = Theme.FG, Font = Theme.FONT };
            chevron = new ChevronButton();
            chevron.Pressed += (s, e) => TogglePopup();
            Controls.Add(Box);
            Controls.Add(chevron);
            Box.GotFocus += (s, e) => Highlight = true;
            Box.LostFocus += (s, e) => Highlight = chevron.Open;
            Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    if (Submitted != null) Submitted(this, EventArgs.Empty);
                }
                else if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down))
                {
                    e.SuppressKeyPress = true;
                    TogglePopup();
                }
            };
            MouseDown += (s, e) => Box.Focus();
            Cursor = Cursors.IBeam;

            popup = new ToolStripDropDown
            {
                Renderer = new DarkRenderer(), BackColor = Theme.SURFACE, ForeColor = Theme.FG,
                Padding = new Padding(Theme.S(4)), DropShadowEnabled = !Theme.HasDwmCorners
            };
            popup.Opened += (s, e) => Theme.RoundPopup(popup.Handle);
            popup.Closed += (s, e) =>
            {
                closedAt = DateTime.UtcNow;
                chevron.Open = false;
                chevron.Invalidate();
                Highlight = Box.Focused;
            };
        }

        public override string Text
        {
            get { return Box != null ? Box.Text : ""; }
            set { if (Box != null) Box.Text = value; }
        }

        public int PreferredHeight { get { return Box.PreferredHeight + 2 * Padding.Top + Theme.S(4); } }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Box.BackColor = Theme.SURFACE;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Box == null || chevron == null) return;
            int pad = Padding.Left;
            int cw = Theme.S(26);
            chevron.Bounds = new Rectangle(Width - pad - cw, 1, cw, Math.Max(0, Height - 2));
            int x = pad + Theme.S(3);
            Box.Bounds = new Rectangle(x, (Height - Box.PreferredHeight) / 2, Math.Max(10, chevron.Left - x - Theme.S(4)), Box.PreferredHeight);
        }

        void TogglePopup()
        {
            if (popup.Visible) { popup.Close(); return; }
            if ((DateTime.UtcNow - closedAt).TotalSeconds < 0.25 || Choices.Count == 0) return;
            popup.Items.Clear();
            foreach (var c in Choices)
            {
                string choice = c;
                bool current = string.Equals(choice.TrimEnd('\\'), Text.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                var item = new ToolStripMenuItem(choice)
                {
                    Font = Theme.FONT, ForeColor = current ? Theme.ACCENT : Theme.FG
                };
                item.Click += (s, e) =>
                {
                    Text = choice;
                    if (ChoiceCommitted != null) BeginInvoke(new Action(() => ChoiceCommitted(this, EventArgs.Empty)));
                };
                popup.Items.Add(item);
            }
            popup.MinimumSize = new Size(Width, 0);
            chevron.Open = true;
            chevron.Invalidate();
            Highlight = true;
            popup.Show(this, new Point(0, Height + Theme.S(2)));
        }
    }

    /// <summary>Text tabs: the current one is ACCENT with an underline pill, the others MUTED.</summary>
    internal sealed class TabStrip : Control
    {
        readonly string[] titles;
        int selected, hover = -1;
        Rectangle[] rects = new Rectangle[0];
        public event EventHandler SelectedIndexChanged;

        public TabStrip(params string[] titles)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            this.titles = titles;
            Font = Theme.FONT;
            Height = Font.Height + Theme.S(16);
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public void SetTitle(int i, string text) { titles[i] = text; Invalidate(); }

        void Measure(Graphics g)
        {
            rects = new Rectangle[titles.Length];
            int x = 0;
            for (int i = 0; i < titles.Length; i++)
            {
                int w = TextRenderer.MeasureText(g, titles[i], Font, Size.Empty, TextFormatFlags.NoPrefix).Width + Theme.S(12);
                w = Math.Min(w, Math.Max(Theme.S(40), Width - x));
                rects[i] = new Rectangle(x, 0, w, Height);
                x += w + Theme.S(10);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.SURFACE);
            Measure(g);
            for (int i = 0; i < titles.Length; i++)
            {
                var r = rects[i];
                Color c = i == selected ? Theme.ACCENT : i == hover ? Theme.FG : Theme.MUTED;
                TextRenderer.DrawText(g, titles[i], Font, new Rectangle(r.X, r.Y, r.Width, r.Height - Theme.S(4)), c,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                if (i == selected)
                    Theme.FillRound(g, new Rectangle(r.X + Theme.S(6), r.Bottom - Theme.S(4), r.Width - Theme.S(12), Theme.S(3)),
                        Theme.S(2), Theme.ACCENT, Theme.ACCENT);
            }
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < rects.Length; i++) if (rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int h = HitTest(e.Location);
            if (h >= 0 && e.Button == MouseButtons.Left) SelectedIndex = h;
        }
    }

    /// <summary>Thin pill scrollbar without arrows (6 px thumb in a 10 px gutter).</summary>
    internal sealed class PillScrollBar : Control
    {
        public readonly bool Vertical;
        public int Minimum, Maximum, Page = 1, Value;
        public event Action<int> Scrolled;
        public event Action<int> Wheel;
        bool hover, dragging;
        int dragOffset;

        public PillScrollBar(bool vertical)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Vertical = vertical;
        }

        public void SetState(Theme.ScrollInfo si)
        {
            if (dragging) return;
            if (si.Min == Minimum && si.Max == Maximum && (int)si.Page == Page && si.Pos == Value) return;
            Minimum = si.Min;
            Maximum = si.Max;
            Page = Math.Max(1, (int)si.Page);
            Value = si.Pos;
            Invalidate();
        }

        int Track { get { return Vertical ? Height : Width; } }
        int Range { get { return Math.Max(1, Maximum - Minimum + 1); } }
        int MaxValue { get { return Math.Max(Minimum, Maximum - Page + 1); } }

        Rectangle Thumb()
        {
            int track = Track - Theme.S(4);
            int len = Math.Max(Theme.S(20), (int)((long)track * Math.Min(Page, Range) / Range));
            len = Math.Min(len, track);
            int span = MaxValue - Minimum;
            int off = span > 0 ? (int)((long)(track - len) * (Value - Minimum) / span) : 0;
            int t = Theme.S(6), c = ((Vertical ? Width : Height) - t) / 2;
            return Vertical ? new Rectangle(c, Theme.S(2) + off, t, len) : new Rectangle(Theme.S(2) + off, c, len, t);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.SURFACE);
            var t = Thumb();
            Color c = hover || dragging ? Theme.PILL_HOVER : Theme.PILL;
            Theme.FillRound(e.Graphics, t, Theme.S(3), c, c);
        }

        void SetValue(int v)
        {
            v = Math.Max(Minimum, Math.Min(MaxValue, v));
            if (v == Value) return;
            Value = v;
            Invalidate();
            if (Scrolled != null) Scrolled(v);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var t = Thumb();
            int p = Vertical ? e.Y : e.X;
            int start = Vertical ? t.Y : t.X, len = Vertical ? t.Height : t.Width;
            if (p >= start && p < start + len) { dragging = true; dragOffset = p - start; Capture = true; }
            else SetValue(Value + (p < start ? -Page : Page));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            var t = Thumb();
            int len = Vertical ? t.Height : t.Width;
            int track = Track - Theme.S(4) - len;
            int p = (Vertical ? e.Y : e.X) - Theme.S(2) - dragOffset;
            int span = MaxValue - Minimum;
            if (track > 0) SetValue(Minimum + (int)Math.Round((double)p * span / track));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; Capture = false; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Wheel != null) Wheel(e.Delta);
        }
    }

    /// <summary>
    /// Hosts a native list/tree control: its own scrollbars are clipped away and replaced by pill scrollbars
    /// that mirror and drive the native scroll position.
    /// </summary>
    internal sealed class ScrollHost : Control
    {
        const int WS_VSCROLL = 0x00200000, WS_HSCROLL = 0x00100000;
        const int WM_HSCROLL = 0x114, WM_VSCROLL = 0x115, WM_MOUSEWHEEL = 0x20A, LVM_SCROLL = 0x1014;
        readonly Control inner;
        readonly Panel clip;
        readonly PillScrollBar vbar, hbar;
        bool pending;

        public ScrollHost(Control inner)
        {
            this.inner = inner;
            inner.Dock = DockStyle.None;
            BackColor = Theme.SURFACE;
            clip = new Panel { BackColor = Theme.SURFACE };
            clip.Controls.Add(inner);
            vbar = new PillScrollBar(true) { Visible = false };
            hbar = new PillScrollBar(false) { Visible = false };
            Controls.Add(clip);
            Controls.Add(vbar);
            Controls.Add(hbar);
            vbar.Scrolled += v => ScrollTo(true, v);
            hbar.Scrolled += v => ScrollTo(false, v);
            vbar.Wheel += ForwardWheel;
            hbar.Wheel += ForwardWheel;
            var notify = inner as IScrollNotify;
            if (notify != null) notify.ScrollChanged += (s, e) => RequestSync();
        }

        void ForwardWheel(int delta)
        {
            if (inner.IsHandleCreated)
                Theme.SendMessage(inner.Handle, WM_MOUSEWHEEL, (IntPtr)(delta << 16), IntPtr.Zero);
        }

        public void RequestSync()
        {
            if (pending || !IsHandleCreated) return;
            pending = true;
            BeginInvoke(new Action(Sync));
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RequestSync(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Sync(); }

        void Sync()
        {
            pending = false;
            if (!inner.IsHandleCreated || Width <= 0 || Height <= 0) return;
            int style = Theme.GetWindowLong(inner.Handle, -16);
            bool vs = (style & WS_VSCROLL) != 0, hs = (style & WS_HSCROLL) != 0;
            int gutter = Theme.S(10);
            int cw = Math.Max(1, Width - (vs ? gutter : 0)), ch = Math.Max(1, Height - (hs ? gutter : 0));
            var cb = new Rectangle(0, 0, cw, ch);
            if (clip.Bounds != cb) clip.Bounds = cb;
            var ib = new Rectangle(0, 0, cw + (vs ? SystemInformation.VerticalScrollBarWidth : 0),
                ch + (hs ? SystemInformation.HorizontalScrollBarHeight : 0));
            if (inner.Bounds != ib) inner.Bounds = ib;

            vbar.Visible = vs;
            if (vs) { vbar.Bounds = new Rectangle(cw, 0, gutter, ch); vbar.SetState(Theme.GetScroll(inner.Handle, true)); }
            hbar.Visible = hs;
            if (hs) { hbar.Bounds = new Rectangle(0, ch, cw, gutter); hbar.SetState(Theme.GetScroll(inner.Handle, false)); }
        }

        void ScrollTo(bool vertical, int value)
        {
            if (!inner.IsHandleCreated) return;
            var lv = inner as ListView;
            int cur = Theme.GetScroll(inner.Handle, vertical).Pos;
            if (lv != null)
            {
                if (vertical)
                {
                    int row = lv.Items.Count > 0 ? lv.GetItemRect(0).Height : 0;
                    if (row > 0) Theme.SendMessage(lv.Handle, LVM_SCROLL, IntPtr.Zero, (IntPtr)((value - cur) * row));
                }
                else Theme.SendMessage(lv.Handle, LVM_SCROLL, (IntPtr)(value - cur), IntPtr.Zero);
            }
            else
            {
                int msg = vertical ? WM_VSCROLL : WM_HSCROLL;
                Theme.SendMessage(inner.Handle, msg, (IntPtr)(4 | (Math.Min(value, 0xFFFF) << 16)), IntPtr.Zero);
                Theme.SendMessage(inner.Handle, msg, (IntPtr)8, IntPtr.Zero);
            }
            RequestSync();
        }
    }

    internal interface IScrollNotify
    {
        event EventHandler ScrollChanged;
    }

    /// <summary>Rotating ACCENT arc: inset 4, extent 120 degrees, width 6, 18 degrees every 40 ms.</summary>
    internal sealed class Spinner : Control
    {
        readonly Timer timer = new Timer { Interval = 40 };
        float angle;

        public Spinner()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            timer.Tick += (s, e) => { angle = (angle + 18) % 360; Invalidate(); };
        }

        public bool Spinning
        {
            get { return timer.Enabled; }
            set { timer.Enabled = value; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.BG);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = 6 * Theme.Scale, inset = 4 * Theme.Scale + w / 2;
            using (var pen = new Pen(Theme.ACCENT, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, inset, inset, Width - 2 * inset, Height - 2 * inset, angle, 120);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Full-window BG overlay with spinner, status text, a MUTED detail line and an optional Cancel.</summary>
    internal sealed class BusyOverlay : Control
    {
        readonly Spinner spinner = new Spinner();
        readonly Label status, detail;
        public readonly RoundButton CancelButton;

        public BusyOverlay()
        {
            BackColor = Theme.BG;
            Visible = false;
            status = new Label
            {
                Font = Theme.FONT_11, ForeColor = Theme.FG, BackColor = Theme.BG, TextAlign = ContentAlignment.TopCenter,
                AutoSize = false, UseMnemonic = false
            };
            detail = new Label
            {
                Font = Theme.FONT_9, ForeColor = Theme.MUTED, BackColor = Theme.BG, TextAlign = ContentAlignment.TopCenter,
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false
            };
            CancelButton = new RoundButton("Cancel", ButtonKind.Secondary) { MinChars = 10 };
            Controls.Add(spinner);
            Controls.Add(status);
            Controls.Add(detail);
            Controls.Add(CancelButton);
        }

        public void ShowBusy(string text, bool canCancel)
        {
            status.Text = text;
            detail.Text = "";
            CancelButton.Visible = canCancel;
            CancelButton.Enabled = true;
            Visible = true;
            BringToFront();
            spinner.Spinning = true;
            PerformLayout();
        }

        public void HideBusy()
        {
            spinner.Spinning = false;
            Visible = false;
        }

        public void SetStatus(string text, string detailText)
        {
            if (status.Text != text) { status.Text = text; PerformLayout(); }
            if (detail.Text != detailText) detail.Text = detailText;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int wrap = Theme.S(420), sp = Theme.S(64);
            int sh = TextRenderer.MeasureText(status.Text, status.Font, new Size(wrap, 0), TextFormatFlags.WordBreak).Height;
            int dh = detail.Font.Height + Theme.S(2);
            var bs = CancelButton.GetPreferredSize(Size.Empty);
            int total = sp + Theme.S(14) + sh + Theme.S(4) + dh + (CancelButton.Visible ? Theme.S(16) + bs.Height : 0);
            int y = Math.Max(0, (Height - total) / 2), cx = Width / 2;
            spinner.Bounds = new Rectangle(cx - sp / 2, y, sp, sp);
            y += sp + Theme.S(14);
            status.Bounds = new Rectangle(cx - wrap / 2, y, wrap, sh);
            y += sh + Theme.S(4);
            detail.Bounds = new Rectangle(cx - wrap / 2, y, wrap, dh);
            y += dh + Theme.S(16);
            CancelButton.Bounds = new Rectangle(cx - bs.Width / 2, y, bs.Width, bs.Height);
        }
    }

    /// <summary>Dark menus and popup lists: SURFACE fill, hovered row BORDER, disabled DISABLED_FG.</summary>
    internal sealed class DarkRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.SURFACE)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (Theme.HasDwmCorners) return;
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (var p = new Pen(Theme.BORDER)) e.Graphics.DrawRectangle(p, r);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(Theme.S(2), 0, e.Item.Width - Theme.S(4), e.Item.Height);
            Theme.FillRound(e.Graphics, r, Theme.S(6), Theme.BORDER, Theme.BORDER);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var mi = e.Item as ToolStripMenuItem;
            if (!e.Item.Enabled) e.TextColor = Theme.DISABLED_FG;
            else if (mi != null && e.Text == mi.ShortcutKeyDisplayString) e.TextColor = Theme.MUTED;
            else e.TextColor = e.Item.ForeColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.BORDER)) e.Graphics.DrawLine(p, Theme.S(8), y, e.Item.Width - Theme.S(8), y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.MUTED;
            base.OnRenderArrow(e);
        }
    }

    /// <summary>
    /// Folder tree drawn in the palette: chevrons, folder glyphs and the name on the left, plus right-aligned
    /// value columns (size, percentage) that line up on every row. No horizontal scrolling: long names are ellipsized.
    /// </summary>
    internal sealed class DarkTreeView : TreeView, IScrollNotify
    {
        public event EventHandler ScrollChanged;
        public event EventHandler ColumnsMoved;

        /// <summary>Text for the value columns of a node (same order as ColumnSamples); null = none.</summary>
        public Func<TreeNode, string[]> ColumnText;
        /// <summary>Widest expected value per column; sets the column widths.</summary>
        public string[] ColumnSamples = new string[0];
        public bool[] ColumnMuted = new bool[0];
        int lastWidth = -1;

        const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

        public DarkTreeView()
        {
            DrawMode = TreeViewDrawMode.OwnerDrawAll;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.SURFACE;
            ForeColor = Theme.FG;
            Font = Theme.FONT;
            ShowLines = false;
            FullRowSelect = true;
            HideSelection = false;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x8000; // TVS_NOHSCROLL
                return cp;
            }
        }

        public int ColumnGap { get { return Theme.S(14); } }

        /// <summary>Left x of each value column, in tree client coordinates (rightmost column ends 8 px from the edge).</summary>
        public int[] ColumnLefts()
        {
            var lefts = new int[ColumnSamples.Length];
            int right = ClientSize.Width - Theme.S(8);
            for (int i = ColumnSamples.Length - 1; i >= 0; i--)
            {
                int w = TextRenderer.MeasureText(ColumnSamples[i], Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                lefts[i] = right - w;
                right = lefts[i] - ColumnGap;
            }
            return lefts;
        }

        public int ColumnRight(int i)
        {
            var lefts = ColumnLefts();
            return i + 1 < lefts.Length ? lefts[i + 1] - ColumnGap : ClientSize.Width - Theme.S(8);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.SendMessage(Handle, 0x112C, (IntPtr)0x4, (IntPtr)0x4); // TVS_EX_DOUBLEBUFFER
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            switch (m.Msg)
            {
                case 0x0F: case 0x114: case 0x115: case 0x20A: case 0x20E: case 0x05:
                    if (ScrollChanged != null) ScrollChanged(this, EventArgs.Empty);
                    if (ClientSize.Width != lastWidth)
                    {
                        // Columns hang off the right edge: repaint all rows (and the header) when the width changes,
                        // e.g. when the vertical scrollbar appears or the splitter moves.
                        lastWidth = ClientSize.Width;
                        Invalidate();
                        if (ColumnsMoved != null) ColumnsMoved(this, EventArgs.Empty);
                    }
                    break;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnDrawNode(DrawTreeNodeEventArgs e)
        {
            var n = e.Node;
            if (n == null || e.Bounds.Height <= 0) return;
            var g = e.Graphics;
            var row = new Rectangle(0, e.Bounds.Y, ClientSize.Width, e.Bounds.Height);
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, row);

            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            bool strong = sel && Focused;
            if (sel)
            {
                Color fill = strong ? Theme.ACCENT : Theme.BORDER;
                Theme.FillRound(g, new Rectangle(row.X + Theme.S(2), row.Y + 1, row.Width - Theme.S(4), row.Height - 2), Theme.S(6), fill, fill);
            }

            int textX = n.Bounds.X;
            int icon = Theme.S(16);
            var iconRect = new Rectangle(textX - icon - Theme.S(3), row.Y + (row.Height - icon) / 2, icon, icon);
            if (n.Nodes.Count > 0)
            {
                int cs = Theme.S(12);
                var cr = new Rectangle(iconRect.X - Indent / 2 - cs / 2 - Theme.S(2), row.Y + (row.Height - cs) / 2, cs, cs);
                Theme.DrawChevron(g, cr, strong ? Theme.BG : n.IsExpanded ? Theme.ACCENT : Theme.MUTED, n.IsExpanded);
            }
            Theme.DrawFolder(g, iconRect, strong ? Theme.BG : Theme.MUTED);

            var values = ColumnText != null ? ColumnText(n) : null;
            int nameRight = row.Right - Theme.S(8);
            if (values != null && ColumnSamples.Length > 0)
            {
                var lefts = ColumnLefts();
                nameRight = lefts[0] - ColumnGap;
                for (int i = 0; i < lefts.Length && i < values.Length; i++)
                {
                    var cr = new Rectangle(lefts[i], row.Y, ColumnRight(i) - lefts[i], row.Height);
                    bool muted = i < ColumnMuted.Length && ColumnMuted[i];
                    TextRenderer.DrawText(g, values[i] ?? "", Font, cr, strong ? Theme.BG : muted ? Theme.MUTED : Theme.FG, Flags | TextFormatFlags.Right);
                }
            }
            bool dim = n.ForeColor == Theme.MUTED;
            var tr = new Rectangle(textX, row.Y, Math.Max(0, nameRight - textX), row.Height);
            TextRenderer.DrawText(g, n.Text, Font, tr, strong ? Theme.BG : dim ? Theme.MUTED : Theme.FG, Flags);
        }
    }

    /// <summary>Header row for a DarkTreeView: "Name" on the left and the value column titles aligned over their columns.</summary>
    internal sealed class TreeHeader : Control
    {
        readonly DarkTreeView tree;
        readonly string name;
        readonly string[] titles;

        public TreeHeader(DarkTreeView tree, string name, params string[] titles)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            this.tree = tree;
            this.name = name;
            this.titles = titles;
            Font = Theme.FONT;
            BackColor = Theme.SURFACE;
            Height = Font.Height + Theme.S(12);
            tree.ColumnsMoved += (s, e) => Invalidate();
            tree.Resize += (s, e) => Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            const TextFormatFlags f = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, name, Font, new Rectangle(Theme.S(8), 0, Width, Height - 1), Theme.MUTED, f);
            if (tree.IsHandleCreated)
            {
                var lefts = tree.ColumnLefts();
                for (int i = 0; i < lefts.Length && i < titles.Length; i++)
                {
                    int right = tree.ColumnRight(i);
                    TextRenderer.DrawText(g, titles[i], Font, new Rectangle(right - Theme.S(200), 0, Theme.S(200), Height - 1), Theme.MUTED, f | TextFormatFlags.Right);
                }
            }
            using (var p = new Pen(Theme.BORDER)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }
}
