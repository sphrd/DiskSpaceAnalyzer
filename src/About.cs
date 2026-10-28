using System;
using System.Collections.Generic;
using System.Drawing;
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
            Theme.DrawLogo(e.Graphics, new Rectangle(Theme.S(32), Theme.S(32), d, d), 1f);
        }
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
            { "Insert", "Add to the delete list" },
            { "Del", "Delete permanently" },
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
