using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DiskSpaceAnalyzer
{
    /// <summary>An installed program (from the registry's uninstall entries) or a Microsoft Store app.</summary>
    internal sealed class InstalledApp
    {
        public string Name = "", Publisher = "", Version = "", InstallDate = "", Location = "";
        public string UninstallString, QuietUninstallString, ProductCode, KeyName;
        public RegistryHive Hive;
        public RegistryView View;
        public string KeyPath;                  // ...\Uninstall\<KeyName>
        public bool IsMsi, IsStore;
        public string PackageFullName, PackageFamilyName;
        public long Size, EstimatedSize;
        public bool Uninstalled, Expanded;
        /// <summary>Another app with the same program folder (its size is counted there, not here).</summary>
        public string SharedWith;
        public List<Leftover> Leftovers;

        public string UninstallKeyDisplay { get { return (Hive == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\") + KeyPath; } }
    }

    /// <summary>Something an app left behind: a file or folder, or a registry key.</summary>
    internal sealed class Leftover
    {
        public string Label = "", Kind = "", Path = "";
        public bool IsDir, IsRegistry, Checked = true;
        public RegistryHive Hive;
        public RegistryView View;
        public string SubKey;                   // registry: path under the hive
        public long Size;
        public string Note = "";
    }

    /// <summary>
    /// Apps tab: installed programs (registry uninstall entries, both views, machine and user) and Store apps, with their
    /// size on disk. Uninstall runs the app's own uninstaller first, waits for it, then looks for what was left behind
    /// (folders in AppData, ProgramData, Program Files, Start menu shortcuts, registry keys) and lets the user check what to
    /// remove. Registry keys are exported to a .reg backup before they are deleted.
    /// </summary>
    internal sealed partial class MainForm
    {
        Page pApps;
        List<InstalledApp> apps;
        int appsGen;
        bool appsLoading;
        SearchBox appSearch;
        RoundButton btnAppUninstall, btnAppLeftovers;
        ToolStripMenuItem miLookLeftovers;
        volatile bool waitContinue;
        /// <summary>Self-test: extra uninstall roots to read (e.g. a fake app's key) and no Store apps.</summary>
        internal bool AppsTestMode;

        const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        static string BackupFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSpaceAnalyzer", "registry-backups"); }
        }

        Page BuildAppsPage()
        {
            var refresh = Btn("Refresh", ButtonKind.Secondary, "Read the list of installed apps again", () => { apps = null; FillApps(); });
            refresh.Glyph = Theme.DrawRefresh;
            appSearch = new SearchBox("Search apps");
            appSearch.TextChangedDelayed += (s, e) => ShowApps();
            btnAppUninstall = Btn("Uninstall...", ButtonKind.Warn, "Run the selected app's own uninstaller, then choose which leftovers to remove", UninstallSelectedApp);
            btnAppLeftovers = Btn("Remove leftovers", ButtonKind.Warn, "Delete the checked leftovers (registry keys are backed up to a .reg file first)", RemoveCheckedLeftovers);
            foreach (var b in new[] { refresh, btnAppUninstall, btnAppLeftovers }) b.Compact = true;
            btnAppUninstall.Tag = btnAppLeftovers.Tag = "right";
            var list = NewList(new[] { "Name", "Size", "% of apps", "Publisher", "Version", "Installed", "Location" },
                new[] { "name", "size", "size", "cat", "note", "mod", "folder" }, new[] { 260, 90, 130, 170, 100, 100, 360 }, 2);
            list.GlyphClicked += AppGlyph;
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Space) { ToggleSelectedLeftovers(); e.Handled = true; } };
            list.SelectedIndexChanged += (s, e) => UpdateAppButtons();
            var p = AddPage("Apps", list, MakeBar(refresh, appSearch, btnAppUninstall, btnAppLeftovers));
            p.ChartTitle = "Installed apps";
            p.ChartScope = "of apps";
            p.Empty = "No apps found.";
            p.Fill = FillApps;

            miLookLeftovers = new ToolStripMenuItem("Look for leftovers", null, (s, e) => LookForSelectedLeftovers()) { ForeColor = Theme.FG };
            menu.Items.Insert(menu.Items.IndexOf(miRescan) + 1, miLookLeftovers);
            menu.Opening += (s, e) =>
            {
                var a = SelectedApp();
                miLookLeftovers.Visible = lastView == list;
                miLookLeftovers.Enabled = a != null && !busy && !a.IsStore;
            };
            return p;
        }

        // ---------------------------------------------------------------- list

        void FillApps()
        {
            if (apps != null) { ShowApps(); return; }
            if (appsLoading) return;
            appsLoading = true;
            int gen = ++appsGen;
            pApps.Caption.Text = "Reading the installed apps...";
            SetRows(pApps.List, new List<ListViewItem>(), "Reading the installed apps...");
            UpdateAppButtons();
            bool testMode = AppsTestMode;
            List<InstalledApp> found = null;
            Task.Factory.StartNew(() =>
            {
                found = ReadInstalledApps(testMode);
                // Sizes: from the scan when the folder was scanned, otherwise measured now.
                foreach (var a in found)
                {
                    if (!string.IsNullOrEmpty(a.Location) && Native.IsDirectory(a.Location))
                    {
                        DirNode n = null;
                        try { n = (DirNode)Invoke(new Func<DirNode>(() => FindScanned(a.Location, true))); } catch (Exception) { }
                        if (n != null) a.Size = n.Size;
                        else try { a.Size = new Scanner().Run(a.Location).Size; } catch (Exception) { a.Size = a.EstimatedSize; }
                    }
                    else a.Size = a.EstimatedSize;
                    if (a.IsStore && !string.IsNullOrEmpty(a.PackageFamilyName))
                    {
                        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", a.PackageFamilyName);
                        if (Native.IsDirectory(data)) try { a.Size += new Scanner().Run(data).Size; } catch (Exception) { }
                    }
                }
                // Apps sharing one program folder (e.g. Office and its language packs): the folder counts once.
                foreach (var g in found.Where(a => !string.IsNullOrEmpty(a.Location)).GroupBy(a => a.Location.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                {
                    var first = g.OrderByDescending(a => a.EstimatedSize).ThenBy(a => a.Name.Length).First();
                    foreach (var a in g) if (a != first) { a.Size = 0; a.SharedWith = first.Name; }
                }
            }).ContinueWith(t =>
            {
                appsLoading = false;
                if (gen != appsGen || IsDisposed) return;
                apps = t.IsFaulted || found == null ? new List<InstalledApp>() : found;
                ShowApps();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowApps()
        {
            var p = pApps;
            if (apps == null) return;
            string q = appSearch.Text.Trim();
            var shown = apps.Where(a => q.Length == 0 || a.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                a.Publisher.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            long total = shown.Sum(a => a.Uninstalled ? 0 : a.Size);
            var lv = p.List;
            var keepSel = lv.SelectedItems.Count == 1 ? ((ItemInfo)lv.SelectedItems[0].Tag).Data : null;
            string top = lv.TopItem != null ? ((ItemInfo)lv.TopItem.Tag).Name : null;
            var rows = new List<ListViewItem>();
            // Apps with leftovers first, then by size.
            foreach (var a in shown.OrderByDescending(a => a.Leftovers != null && a.Leftovers.Count > 0).ThenByDescending(a => a.Size))
            {
                bool hasLeft = a.Leftovers != null && a.Leftovers.Count > 0;
                long leftSize = hasLeft ? a.Leftovers.Sum(l => l.Size) : 0;
                var head = new ItemInfo
                {
                    Data = a, Name = a.Uninstalled ? a.Name + "  (uninstalled -- leftovers)" : a.Name, Size = a.Uninstalled ? leftSize : a.Size,
                    FolderIcon = true, Path = string.IsNullOrEmpty(a.Location) ? null : a.Location, Folder = a.Location,
                    Category = a.Publisher, Note = a.Version, Percent = total > 0 && !a.Uninstalled ? 100.0 * a.Size / total : 0,
                    Expandable = hasLeft, Expanded = a.Expanded, Check = hasLeft ? LeftoverState(a) : -1
                };
                string where = a.SharedWith != null ? "same folder as " + a.SharedWith : a.IsStore && string.IsNullOrEmpty(a.Location) ? "Microsoft Store" : a.Location;
                var lvi = Row(head, head.Name, a.Size > 0 || a.Uninstalled ? Fmt.Size(head.Size) : "", a.Uninstalled || a.SharedWith != null ? "" : head.Percent.ToString("0.0") + " %",
                    a.Publisher, a.Version, FormatInstallDate(a.InstallDate), where);
                if (a.SharedWith != null) lvi.ForeColor = Theme.MUTED;
                if (a.Uninstalled) lvi.ForeColor = Theme.WARN_FG;
                rows.Add(lvi);
                if (!hasLeft || !a.Expanded) continue;
                foreach (var l in a.Leftovers.OrderByDescending(x => x.Size))
                {
                    var it = new ItemInfo
                    {
                        Data = l, GroupOf = head, Name = l.Label, Size = l.Size, FolderIcon = l.IsDir || l.IsRegistry, Path = l.Path, Folder = l.Path,
                        Note = l.Kind, Check = l.Checked ? 1 : 0,
                        Targets = l.IsRegistry ? null : new List<DeleteTarget> { new DeleteTarget { Path = l.Path, Label = l.Label, IsDir = l.IsDir, Size = l.Size } }
                    };
                    rows.Add(Row(it, it.Name, l.IsRegistry ? "--" : Fmt.Size(l.Size), "", l.Kind, l.Note, "", l.Path));
                }
            }
            SetRows(lv, rows, apps.Count == 0 ? "No installed apps were found." : "No app matches \"" + q + "\".");
            if (top != null)
                foreach (ListViewItem it in lv.Items) if (((ItemInfo)it.Tag).Name == top) { try { lv.TopItem = it; } catch (Exception) { } break; }
            if (keepSel != null)
                foreach (ListViewItem it in lv.Items) if (((ItemInfo)it.Tag).Data == keepSel) { it.Selected = true; break; }
            int installed = apps.Count(a => !a.Uninstalled);
            p.Caption.Text = string.Format("{0:N0} apps, {1}{2}.  Select an app and press Uninstall: its own uninstaller runs first, then you choose which leftovers to remove.",
                installed, Fmt.Size(apps.Where(a => !a.Uninstalled).Sum(a => a.Size)), q.Length > 0 ? string.Format(" ({0:N0} shown)", shown.Count) : "");
            p.ChartSub = Fmt.Size(total) + " in apps";
            p.ChartTotal = 0;
            UpdateAppButtons();
            if (ActivePage == p) RefreshChart();
        }

        static string FormatInstallDate(string d)
        {
            if (string.IsNullOrEmpty(d)) return "";
            if (d.Length == 8 && d.All(char.IsDigit)) return d.Substring(0, 4) + "-" + d.Substring(4, 2) + "-" + d.Substring(6, 2);
            DateTime t;
            return DateTime.TryParse(d, out t) ? t.ToString("yyyy-MM-dd") : d;
        }

        static int LeftoverState(InstalledApp a)
        {
            int n = a.Leftovers.Count(l => l.Checked);
            return n == 0 ? 0 : n == a.Leftovers.Count ? 1 : 2;
        }

        InstalledApp SelectedApp()
        {
            var lv = pApps != null ? pApps.List : null;
            if (lv == null || lv.SelectedItems.Count != 1) return null;
            var info = (ItemInfo)lv.SelectedItems[0].Tag;
            return info.Data as InstalledApp ?? (info.GroupOf != null ? info.GroupOf.Data as InstalledApp : null);
        }

        void UpdateAppButtons()
        {
            if (btnAppUninstall == null) return;
            var a = SelectedApp();
            btnAppUninstall.Enabled = a != null && !a.Uninstalled && !busy && !scanning;
            btnAppLeftovers.Enabled = apps != null && apps.Any(x => x.Leftovers != null && x.Leftovers.Any(l => l.Checked)) && !busy && !scanning;
        }

        void AppGlyph(ListViewItem lvi, bool checkbox)
        {
            var info = (ItemInfo)lvi.Tag;
            var a = info.Data as InstalledApp;
            if (!checkbox) { if (a != null) { a.Expanded = !a.Expanded; ShowApps(); } return; }
            if (a != null && a.Leftovers != null) { bool on = LeftoverState(a) != 1; foreach (var l in a.Leftovers) l.Checked = on; }
            else { var l = info.Data as Leftover; if (l != null) l.Checked = !l.Checked; }
            ShowApps();
        }

        void ToggleSelectedLeftovers()
        {
            var sel = pApps.List.SelectedItems.Cast<ListViewItem>().Select(i => (ItemInfo)i.Tag).ToList();
            var ls = sel.SelectMany(i => i.Data is InstalledApp && ((InstalledApp)i.Data).Leftovers != null ? ((InstalledApp)i.Data).Leftovers
                : i.Data is Leftover ? new List<Leftover> { (Leftover)i.Data } : new List<Leftover>()).Distinct().ToList();
            if (ls.Count == 0) return;
            bool on = ls.Any(l => !l.Checked);
            foreach (var l in ls) l.Checked = on;
            ShowApps();
        }

        // ---------------------------------------------------------------- reading the installed apps

        List<InstalledApp> ReadInstalledApps(bool testMode)
        {
            var list = new List<InstalledApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = testMode
                ? new[] { Tuple.Create(RegistryHive.CurrentUser, RegistryView.Registry64) }
                : new[]
                {
                    Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry64), Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry32),
                    Tuple.Create(RegistryHive.CurrentUser, RegistryView.Registry64)
                };
            foreach (var r in roots)
            {
                try
                {
                    using (var hive = RegistryKey.OpenBaseKey(r.Item1, r.Item2))
                    using (var un = hive.OpenSubKey(UninstallPath))
                    {
                        if (un == null) continue;
                        foreach (var name in un.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = un.OpenSubKey(name))
                                {
                                    var a = ReadApp(k, name, r.Item1, r.Item2);
                                    if (a == null) continue;
                                    if (testMode && !a.Name.StartsWith("DSA SelfTest")) continue;
                                    if (seen.Add(a.Name + "|" + a.Version)) list.Add(a);
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                }
                catch (Exception) { }
            }
            if (!testMode) list.AddRange(ReadStoreApps().Where(a => seen.Add(a.Name + "|" + a.Version)));
            return list;
        }

        static string Str(RegistryKey k, string name)
        {
            var v = k.GetValue(name);
            return v == null ? "" : v.ToString().Trim();
        }

        static InstalledApp ReadApp(RegistryKey k, string keyName, RegistryHive hive, RegistryView view)
        {
            string name = Str(k, "DisplayName");
            if (name.Length == 0) return null;
            var sys = k.GetValue("SystemComponent");
            if (sys is int && (int)sys == 1) return null;
            if (Str(k, "ParentKeyName").Length > 0) return null;                    // updates of another product
            string rt = Str(k, "ReleaseType");
            if (rt.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 || rt.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            var wi = k.GetValue("WindowsInstaller");
            bool msi = wi is int && (int)wi == 1 && keyName.StartsWith("{");
            string un = Str(k, "UninstallString");
            if (un.Length == 0 && !msi) return null;
            var a = new InstalledApp
            {
                Name = name, Publisher = Str(k, "Publisher"), Version = Str(k, "DisplayVersion"), InstallDate = Str(k, "InstallDate"),
                UninstallString = un, QuietUninstallString = Str(k, "QuietUninstallString"), IsMsi = msi, ProductCode = msi ? keyName : null,
                KeyName = keyName, Hive = hive, View = view, KeyPath = UninstallPath + "\\" + keyName
            };
            var est = k.GetValue("EstimatedSize");
            if (est is int) a.EstimatedSize = (long)(int)est * 1024;
            a.Location = CleanFolder(Str(k, "InstallLocation"));
            if (a.Location.Length == 0) a.Location = FolderOfExe(Str(k, "DisplayIcon"));
            if (a.Location.Length == 0 && !msi) a.Location = FolderOfExe(un);
            return a;
        }

        static string CleanFolder(string p)
        {
            p = (p ?? "").Trim().Trim('"').TrimEnd('\\');
            if (p.Length < 4 || !Path.IsPathRooted(p)) return "";
            try { p = Environment.ExpandEnvironmentVariables(p); } catch (Exception) { }
            return IsSystemFolder(p) ? "" : p;
        }

        /// <summary>Folder of the exe in a command line or icon path ("C:\x\a.exe",0 / "C:\x\a.exe" /S), unless it is a shared place.</summary>
        static string FolderOfExe(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            string exe;
            cmd = cmd.Trim();
            if (cmd.StartsWith("\"")) { int e = cmd.IndexOf('"', 1); exe = e > 0 ? cmd.Substring(1, e - 1) : cmd.Trim('"'); }
            else { int e = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); exe = e > 0 ? cmd.Substring(0, e + 4) : cmd.Split(',')[0]; }
            exe = exe.Split(',')[0].Trim();
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !exe.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return "";
            string dir = Native.ParentDir(exe) ?? "";
            if (dir.Length < 4 || IsSystemFolder(dir)) return "";
            string low = dir.ToLowerInvariant();
            if (low.Contains(@"\package cache\") || low.Contains(@"\installer\") || low.Contains(@"\temp\")) return "";
            return dir;
        }

        static bool IsSystemFolder(string p)
        {
            string t = p.TrimEnd('\\');
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            var shared = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), Path.GetPathRoot(win)
            };
            if (t.Length <= 3) return true;
            if (IsWithin(t, win)) return true;
            return shared.Any(s => !string.IsNullOrEmpty(s) && string.Equals(s.TrimEnd('\\'), t, StringComparison.OrdinalIgnoreCase));
        }

        List<InstalledApp> ReadStoreApps()
        {
            var list = new List<InstalledApp>();
            try
            {
                const string script = "$ErrorActionPreference='SilentlyContinue'; Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -eq 'Store' } | " +
                    "ForEach-Object { [pscustomobject]@{ N=$_.Name; F=$_.PackageFullName; M=$_.PackageFamilyName; P=$_.Publisher; V=$_.Version.ToString(); L=$_.InstallLocation } } | ConvertTo-Json -Compress";
                string json = RunPowerShell(script, 60000);
                if (string.IsNullOrWhiteSpace(json)) return list;
                json = json.Trim();
                if (json.StartsWith("{")) json = "[" + json + "]";
                var items = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<Dictionary<string, object>>>(json);
                foreach (var d in items)
                {
                    Func<string, string> g = k => d.ContainsKey(k) && d[k] != null ? d[k].ToString() : "";
                    string loc = g("L");
                    var a = new InstalledApp
                    {
                        IsStore = true, Name = StoreDisplayName(loc, g("N")), Publisher = StorePublisher(g("P")), Version = g("V"),
                        PackageFullName = g("F"), PackageFamilyName = g("M"), Location = loc
                    };
                    try { if (Native.IsDirectory(loc)) a.InstallDate = Directory.GetCreationTime(loc).ToString("yyyyMMdd"); } catch (Exception) { }
                    list.Add(a);
                }
            }
            catch (Exception) { }
            return list;
        }

        static string RunPowerShell(string script, int timeoutMs)
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return ""; }
                return output.Result;
            }
        }

        /// <summary>The Store app's display name from its manifest (falls back to the package name).</summary>
        static string StoreDisplayName(string location, string fallback)
        {
            try
            {
                string m = Path.Combine(location ?? "", "AppxManifest.xml");
                if (File.Exists(m))
                {
                    string x = File.ReadAllText(m);
                    int i = x.IndexOf("<DisplayName>", StringComparison.Ordinal);
                    if (i >= 0)
                    {
                        int e = x.IndexOf("</DisplayName>", i, StringComparison.Ordinal);
                        string n = x.Substring(i + 13, e - i - 13).Trim();
                        if (n.Length > 0 && !n.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return n;
                    }
                }
            }
            catch (Exception) { }
            string f = fallback ?? "";
            int dot = f.LastIndexOf('.');
            return dot > 0 && dot < f.Length - 1 ? f.Substring(dot + 1) : f;
        }

        static string StorePublisher(string p)
        {
            int i = (p ?? "").IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return p ?? "";
            string s = p.Substring(i + 3);
            int e = s.IndexOf(',');
            return (e >= 0 ? s.Substring(0, e) : s).Trim().Trim('"');
        }

        // ---------------------------------------------------------------- uninstall

        async void UninstallSelectedApp()
        {
            var a = SelectedApp();
            if (a != null) await UninstallApp(a);
        }

        async Task UninstallApp(InstalledApp a)
        {
            if (a == null || a.Uninstalled || busy || scanning) return;
            if (SelfTestDir == null && MessageBox.Show(this,
                    string.Format("Uninstall {0}{1}?\n\nIts own uninstaller runs first (finish its steps). Then Disk Space Analyzer looks for what it left behind " +
                        "(settings, caches, shortcuts, registry keys) and lets you choose what to remove.", a.Name, a.Size > 0 ? " (" + Fmt.Size(a.Size) + ")" : ""),
                    AppName + " - uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            bool done = await RunUninstaller(a);
            if (!done)
            {
                statusLabel.Text = a.Name + " is still installed: its uninstaller was cancelled or failed.";
                return;
            }
            a.Uninstalled = true;
            await FindLeftovers(a);
            a.Expanded = true;
            if (a.Leftovers.Count == 0) { apps.Remove(a); statusLabel.Text = a.Name + " was removed completely."; }
            else statusLabel.Text = string.Format("Uninstalled {0}. Found {1:N0} leftover{2} ({3}): check what to remove, then press Remove leftovers.",
                a.Name, a.Leftovers.Count, a.Leftovers.Count == 1 ? "" : "s", Fmt.Size(a.Leftovers.Sum(l => l.Size)));
            ViewsChanged();
            ShowApps();
            RevealApp(a);
        }

        /// <summary>Selects an app's row and scrolls it into view (the list otherwise keeps its scroll position).</summary>
        void RevealApp(InstalledApp a)
        {
            var lv = pApps.List;
            foreach (ListViewItem it in lv.Items)
            {
                if (((ItemInfo)it.Tag).Data != a) continue;
                lv.SelectedItems.Clear();
                it.Selected = true;
                try { lv.TopItem = it; } catch (Exception) { it.EnsureVisible(); }
                break;
            }
        }

        bool StillInstalled(InstalledApp a)
        {
            if (a.IsStore)
            {
                string r = RunPowerShell("(Get-AppxPackage -Name '" + StoreNameFromFull(a.PackageFullName).Replace("'", "''") + "' | Measure-Object).Count", 30000);
                return r.Trim() != "0" && r.Trim().Length > 0;
            }
            try
            {
                using (var hive = RegistryKey.OpenBaseKey(a.Hive, a.View))
                using (var k = hive.OpenSubKey(a.KeyPath)) return k != null;
            }
            catch (Exception) { return false; }
        }

        static string StoreNameFromFull(string full)
        {
            int i = (full ?? "").IndexOf('_');
            return i > 0 ? full.Substring(0, i) : full ?? "";
        }

        /// <summary>Runs the app's uninstaller and waits until its uninstall entry is gone (or the user presses Continue).</summary>
        async Task<bool> RunUninstaller(InstalledApp a)
        {
            ProcessStartInfo psi;
            if (a.IsStore)
                psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '" + a.PackageFullName.Replace("'", "''") + "'\"")
                { UseShellExecute = false, CreateNoWindow = true };
            else if (a.IsMsi)
                psi = new ProcessStartInfo("msiexec.exe", "/x " + a.ProductCode) { UseShellExecute = true };
            else
            {
                string cmd = a.UninstallString.Trim(), exe, args;
                if (cmd.StartsWith("\"")) { int e = cmd.IndexOf('"', 1); exe = cmd.Substring(1, e - 1); args = cmd.Substring(e + 1).Trim(); }
                else
                {
                    int e = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                    if (e > 0) { exe = cmd.Substring(0, e + 4); args = cmd.Substring(e + 4).Trim(); }
                    else { exe = "cmd.exe"; args = "/c " + cmd; }
                }
                psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
                if (SelfTestDir != null) psi.WindowStyle = ProcessWindowStyle.Hidden;
            }

            busy = true;
            UpdateButtons();
            overlay.ShowBusy("Uninstalling " + a.Name + "...", true);
            overlay.SetStatus("Uninstalling " + a.Name + ": waiting for its uninstaller...",
                "Follow the uninstaller's own steps. When it is done, the leftovers are looked for. Press Continue if it has finished but this keeps waiting.");
            string cancelText = overlay.CancelButton.Text;
            overlay.CancelButton.Text = "Continue";
            overlay.CancelButton.Enabled = true;
            waitContinue = false;
            EventHandler onContinue = (s, e) => waitContinue = true;
            overlay.CancelButton.Click += onContinue;
            bool started = true;
            try
            {
                await Task.Run(() =>
                {
                    Process p = null;
                    try { p = Process.Start(psi); } catch (Exception) { started = false; return; }   // e.g. the user declined the UAC prompt
                    // Wait for the process, then for the uninstall entry to disappear (many uninstallers hand over to a copy of
                    // themselves and exit at once), up to 30 minutes or until Continue.
                    var sw = Stopwatch.StartNew();
                    while (!waitContinue && p != null && !p.HasExited && sw.Elapsed.TotalMinutes < 30) System.Threading.Thread.Sleep(300);
                    while (!waitContinue && StillInstalled(a) && sw.Elapsed.TotalMinutes < 30)
                    {
                        System.Threading.Thread.Sleep(1000);
                        if (sw.Elapsed.TotalSeconds > 20 && p != null && p.HasExited && !AnyUninstallerRunning() && SelfTestDir == null) break;
                    }
                });
            }
            finally
            {
                overlay.CancelButton.Click -= onContinue;
                overlay.CancelButton.Text = cancelText;
                overlay.HideBusy();
                busy = false;
                UpdateButtons();
            }
            if (!started) return false;
            return !StillInstalled(a);
        }

        /// <summary>A typical uninstaller helper still running (NSIS Au_, Un_A, Inno unins..., msiexec)?</summary>
        static bool AnyUninstallerRunning()
        {
            try
            {
                return Process.GetProcesses().Any(p =>
                {
                    string n = p.ProcessName.ToLowerInvariant();
                    return n.StartsWith("au_") || n.StartsWith("un_a") || n.StartsWith("unins") || n.StartsWith("uninst") || n == "msiexec" || n.Contains("uninstall");
                });
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- leftovers

        void LookForSelectedLeftovers()
        {
            var a = SelectedApp();
            if (a == null || busy) return;
            LookForLeftovers(a);
        }

        async void LookForLeftovers(InstalledApp a)
        {
            await FindLeftovers(a);
            a.Expanded = true;
            ShowApps();
            RevealApp(a);
            statusLabel.Text = a.Leftovers.Count == 0 ? "No leftovers of " + a.Name + " found outside its program folder."
                : string.Format("{0}: {1:N0} related item{2} outside its program folder ({3}).", a.Name, a.Leftovers.Count, a.Leftovers.Count == 1 ? "" : "s",
                    Fmt.Size(a.Leftovers.Sum(l => l.Size)));
        }

        async Task FindLeftovers(InstalledApp a)
        {
            statusLabel.Text = "Looking for leftovers of " + a.Name + "...";
            List<Leftover> found = null;
            await Task.Run(() => { found = SearchLeftovers(a); });
            a.Leftovers = found ?? new List<Leftover>();
        }

        static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Names the app's files and keys are likely to use: display name (with and without version/bitness), folder and key names.</summary>
        static HashSet<string> CandidateNames(InstalledApp a)
        {
            var names = new HashSet<string>();
            Action<string> add = s => { string n = Norm(s); if (n.Length >= 4) names.Add(n); };
            add(a.Name);
            // "Foo 1.2.3 (x64)" -> "Foo"
            var words = a.Name.Split(new[] { ' ', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .TakeWhile(w => !char.IsDigit(w[0]) && !w.StartsWith("x64") && !w.StartsWith("x86") && !w.Equals("version", StringComparison.OrdinalIgnoreCase)).ToList();
            if (words.Count > 0) add(string.Join(" ", words));
            if (!string.IsNullOrEmpty(a.Location)) add(Native.FileName(a.Location));
            if (!string.IsNullOrEmpty(a.KeyName) && !a.KeyName.StartsWith("{") && !a.KeyName.Contains("_is1") ) add(a.KeyName);
            if (!string.IsNullOrEmpty(a.KeyName) && a.KeyName.EndsWith("_is1")) add(a.KeyName.Substring(0, a.KeyName.Length - 4));
            if (a.IsStore) { add(a.PackageFamilyName); add(StoreNameFromFull(a.PackageFullName)); }
            // Never match very generic names.
            foreach (var g in new[] { "microsoft", "windows", "program", "programs", "common", "commonfiles", "update", "updater", "tools", "setup", "installer", "temp", "cache", "data" })
                names.Remove(g);
            return names;
        }

        List<Leftover> SearchLeftovers(InstalledApp a)
        {
            var result = new List<Leftover>();
            var names = CandidateNames(a);
            if (names.Count == 0) return result;
            string pub = Norm(a.Publisher);
            bool pubOk = pub.Length >= 4 && !pub.StartsWith("microsoft");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string, bool> addPath = (path, kind, isDir) =>
            {
                // While the app is installed, its program folder is not a leftover.
                if (!a.Uninstalled && !string.IsNullOrEmpty(a.Location) && (IsWithin(path, a.Location) || IsWithin(a.Location, path))) return;
                if (!seen.Add(path.TrimEnd('\\'))) return;
                var l = new Leftover { Path = path, Label = Native.FileName(path), Kind = kind, IsDir = isDir };
                try { l.Size = isDir ? new Scanner().Run(path).Size : new FileInfo(path).Length; } catch (Exception) { }
                result.Add(l);
            };

            // The program folder itself, if the uninstaller left it.
            if (!string.IsNullOrEmpty(a.Location) && Native.IsDirectory(a.Location) && a.Uninstalled && !a.IsStore)
                addPath(a.Location, "Program folder", true);

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folders = new List<Tuple<string, string>>
            {
                Tuple.Create(roaming, "Settings (AppData\\Roaming)"),
                Tuple.Create(local, "Data and cache (AppData\\Local)"),
                Tuple.Create(Path.Combine(home, "AppData", "LocalLow"), "Data (AppData\\LocalLow)"),
                Tuple.Create(Path.Combine(local, "Programs"), "Program folder"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Shared data (ProgramData)"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program folder"),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program folder"),
                Tuple.Create(Path.Combine(home, "Documents"), "Documents folder"),
            };
            if (a.IsStore) folders.Add(Tuple.Create(Path.Combine(local, "Packages"), "App data (Store package)"));
            foreach (var f in folders)
            {
                if (string.IsNullOrEmpty(f.Item1) || !Native.IsDirectory(f.Item1)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(f.Item1); } catch (Exception) { continue; }   // these roots are short paths (.NET 4 rejects \\?\ here)
                foreach (var d0 in dirs)
                {
                    string d = d0.StartsWith(@"\\?\") ? d0.Substring(4) : d0;
                    string n = Norm(Native.FileName(d));
                    if (names.Contains(n)) { if (f.Item2 != "Documents folder" || n.Length >= 6) addPath(d, f.Item2, true); continue; }
                    // Publisher folder: the app's subfolder (or the publisher folder when that is all it holds).
                    if (pubOk && (n == pub || pub.StartsWith(n) && n.Length >= 5))
                    {
                        string[] subs;
                        try { subs = Directory.GetFileSystemEntries(d); } catch (Exception) { continue; }
                        var matching = subs.Where(s => names.Contains(Norm(Native.FileName(s)))).ToList();
                        if (matching.Count == 0) continue;
                        if (matching.Count == subs.Length) addPath(d, f.Item2, true);
                        else foreach (var m in matching) addPath(m.StartsWith(@"\\?\") ? m.Substring(4) : m, f.Item2, Native.IsDirectory(m));
                    }
                }
            }

            // Shortcuts in the Start menu and on the desktop.
            var shortcutRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            foreach (var root in shortcutRoots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
            {
                try
                {
                    foreach (var d in Directory.GetDirectories(root))
                        if (names.Contains(Norm(Path.GetFileName(d))) || (pubOk && Norm(Path.GetFileName(d)) == pub && Directory.GetFileSystemEntries(d).All(x => names.Contains(Norm(Path.GetFileNameWithoutExtension(x))))))
                            addPath(d, "Start menu folder", true);
                    foreach (var lnk in Directory.GetFiles(root, "*.lnk"))
                        if (names.Contains(Norm(Path.GetFileNameWithoutExtension(lnk)))) addPath(lnk, "Shortcut", false);
                }
                catch (Exception) { }
            }

            // Registry: the uninstall entry if it is still there, and Software\<App> or Software\<Publisher>\<App>.
            Action<RegistryHive, RegistryView, string, string> addKey = (hive, view, sub, kind) =>
            {
                string disp = (hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER\\" : "HKEY_LOCAL_MACHINE\\") + sub;
                if (!seen.Add(disp)) return;
                result.Add(new Leftover { IsRegistry = true, Hive = hive, View = view, SubKey = sub, Path = disp, Label = Native.FileName(sub), Kind = kind });
            };
            if (!a.IsStore && a.Uninstalled && StillInstalledKey(a)) addKey(a.Hive, a.View, a.KeyPath, "Uninstall entry (registry)");
            var regRoots = new[]
            {
                Tuple.Create(RegistryHive.CurrentUser, RegistryView.Registry64, "Software"),
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE")
            };
            foreach (var r in regRoots)
            {
                try
                {
                    using (var hive = RegistryKey.OpenBaseKey(r.Item1, r.Item2))
                    using (var sw = hive.OpenSubKey(r.Item3))
                    {
                        if (sw == null) continue;
                        string prefix = r.Item3 + (r.Item2 == RegistryView.Registry32 && r.Item1 == RegistryHive.LocalMachine ? "\\WOW6432Node" : "");
                        foreach (var k in sw.GetSubKeyNames())
                        {
                            string n = Norm(k);
                            if (names.Contains(n)) { addKey(r.Item1, RegistryView.Registry64, prefix + "\\" + k, "Settings (registry)"); continue; }
                            if (!pubOk || n != pub) continue;
                            using (var pk = sw.OpenSubKey(k))
                            {
                                if (pk == null) continue;
                                var subs = pk.GetSubKeyNames();
                                var matching = subs.Where(s => names.Contains(Norm(s))).ToList();
                                if (matching.Count == 0) continue;
                                if (matching.Count == subs.Length && pk.ValueCount == 0) addKey(r.Item1, RegistryView.Registry64, prefix + "\\" + k, "Settings (registry)");
                                else foreach (var m in matching) addKey(r.Item1, RegistryView.Registry64, prefix + "\\" + k + "\\" + m, "Settings (registry)");
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
            // Leftovers that need administrator rights start unchecked when the app is not elevated.
            foreach (var l in result)
                if (!isAdmin && NeedsAdmin(l)) { l.Checked = false; l.Note = "needs Run as administrator"; }
            return result;
        }

        bool StillInstalledKey(InstalledApp a)
        {
            try
            {
                using (var hive = RegistryKey.OpenBaseKey(a.Hive, a.View))
                using (var k = hive.OpenSubKey(a.KeyPath)) return k != null;
            }
            catch (Exception) { return false; }
        }

        static bool NeedsAdmin(Leftover l)
        {
            if (l.IsRegistry) return l.Hive == RegistryHive.LocalMachine;
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string cp = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), cd = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            return new[] { pd, pf, pf86, cp, cd }.Any(x => !string.IsNullOrEmpty(x) && IsWithin(l.Path, x));
        }

        // ---------------------------------------------------------------- removing leftovers

        async void RemoveCheckedLeftovers() { await RemoveLeftovers(); }

        /// <summary>Removes the checked leftovers of all apps; returns the .reg backup file (null when no keys were removed).</summary>
        async Task<string> RemoveLeftovers()
        {
            if (busy || scanning || apps == null) return null;
            var owners = apps.Where(x => x.Leftovers != null && x.Leftovers.Any(l => l.Checked)).ToList();
            var items = owners.SelectMany(x => x.Leftovers.Where(l => l.Checked)).ToList();
            if (items.Count == 0) return null;
            long total = items.Sum(l => l.Size);
            string list = string.Join("\n", items.Take(12).Select(l => "    " + Fmt.Shorten(l.Path, 90) + (l.IsRegistry ? "" : "   (" + Fmt.Size(l.Size) + ")")));
            if (items.Count > 12) list += string.Format("\n    ... and {0:N0} more", items.Count - 12);
            string who = owners.Count == 1 ? owners[0].Name : owners.Count + " apps";
            if (SelfTestDir == null && MessageBox.Show(this,
                    string.Format("Permanently remove {0:N0} leftover{1} of {2} ({3})?\n\n{4}\n\n{5}This bypasses the Recycle Bin and CANNOT be undone.",
                        items.Count, items.Count == 1 ? "" : "s", who, Fmt.Size(total), list,
                        items.Any(l => l.IsRegistry) ? "Registry keys are saved to a .reg backup first (in " + BackupFolder + ").\n\n" : ""),
                    AppName + " - remove leftovers", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return null;

            var files = items.Where(l => !l.IsRegistry).Select(l => new DeleteTarget { Path = l.Path, Label = l.Label, IsDir = l.IsDir, Size = l.Size }).ToList();
            var keys = items.Where(l => l.IsRegistry).ToList();
            var job = new DeleteJob();
            deleteJob = job;
            var regErrors = new List<string>();
            string backup = null;
            BeginOwnChanges(files.Select(f => f.Path));
            SetBusy(true);
            try
            {
                await RunThread(() =>
                {
                    job.Run(files, false);
                    if (keys.Count > 0)
                    {
                        Directory.CreateDirectory(BackupFolder);
                        backup = Path.Combine(BackupFolder, string.Join("_", who.Split(Path.GetInvalidFileNameChars())) + " " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".reg");
                        foreach (var k in keys)
                        {
                            job.Current = k.Path;
                            if (!ExportKey(k, backup)) { regErrors.Add(k.Path + "  -  could not be backed up, so it was kept"); continue; }
                            try
                            {
                                using (var hive = RegistryKey.OpenBaseKey(k.Hive, k.View)) hive.DeleteSubKeyTree(k.SubKey, false);
                            }
                            catch (Exception ex) { regErrors.Add(k.Path + "  -  " + (ex is UnauthorizedAccessException || ex is System.Security.SecurityException ? "access denied" : ex.Message)); }
                        }
                    }
                });
                job.Current = "Updating the scan...";
                await ApplyDeleted(files);
            }
            finally { deleteJob = null; SetBusy(false); EndOwnChanges(); }

            // Measure again: gone items leave, the rest stay with what is left.
            foreach (var o in owners)
            {
                foreach (var l in o.Leftovers.Where(x => x.Checked).ToList())
                {
                    bool exists = l.IsRegistry ? KeyExists(l) : Native.Exists(l.Path);
                    if (!exists) { o.Leftovers.Remove(l); continue; }
                    if (!l.IsRegistry && l.IsDir) { var n = FindScanned(l.Path, true); try { l.Size = n != null ? n.Size : new Scanner().Run(l.Path).Size; } catch (Exception) { } }
                    l.Note = "partly removed -- in use, or needs Run as administrator";
                }
                if (o.Uninstalled && o.Leftovers.Count == 0) apps.Remove(o);
            }
            ViewsChanged();
            ShowApps();
            RefreshTree(current != null && IsAttached(current) ? current : root);
            int errors = job.Errors.Count + regErrors.Count;
            statusLabel.Text = string.Format("Removed leftovers of {0}: freed {1}{2}.{3}", who, Fmt.Size(Math.Max(0, total - owners.Sum(o => o.Leftovers.Where(l => l.Checked).Sum(l => l.Size)))),
                errors > 0 ? string.Format(", {0:N0} item{1} could not be removed", errors, errors == 1 ? "" : "s") : "",
                backup != null && File.Exists(backup) ? " Registry backup: " + backup : "");
            if (errors > 0 && SelfTestDir == null)
                MessageBox.Show(this, "Some leftovers could not be removed (in use, or access denied" + (isAdmin ? "" : " - try Run as administrator") + "):\n\n" +
                    string.Join("\n", job.Errors.Concat(regErrors).Take(20).Select(e => Fmt.Shorten(e, 160))), AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return backup;
        }

        static bool KeyExists(Leftover k)
        {
            try
            {
                using (var hive = RegistryKey.OpenBaseKey(k.Hive, k.View))
                using (var s = hive.OpenSubKey(k.SubKey)) return s != null;
            }
            catch (Exception) { return true; }
        }

        /// <summary>Appends the key to the .reg backup (reg.exe export; the first export writes the header).</summary>
        static bool ExportKey(Leftover k, string backup)
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "dsa-" + Guid.NewGuid().ToString("N") + ".reg");
                string root = k.Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
                var psi = new ProcessStartInfo("reg.exe", "export \"" + root + "\\" + k.SubKey + "\" \"" + tmp + "\" /y" + (k.View == RegistryView.Registry32 ? " /reg:32" : " /reg:64"))
                { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)) { p.WaitForExit(30000); if (p.ExitCode != 0) return false; }
                string text = File.ReadAllText(tmp, Encoding.Unicode);
                File.Delete(tmp);
                if (File.Exists(backup))
                {
                    int i = text.IndexOf("\r\n", StringComparison.Ordinal);   // skip the "Windows Registry Editor" header line
                    File.AppendAllText(backup, i >= 0 ? text.Substring(i) : text, Encoding.Unicode);
                }
                else File.WriteAllText(backup, text, Encoding.Unicode);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
