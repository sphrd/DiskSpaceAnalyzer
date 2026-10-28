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
    /// Update flow shown in the update window (same frame as About): checking, up to date, update available,
    /// downloading with progress and Cancel, installed with Restart now, and the error cases. Automatic checks stay
    /// silent unless there is an update; manual checks always open the window.
    /// </summary>
    internal sealed class UpdateManager
    {
        readonly Form owner;
        UpdateForm form;
        ReleaseInfo release;
        CancellationTokenSource cancel;
        bool checking;

        /// <summary>Asked before restarting; false while work is running that must not be cut off (permanent delete).</summary>
        public Func<bool> CanRestart = () => true;

        public UpdateManager(Form owner) { this.owner = owner; }

        static KeyValuePair<string, Action> B(string text, Action a) { return new KeyValuePair<string, Action>(text, a); }

        /// <summary>The update window, created on first use and brought to the front.</summary>
        UpdateForm Window()
        {
            if (form == null || form.IsDisposed)
            {
                form = new UpdateForm();
                form.FormClosed += (s, e) => { if (cancel != null) cancel.Cancel(); };
                form.Show(owner);
            }
            else form.Activate();
            return form;
        }

        void CloseWindow() { if (form != null && !form.IsDisposed) form.Close(); }

        /// <summary>Checks GitHub in the background.</summary>
        public async void Check(bool manual)
        {
            if (checking || cancel != null) { if (manual && form != null && !form.IsDisposed) form.Activate(); return; }
            checking = true;
            if (manual) Window().SetStep("Checking for updates...", "Looking for the latest release on GitHub.", -1, B("Close", CloseWindow));
            ReleaseInfo r = null;
            Exception error = null;
            try { r = await Task.Run(() => Updater.GetLatest()); }
            catch (Exception ex) { error = ex; }
            checking = false;
            if (owner.IsDisposed) return;
            bool windowOpen = form != null && !form.IsDisposed;
            if (manual && !windowOpen) return;                      // closed while checking

            if (error != null || r == null)
            {
                if (manual) Window().SetStep("Update check failed", "Could not check for updates: " + FirstLine(error) + ".", -1,
                    B("Close", CloseWindow), B("Open download page", OpenPage));
                return;
            }
            release = r;
            if (r.Version <= Updater.Current)
            {
                if (manual) Window().SetStep("You're up to date",
                    string.Format("Version {0} is the latest version.", Updater.Short(Updater.Current)), -1,
                    B("Close", CloseWindow), B("What's new", OpenPage));
                return;
            }
            Offer();
        }

        void Offer()
        {
            string msg = string.Format("Version {0} is available -- you have {1}.", Updater.Short(release.Version), Updater.Short(Updater.Current));
            if (release.AssetUrl == null)
                Window().SetStep("Update available", msg, -1, B("Open download page", OpenPage), B("Later", CloseWindow));
            else
                Window().SetStep("Update available", msg, -1, B("Update now", StartUpdate), B("What's new", OpenPage), B("Later", CloseWindow));
        }

        void OpenPage() { Updater.Open(release != null ? release.PageUrl : Updater.ReleasesPage); }

        async void StartUpdate()
        {
            if (!Updater.FolderWritable())
            {
                Window().SetStep("Can't update here",
                    "This folder can't be written to, so the update can't be installed here -- download it from the release page.", -1,
                    B("Open download page", OpenPage), B("Later", CloseWindow));
                return;
            }
            cancel = new CancellationTokenSource();
            var token = cancel.Token;
            string label = "Version " + Updater.Short(release.Version);
            var w = Window();
            w.SetStep("Downloading update", label + "...", 0, B("Cancel", () => { if (cancel != null) cancel.Cancel(); }));
            long lastTick = 0;
            Action<long, long> progress = (done, total) =>
            {
                long now = Environment.TickCount;
                if (now - Interlocked.Read(ref lastTick) < 100 && done < total) return;
                Interlocked.Exchange(ref lastTick, now);
                string t = total > 0
                    ? string.Format("{0}: {1} of {2} ({3:0} %)", label, Fmt.Size(done), Fmt.Size(total), 100.0 * done / total)
                    : string.Format("{0}: {1}", label, Fmt.Size(done));
                double f = total > 0 ? (double)done / total : 0;
                if (!w.IsDisposed) w.BeginInvoke(new Action(() => { if (!w.IsDisposed) w.SetProgress(t, f); }));
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
            if (owner.IsDisposed) return;
            bool windowOpen = form != null && !form.IsDisposed;
            if (error is OperationCanceledException)
            {
                Updater.CleanUp();
                if (windowOpen) Offer();
                return;
            }
            if (error != null)
            {
                Updater.CleanUp();
                Window().SetStep("Update failed", FirstLine(error) + ". You can download it from the release page.", -1,
                    B("Open download page", OpenPage), B("Close", CloseWindow));
                return;
            }
            Window().SetStep("Update installed",
                string.Format("Version {0} is installed -- restart the app to use it.", Updater.Short(release.Version)), -1,
                B("Restart now", Restart), B("Later", CloseWindow));
        }

        void Restart()
        {
            if (!CanRestart())
            {
                Window().SetProgress("Wait until the current delete has finished, then restart.", -1);
                return;
            }
            try { Process.Start(new ProcessStartInfo(Updater.ExePath) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                Window().SetProgress("Could not restart: " + FirstLine(ex) + ". Start the app again yourself.", -1);
                return;
            }
            CloseWindow();
            owner.Close();
        }

        static string FirstLine(Exception ex)
        {
            if (ex == null) return "Unknown error";
            var we = ex as WebException;
            var hr = we != null ? we.Response as HttpWebResponse : null;
            if (hr != null) return "GitHub answered " + (int)hr.StatusCode + " " + hr.StatusDescription;
            return ex.Message.Split('\r', '\n')[0].TrimEnd('.');
        }
    }
}