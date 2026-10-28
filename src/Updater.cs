using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    internal sealed class ReleaseInfo
    {
        public Version Version;
        public string Tag, PageUrl, AssetUrl, Sha256;   // Sha256 is null when GitHub does not report a digest
        public long AssetSize;
    }

    /// <summary>
    /// Self-update from the GitHub releases of this repository: find the latest release, download its exe next to the
    /// running one, verify it (size, and SHA-256 when GitHub reports a digest), then swap the files. Windows allows a
    /// running exe to be renamed, so the old one becomes "&lt;exe&gt;.old" and is deleted at the next start.
    /// </summary>
    internal static class Updater
    {
        const string Repo = "sphrd/DiskSpaceAnalyzer";
        const string AssetName = "DiskSpaceAnalyzer.exe";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

        static Updater()
        {
            // .NET Framework 4.x defaults to old TLS versions; GitHub requires TLS 1.2.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        }

        public static Version Current { get { return Normalize(Assembly.GetExecutingAssembly().GetName().Version); } }

        public static string ExePath { get { return Application.ExecutablePath; } }

        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        public static string Short(Version v)
        {
            return v.Revision > 0 ? v.ToString(4) : v.ToString(3);
        }

        static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "DiskSpaceAnalyzer/" + Short(Current);   // GitHub rejects requests without a User-Agent
            req.Timeout = 15000;
            req.ReadWriteTimeout = 30000;
            return req;
        }

        /// <summary>Latest published release (drafts and pre-releases excluded). Throws on network or format errors.</summary>
        public static ReleaseInfo GetLatest()
        {
            var req = Request("https://api.github.com/repos/" + Repo + "/releases/latest");
            req.Accept = "application/vnd.github+json";
            string json;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream())) json = sr.ReadToEnd();

            var o = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var info = new ReleaseInfo { Tag = (string)o["tag_name"], PageUrl = (string)o["html_url"] };
            info.Version = Normalize(Version.Parse(info.Tag.TrimStart('v', 'V')));
            foreach (Dictionary<string, object> a in (object[])o["assets"])
            {
                if (!string.Equals((string)a["name"], AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                info.AssetUrl = (string)a["browser_download_url"];
                info.AssetSize = Convert.ToInt64(a["size"]);
                object d;
                if (a.TryGetValue("digest", out d) && d is string && ((string)d).StartsWith("sha256:"))
                    info.Sha256 = ((string)d).Substring(7).ToLowerInvariant();
            }
            return info;
        }

        /// <summary>True if a file can be created next to the running exe (the swap needs it).</summary>
        public static bool FolderWritable()
        {
            try
            {
                string probe = Path.Combine(Path.GetDirectoryName(ExePath), ".dsa-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Downloads and verifies the release exe; returns the path of the downloaded file.</summary>
        public static string Download(ReleaseInfo r, Action<long, long> progress, CancellationToken cancel)
        {
            string target = ExePath + ".download";
            var req = Request(r.AssetUrl);
            using (var resp = req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write))
            using (var sha = SHA256.Create())
            {
                var buf = new byte[81920];
                long done = 0, total = r.AssetSize > 0 ? r.AssetSize : resp.ContentLength;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, n);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    progress(done, total);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (r.AssetSize > 0 && done != r.AssetSize)
                    throw new IOException(string.Format("download incomplete ({0:N0} of {1:N0} bytes)", done, r.AssetSize));
                string hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (r.Sha256 != null && hash != r.Sha256)
                    throw new IOException("downloaded file does not match the release checksum");
            }
            return target;
        }

        /// <summary>Puts the downloaded exe in place of the running one; the running one is kept as .old until next start.</summary>
        public static void Install(string downloaded)
        {
            string exe = ExePath, old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(downloaded, exe); }
            catch (Exception)
            {
                File.Move(old, exe);
                throw;
            }
        }

        /// <summary>
        /// Removes leftovers of an earlier update (the replaced exe and unfinished downloads). Right after "Restart now"
        /// the previous process may still be closing and holding the old exe, so this retries in the background for ~15 s.
        /// </summary>
        public static void CleanUp()
        {
            if (!TryCleanUp())
                new Thread(() =>
                {
                    for (int i = 0; i < 30 && !TryCleanUp(); i++) Thread.Sleep(500);
                }) { IsBackground = true }.Start();
        }

        static bool TryCleanUp()
        {
            bool ok = true;
            foreach (var p in new[] { ExePath + ".old", ExePath + ".download" })
                try { if (File.Exists(p)) File.Delete(p); } catch (Exception) { ok = false; }
            return ok;
        }

        public static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Attention banner under the toolbar for updates (design language §2.4: warn-bg panel, warn-border outline,
    /// warn-fg text on the left, buttons on the right). Hidden unless there is something to say.
    /// </summary>
    internal sealed class UpdateBanner : Control
    {
        readonly Label text;
        readonly RoundButton primary, secondary, tertiary;
        Action onPrimary, onSecondary, onTertiary;
        ReleaseInfo release;
        CancellationTokenSource cancel;
        bool checking;

        /// <summary>Asked before restarting; false while work is running that must not be cut off (permanent delete).</summary>
        public Func<bool> CanRestart = () => true;

        public UpdateBanner()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.BG;
            Visible = false;
            text = new Label { AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Font = Theme.FONT_9, ForeColor = Theme.WARN_FG, BackColor = Theme.WARN_BG, TextAlign = ContentAlignment.MiddleLeft };
            primary = new RoundButton("", ButtonKind.Warn) { Font = new Font(Theme.FONT_9, FontStyle.Bold) };
            secondary = new RoundButton("", ButtonKind.Secondary) { Font = Theme.FONT_9 };
            tertiary = new RoundButton("", ButtonKind.Secondary) { Font = Theme.FONT_9 };
            primary.Click += (s, e) => { if (onPrimary != null) onPrimary(); };
            secondary.Click += (s, e) => { if (onSecondary != null) onSecondary(); };
            tertiary.Click += (s, e) => { if (onTertiary != null) onTertiary(); };
            Controls.AddRange(new Control[] { text, primary, secondary, tertiary });
            Height = Theme.FONT_9.Height + Theme.S(12) + Theme.S(16) + Theme.S(10);
        }

        void ShowMessage(string message, string p, Action pa, string s = null, Action sa = null, string t = null, Action ta = null)
        {
            text.Text = message;
            Set(primary, p, pa, ref onPrimary);
            Set(secondary, s, sa, ref onSecondary);
            Set(tertiary, t, ta, ref onTertiary);
            Visible = true;
            PerformLayout();
            Invalidate();
        }

        static void Set(RoundButton b, string label, Action action, ref Action slot)
        {
            b.Visible = label != null;
            b.Text = label ?? "";
            b.Enabled = true;
            slot = action;
        }

        void Dismiss() { Visible = false; }

        Rectangle Box { get { return new Rectangle(0, 0, Width, Height - Theme.S(10)); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (text == null) return;
            var b = Box;
            int x = b.Right - Theme.S(10), gap = Theme.S(6);
            foreach (var btn in new[] { tertiary, secondary, primary })
            {
                if (!btn.Visible) continue;
                var ps = btn.GetPreferredSize(Size.Empty);
                x -= ps.Width;
                btn.Bounds = new Rectangle(x, b.Y + (b.Height - ps.Height) / 2, ps.Width, ps.Height);
                x -= gap;
            }
            int left = b.X + Theme.S(14);
            text.Bounds = new Rectangle(left, b.Y + 2, Math.Max(0, x - left - Theme.S(6)), b.Height - 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Theme.FillRound(e.Graphics, Box, Theme.S(12), Theme.WARN_BG, Theme.WARN_BORDER);
        }

        // ------------------------------------------------------------------ flow

        /// <summary>Checks GitHub in the background. Automatic checks stay silent unless an update exists.</summary>
        public async void Check(bool manual)
        {
            if (checking || (cancel != null)) return;
            checking = true;
            if (manual) ShowMessage("Checking for updates...", null, null);
            ReleaseInfo r = null;
            Exception error = null;
            try { r = await Task.Run(() => Updater.GetLatest()); }
            catch (Exception ex) { error = ex; }
            checking = false;
            if (IsDisposed) return;

            if (error != null || r == null)
            {
                if (manual) ShowMessage("Could not check for updates: " + FirstLine(error) + ".", "Open download page", OpenPage, "Close", Dismiss);
                return;
            }
            release = r;
            if (r.Version <= Updater.Current)
            {
                if (manual) ShowMessage(string.Format("You have the latest version ({0}).", Updater.Short(Updater.Current)), "Close", Dismiss);
                return;
            }
            Offer();
        }

        void Offer()
        {
            string msg = string.Format("Version {0} is available -- you have {1}.", Updater.Short(release.Version), Updater.Short(Updater.Current));
            if (release.AssetUrl == null)
                ShowMessage(msg, "Open download page", OpenPage, "Later", Dismiss);
            else
                ShowMessage(msg, "Update now", StartUpdate, "What's new", OpenPage, "Later", Dismiss);
        }

        void OpenPage() { Updater.Open(release != null ? release.PageUrl : Updater.ReleasesPage); }

        async void StartUpdate()
        {
            if (!Updater.FolderWritable())
            {
                ShowMessage("This folder can't be written to, so the update can't be installed here -- download it from the release page.",
                    "Open download page", OpenPage, "Later", Dismiss);
                return;
            }
            cancel = new CancellationTokenSource();
            var token = cancel.Token;
            string label = "Downloading version " + Updater.Short(release.Version);
            ShowMessage(label + "...", null, null, "Cancel", () => cancel.Cancel());
            long lastTick = 0;
            Action<long, long> progress = (done, total) =>
            {
                long now = Environment.TickCount;
                if (now - Interlocked.Read(ref lastTick) < 100 && done < total) return;
                Interlocked.Exchange(ref lastTick, now);
                string t = total > 0
                    ? string.Format("{0}: {1} of {2} ({3:0} %)", label, Fmt.Size(done), Fmt.Size(total), 100.0 * done / total)
                    : string.Format("{0}: {1}", label, Fmt.Size(done));
                if (!IsDisposed) BeginInvoke(new Action(() => text.Text = t));
            };
            Exception error = null;
            try
            {
                await Task.Run(() =>
                {
                    string file = Updater.Download(release, progress, token);
                    token.ThrowIfCancellationRequested();
                    Updater.Install(file);
                });
            }
            catch (Exception ex) { error = ex; }
            cancel = null;
            if (IsDisposed) return;
            if (error is OperationCanceledException)
            {
                Updater.CleanUp();
                Offer();
                return;
            }
            if (error != null)
            {
                Updater.CleanUp();
                ShowMessage("Update failed: " + FirstLine(error) + ". You can download it from the release page.", "Open download page", OpenPage, "Close", Dismiss);
                return;
            }
            ShowMessage(string.Format("Version {0} is installed -- restart the app to use it.", Updater.Short(release.Version)),
                "Restart now", Restart, "Later", Dismiss);
        }

        void Restart()
        {
            if (!CanRestart())
            {
                text.Text = "Wait until the current delete has finished, then restart.";
                return;
            }
            try { Process.Start(new ProcessStartInfo(Updater.ExePath) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                text.Text = "Could not restart: " + FirstLine(ex) + ". Start the app again yourself.";
                return;
            }
            var f = FindForm();
            if (f != null) f.Close();
        }

        static string FirstLine(Exception ex)
        {
            if (ex == null) return "unknown error";
            var we = ex as WebException;
            var hr = we != null ? we.Response as HttpWebResponse : null;
            if (hr != null) return "GitHub answered " + (int)hr.StatusCode + " " + hr.StatusDescription;
            return ex.Message.Split('\r', '\n')[0].TrimEnd('.');
        }
    }
}
