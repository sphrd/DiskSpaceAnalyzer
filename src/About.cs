using System;
using System.Drawing;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>About window: logo, name, version, author, repository link, licence. Dark, like the main window.</summary>
    internal sealed class AboutForm : Form
    {
        const string RepoUrl = "https://github.com/sphrd/DiskSpaceAnalyzer";

        public AboutForm(Action checkForUpdates)
        {
            Text = "About Disk Space Analyzer";
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

            int x = Theme.S(140), w = ClientSize.Width - x - Theme.S(24), y = Theme.S(28);
            Add(new Label { Text = "Disk Space Analyzer", Font = new Font("Segoe UI", 16f, FontStyle.Bold), ForeColor = Theme.FG }, x, ref y, w, Theme.S(4));
            Add(new Label { Text = "Version " + Updater.Short(Updater.Current), ForeColor = Theme.FG }, x, ref y, w, Theme.S(2));
            Add(new Label { Text = "Author: sphrd", ForeColor = Theme.FG }, x, ref y, w, Theme.S(12));
            Add(new Label { Text = "Fast disk space analyzer and cleaner for Windows.\nFree and open source (MIT licence).", Font = Theme.FONT_9, ForeColor = Theme.MUTED }, x, ref y, w, Theme.S(8));
            var link = new Label { Text = "github.com/sphrd/DiskSpaceAnalyzer", Font = Theme.FONT_9, ForeColor = Theme.ACCENT, Cursor = Cursors.Hand };
            link.MouseEnter += (s, e) => link.ForeColor = Theme.ACCENT_ACTIVE;
            link.MouseLeave += (s, e) => link.ForeColor = Theme.ACCENT;
            link.Click += (s, e) => Updater.Open(RepoUrl);
            Add(link, x, ref y, w, 0);

            var close = new RoundButton("Close", ButtonKind.Primary) { MinChars = 8 };
            var check = new RoundButton("Check for updates", ButtonKind.Secondary);
            close.Click += (s, e) => Close();
            check.Click += (s, e) => { Close(); if (checkForUpdates != null) checkForUpdates(); };
            var cs = close.GetPreferredSize(Size.Empty); var ks = check.GetPreferredSize(Size.Empty);
            int by = ClientSize.Height - Theme.S(20) - cs.Height;
            close.Bounds = new Rectangle(ClientSize.Width - Theme.S(24) - cs.Width, by, cs.Width, cs.Height);
            check.Bounds = new Rectangle(close.Left - Theme.S(8) - ks.Width, by, ks.Width, ks.Height);
            Controls.Add(close);
            Controls.Add(check);
        }

        void Add(Label l, int x, ref int y, int w, int gapAfter)
        {
            l.AutoSize = false;
            l.UseMnemonic = false;
            l.BackColor = Theme.BG;
            if (l.Font == null || l.Font == DefaultFont) l.Font = Theme.FONT;
            int h = TextRenderer.MeasureText(l.Text, l.Font, new Size(w, 0), TextFormatFlags.WordBreak).Height;
            l.Bounds = new Rectangle(x, y, w, h);
            Controls.Add(l);
            y += h + gapAfter;
        }

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
}
