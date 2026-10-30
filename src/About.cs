using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>
    /// The app's small dialog frame, shared by About and the update window so they always look the same:
    /// fixed dark window, the logo on the left, content in a column on the right, buttons bottom right
    /// (the first one primary, the others secondary). Escape closes it.
    /// </summary>
    internal class AppDialog : Form
    {
        protected static readonly Font HeadingFont = new Font("Segoe UI", 16f, FontStyle.Bold);
        readonly List<RoundButton> buttons = new List<RoundButton>();

        protected AppDialog(string caption)
        {
            Text = caption;
            Font = Theme.FONT;
            BackColor = Theme.BG;
            ForeColor = Theme.FG;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(440), Theme.S(250));
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        }

        protected int ContentX { get { return Theme.S(140); } }
        protected int ContentWidth { get { return ClientSize.Width - ContentX - Theme.S(24); } }
        protected int ContentTop { get { return Theme.S(28); } }
        protected int ButtonsTop { get { return buttons.Count > 0 ? buttons[0].Top : ClientSize.Height - Theme.S(20); } }

        /// <summary>Adds a label in the content column at y and moves y below it.</summary>
        protected Label AddLabel(string text, Font font, Color color, ref int y, int gapAfter)
        {
            var l = new Label { Text = text, Font = font, ForeColor = color, BackColor = Theme.BG, AutoSize = false, UseMnemonic = false };
            int h = TextRenderer.MeasureText(text, font, new Size(ContentWidth, 0), TextFormatFlags.WordBreak).Height;
            l.Bounds = new Rectangle(ContentX, y, ContentWidth, h);
            Controls.Add(l);
            y += h + gapAfter;
            return l;
        }

        /// <summary>Replaces the buttons: the first is primary (accent), the others secondary; laid out right to left.</summary>
        protected void SetButtons(params KeyValuePair<string, Action>[] items)
        {
            foreach (var b in buttons) { Controls.Remove(b); b.Dispose(); }
            buttons.Clear();
            int x = ClientSize.Width - Theme.S(24);
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                var b = new RoundButton(item.Key, i == 0 ? ButtonKind.Primary : ButtonKind.Secondary) { MinChars = i == 0 ? 8 : 0 };
                b.Click += (s, e) => item.Value();
                var ps = b.GetPreferredSize(Size.Empty);
                x -= ps.Width;
                b.Bounds = new Rectangle(x, ClientSize.Height - Theme.S(20) - ps.Height, ps.Width, ps.Height);
                x -= Theme.S(8);
                buttons.Add(b);
                Controls.Add(b);
            }
        }

        protected static KeyValuePair<string, Action> Button(string text, Action action) { return new KeyValuePair<string, Action>(text, action); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int d = Theme.S(84);
            DrawIcon(e.Graphics, new Rectangle(Theme.S(32), Theme.S(32), d, d));
        }

        protected virtual void DrawIcon(Graphics g, Rectangle r) { Theme.DrawLogo(g, r, 1f); }
    }

    internal enum AlertIcon { Logo, Warn, Danger }

    /// <summary>
    /// Every prompt of the app (confirmations, warnings, errors) in the About frame: a warning sign (DANGER red for
    /// permanent deletes and uninstalls, WARN amber for warnings) or the logo for plain information, a heading, the text,
    /// an optional amber note, and the full list of affected items in a scrollable box. Buttons bottom right, the first
    /// one primary (DANGER for destructive prompts). Escape cancels; Enter picks the primary button, except on destructive
    /// prompts where it cancels.
    /// </summary>
    internal sealed class AlertForm : AppDialog
    {
        readonly AlertIcon icon;
        public string Result;
        static readonly Font AlertHeading = new Font("Segoe UI", 13f, FontStyle.Bold);
        internal DetailsBox Details;
        internal readonly List<RoundButton> Buttons = new List<RoundButton>();

        public AlertForm(AlertIcon icon, string heading, string body, string note, IList<string> details, params string[] buttons)
            : base("Disk Space Analyzer")
        {
            this.icon = icon;
            bool hasDetails = details != null && details.Count > 0;
            ClientSize = new Size(Theme.S(hasDetails ? 760 : 560), ClientSize.Height);
            int y = ContentTop;
            AddLabel(heading, AlertHeading, Theme.FG, ref y, Theme.S(8));
            if (!string.IsNullOrEmpty(body)) AddLabel(body, Theme.FONT, Theme.FG, ref y, Theme.S(6));
            if (!string.IsNullOrEmpty(note)) AddLabel(note, Theme.FONT_9, Theme.WARN_FG, ref y, Theme.S(6));
            y = Math.Max(y + Theme.S(6), Theme.S(32) + Theme.S(84) + Theme.S(16));
            if (!hasDetails) y = Math.Max(Theme.S(150), y - Theme.S(16));
            if (hasDetails)
            {
                Details = new DetailsBox(details);
                int h = Details.PreferredHeight(12);
                Details.Bounds = new Rectangle(Theme.S(24), y, ClientSize.Width - Theme.S(48), h);
                Controls.Add(Details);
                y += h + Theme.S(8);
            }
            ClientSize = new Size(ClientSize.Width, y + Theme.S(60));
            bool destructive = icon == AlertIcon.Danger;
            var items = new KeyValuePair<string, Action>[buttons.Length];
            for (int i = 0; i < buttons.Length; i++) { string b = buttons[i]; items[i] = Button(b, () => { Result = b; Close(); }); }
            SetButtons(items);
            foreach (Control c in Controls) { var rb = c as RoundButton; if (rb != null) Buttons.Add(rb); }
            if (destructive && Buttons.Count > 0) Buttons[0].Kind = ButtonKind.Danger;
            if (hasDetails)
            {
                var copy = new RoundButton("Copy list", ButtonKind.Secondary);
                var ps = copy.GetPreferredSize(Size.Empty);
                copy.Bounds = new Rectangle(Theme.S(24), ClientSize.Height - Theme.S(20) - ps.Height, ps.Width, ps.Height);
                copy.Click += (s, e) => { Details.CopyAll(); copy.Text = "Copied"; };
                Controls.Add(copy);
            }
            KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter || buttons.Length == 0) return;
                Result = destructive ? (buttons.Length > 1 ? buttons[buttons.Length - 1] : null) : buttons[0];
                e.Handled = true;
                Close();
            };
            Shown += (s, e) => { if (Buttons.Count > 0) (destructive ? Buttons[Buttons.Count - 1] : Buttons[0]).Focus(); };
        }

        protected override void DrawIcon(Graphics g, Rectangle r)
        {
            if (icon == AlertIcon.Logo) { base.DrawIcon(g, r); return; }
            Theme.DrawWarningSign(g, Rectangle.Inflate(r, -Theme.S(6), -Theme.S(6)), icon == AlertIcon.Danger ? Theme.DANGER : Theme.WARN);
        }
    }

    /// <summary>
    /// Read-only list of lines in a rounded SURFACE box with pill scrollbars; a tab separates a muted note (size,
    /// reason) from the text. Ctrl+C / Ctrl+A copy every line.
    /// </summary>
    internal sealed class DetailsBox : Control
    {
        readonly IList<string> lines;
        readonly PillScrollBar vbar = new PillScrollBar(true), hbar = new PillScrollBar(false);
        int top, left, contentW = -1;
        int LineH { get { return Theme.FONT_9.Height + Theme.S(4); } }
        int Pad { get { return Theme.S(10); } }
        int Gutter { get { return Theme.S(10); } }
        const TextFormatFlags F = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        public DetailsBox(IList<string> lines)
        {
            this.lines = lines;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.BG;
            Font = Theme.FONT_9;
            Controls.Add(vbar);
            Controls.Add(hbar);
            vbar.Scrolled += v => { top = v; Invalidate(); };
            hbar.Scrolled += v => { left = v; Invalidate(); };
            vbar.Wheel += d => Scroll(-d / 40);
            hbar.Wheel += d => Scroll(-d / 40);
        }

        public int PreferredHeight(int maxLines) { return Math.Min(lines.Count, maxLines) * LineH + 2 * Pad + Theme.S(2); }

        public int LineCount { get { return lines.Count; } }

        public void CopyAll()
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, lines.Select(l => l.Replace("\t", "   ")))); } catch (Exception) { }
        }

        int VisibleLines { get { return Math.Max(1, (Height - 2 * Pad - (HNeeded ? Gutter : 0)) / LineH); } }
        bool VNeeded { get { return lines.Count * LineH > Height - 2 * Pad; } }
        bool HNeeded { get { return ContentWidth > Width - 2 * Pad - Gutter; } }

        int ContentWidth
        {
            get
            {
                if (contentW >= 0) return contentW;
                int w = 0;
                using (var g = CreateGraphics())
                    foreach (var l in lines.Take(20000)) w = Math.Max(w, LineWidth(g, l));
                return contentW = w;
            }
        }

        int LineWidth(Graphics g, string l)
        {
            int tab = l.IndexOf('\t');
            if (tab < 0) return TextRenderer.MeasureText(g, l, Font, Size.Empty, F).Width;
            return TextRenderer.MeasureText(g, l.Substring(0, tab), Font, Size.Empty, F).Width + Theme.S(16) +
                   TextRenderer.MeasureText(g, l.Substring(tab + 1), Font, Size.Empty, F).Width;
        }

        void Scroll(int linesDelta)
        {
            top = Math.Max(0, Math.Min(lines.Count - VisibleLines, top + linesDelta));
            SyncBars();
            Invalidate();
        }

        void SyncBars()
        {
            vbar.Visible = VNeeded;
            hbar.Visible = HNeeded;
            vbar.Bounds = new Rectangle(Width - Gutter - Theme.S(2), Theme.S(6), Gutter, Height - Theme.S(12) - (HNeeded ? Gutter : 0));
            hbar.Bounds = new Rectangle(Theme.S(6), Height - Gutter - Theme.S(2), Width - Theme.S(12) - (VNeeded ? Gutter : 0), Gutter);
            vbar.SetState(new Theme.ScrollInfo { Min = 0, Max = Math.Max(0, lines.Count - 1), Page = (uint)VisibleLines, Pos = top });
            int viewW = Math.Max(1, Width - 2 * Pad - Gutter);
            hbar.SetState(new Theme.ScrollInfo { Min = 0, Max = Math.Max(0, ContentWidth - 1), Page = (uint)viewW, Pos = left });
        }

        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); SyncBars(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((ModifierKeys & Keys.Shift) != 0 && HNeeded)
            {
                left = Math.Max(0, Math.Min(ContentWidth - (Width - 2 * Pad - Gutter), left - e.Delta / 2));
                SyncBars();
                Invalidate();
            }
            else Scroll(-e.Delta / 40);
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }

        protected override bool IsInputKey(Keys k)
        {
            var kc = k & Keys.KeyCode;
            return kc == Keys.Up || kc == Keys.Down || kc == Keys.PageUp || kc == Keys.PageDown || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.A)) { CopyAll(); e.Handled = true; return; }
            int d = e.KeyCode == Keys.Down ? 1 : e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.PageDown ? VisibleLines : e.KeyCode == Keys.PageUp ? -VisibleLines :
                e.KeyCode == Keys.Home ? -lines.Count : e.KeyCode == Keys.End ? lines.Count : 0;
            if (d != 0) { Scroll(d); e.Handled = true; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Theme.FillRound(g, ClientRectangle, Theme.S(10), Theme.SURFACE, Focused ? Theme.SECONDARY_HOVER : Theme.BORDER);
            var clip = new Rectangle(Pad, Pad, Width - 2 * Pad - (VNeeded ? Gutter : 0), Height - 2 * Pad - (HNeeded ? Gutter : 0));
            g.SetClip(clip);
            int y = Pad;
            for (int i = top; i < lines.Count && y < clip.Bottom; i++, y += LineH)
            {
                string l = lines[i];
                int tab = l.IndexOf('\t');
                string main = tab < 0 ? l : l.Substring(0, tab), sub = tab < 0 ? null : l.Substring(tab + 1);
                int x = Pad - left;
                TextRenderer.DrawText(g, main, Font, new Point(x, y + Theme.S(2)), Theme.FG, F);
                if (sub != null)
                {
                    x += TextRenderer.MeasureText(g, main, Font, Size.Empty, F).Width + Theme.S(16);
                    TextRenderer.DrawText(g, sub, Font, new Point(x, y + Theme.S(2)), Theme.MUTED, F);
                }
            }
            g.ResetClip();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    /// <summary>About window: logo, name, version, author, description and licence, repository link, speed tip, shortcuts.</summary>
    internal sealed class AboutForm : AppDialog
    {
        const string RepoUrl = "https://github.com/sphrd/DiskSpaceAnalyzer";

        static readonly string[,] Shortcuts =
        {
            { "Enter", "Open the selected folder or file" },
            { "Backspace", "Up to the parent folder" },
            { "F5", "Rescan the selected folder" },
            { "Ctrl+D", "Add to the delete list" },
            { "Ctrl+Shift+D", "Delete permanently (also Del)" },
            { "Ctrl+F", "Search files" },
            { "Ctrl+C", "Copy path" },
            { "F1", "About" },
        };

        public AboutForm(Action checkForUpdates) : base("About Disk Space Analyzer")
        {
            ClientSize = new Size(Theme.S(560), ClientSize.Height);
            int y = ContentTop;
            AddLabel("Disk Space Analyzer", HeadingFont, Theme.FG, ref y, Theme.S(4));
            AddLabel("Version " + Updater.Short(Updater.Current), Theme.FONT, Theme.FG, ref y, Theme.S(2));
            AddLabel("Author: sphrd", Theme.FONT, Theme.FG, ref y, Theme.S(12));
            AddLabel("Fast disk space analyzer and cleaner for Windows.\nFree and open source (MIT licence).", Theme.FONT_9, Theme.MUTED, ref y, Theme.S(8));
            var link = AddLabel("github.com/sphrd/DiskSpaceAnalyzer", Theme.FONT_9, Theme.ACCENT, ref y, 0);
            link.Cursor = Cursors.Hand;
            link.MouseEnter += (s, e) => link.ForeColor = Theme.ACCENT_ACTIVE;
            link.MouseLeave += (s, e) => link.ForeColor = Theme.ACCENT;
            link.Click += (s, e) => Updater.Open(RepoUrl);
            y += Theme.S(16);
            AddLabel("Faster scans", Theme.FONT_BOLD, Theme.FG, ref y, Theme.S(2));
            bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            AddLabel(admin
                ? "Running as administrator: whole NTFS drives are read straight from their master file table, about a million files in seconds."
                : "Run as administrator (button in the toolbar) to read whole NTFS drives straight from their master file table: " +
                  "about a million files in seconds, many times faster than going folder by folder.", Theme.FONT_9, Theme.MUTED, ref y, Theme.S(12));
            AddLabel("Keyboard shortcuts", Theme.FONT_BOLD, Theme.FG, ref y, Theme.S(4));
            int keyW = Theme.S(90), rowH = Theme.FONT_9.Height + Theme.S(3);
            for (int i = 0; i < Shortcuts.GetLength(0); i++)
            {
                Controls.Add(new Label { Text = Shortcuts[i, 0], Font = Theme.FONT_9, ForeColor = Theme.FG, BackColor = Theme.BG, UseMnemonic = false,
                    Bounds = new Rectangle(ContentX, y, keyW, rowH) });
                Controls.Add(new Label { Text = Shortcuts[i, 1], Font = Theme.FONT_9, ForeColor = Theme.MUTED, BackColor = Theme.BG, UseMnemonic = false,
                    Bounds = new Rectangle(ContentX + keyW, y, ContentWidth - keyW, rowH) });
                y += rowH;
            }
            ClientSize = new Size(ClientSize.Width, y + Theme.S(84));
            SetButtons(Button("Close", Close), Button("Check for updates", () => { Close(); if (checkForUpdates != null) checkForUpdates(); }));
        }
    }

    /// <summary>
    /// Update window, in the same frame as About: a heading, a line of text, a progress bar while downloading, and the
    /// buttons for the current step. UpdateManager switches it between the steps.
    /// </summary>
    internal sealed class UpdateForm : AppDialog
    {
        readonly Label heading, body;
        double fraction = -1;

        public UpdateForm() : base("Disk Space Analyzer - updates")
        {
            int y = ContentTop;
            heading = AddLabel(" ", HeadingFont, Theme.FG, ref y, Theme.S(8));
            body = AddLabel(" ", Theme.FONT, Theme.FG, ref y, 0);
            body.AutoEllipsis = true;
        }

        public void SetStep(string head, string text, double progress, params KeyValuePair<string, Action>[] items)
        {
            heading.Text = head;
            body.Text = text;
            fraction = progress;
            body.Height = Math.Max(Theme.S(20), ButtonsTop - body.Top - Theme.S(40));
            SetButtons(items);
            body.Height = Math.Max(Theme.S(20), ButtonsTop - body.Top - (progress >= 0 ? Theme.S(40) : Theme.S(12)));
            Invalidate();
        }

        public void SetProgress(string text, double progress)
        {
            body.Text = text;
            fraction = progress;
            Invalidate(ProgressRect);
        }

        Rectangle ProgressRect { get { return new Rectangle(ContentX, ButtonsTop - Theme.S(28), ContentWidth, Theme.S(8)); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (fraction < 0) return;
            var bar = ProgressRect;
            Theme.FillRound(e.Graphics, bar, bar.Height / 2, Theme.BORDER, Theme.BORDER);
            int w = (int)Math.Round(bar.Width * Math.Min(1, fraction));
            if (w > 0) Theme.FillRound(e.Graphics, new Rectangle(bar.X, bar.Y, Math.Max(w, bar.Height), bar.Height), bar.Height / 2, Theme.ACCENT, Theme.ACCENT);
        }
    }
}
