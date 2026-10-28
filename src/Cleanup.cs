using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>A known place that usually holds space that can be freed, with what deleting it means.</summary>
    internal sealed class Suggestion
    {
        public string Name, Note, Location;
        public bool Safe;                         // safe = rebuilt or re-downloaded automatically
        public bool NeedsAdmin;
        public List<DeleteTarget> Targets = new List<DeleteTarget>();
        public long Size;
    }

    internal sealed partial class MainForm
    {
        int suggestGen;
        List<Suggestion> suggestions;

        Page BuildSuggestionsPage()
        {
            var refresh = Btn("Look again", ButtonKind.Secondary, "Measure the suggestions again", () => { suggestions = null; FillSuggestions(); });
            var list = NewList(new[] { "Name", "Size", "% of suggestions", "Location", "What deleting means" },
                new[] { "name", "size", "size", "folder", "note" }, new[] { 230, 90, 150, 300, 420 }, 2);
            var p = AddPage("Suggestions", list, MakeBar(refresh));
            p.ChartTitle = "Cleanup suggestions";
            p.ChartScope = "of the suggestions";
            p.Empty = "Nothing to suggest.";
            p.Fill = FillSuggestions;
            return p;
        }

        void FillSuggestions()
        {
            var p = pSuggest;
            if (suggestions != null) { ShowSuggestions(); return; }
            int gen = ++suggestGen;
            p.Caption.Text = "Looking for space that can be freed...";
            SetRows(p.List, new List<ListViewItem>(), "Looking for space that can be freed...");
            var scanned = roots.Where(r => r.Node != null).Select(r => r.Node).ToList();
            List<Suggestion> found = null;
            Task.Factory.StartNew(() => { found = FindSuggestions(scanned); })
                .ContinueWith(t =>
                {
                    if (gen != suggestGen || IsDisposed) return;
                    suggestions = t.IsFaulted ? new List<Suggestion>() : found;
                    ShowSuggestions();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowSuggestions()
        {
            var p = pSuggest;
            // Targets deleted since (e.g. from the delete list) drop out; folders whose contents were cleared are measured again on "Look again".
            long total = suggestions.Sum(s => s.Size);
            var rows = new List<ListViewItem>();
            foreach (var s in suggestions.Where(x => x.Size > 0))
            {
                var it = new ItemInfo
                {
                    Targets = s.Targets, Name = s.Name, Size = s.Size, FolderIcon = true, Folder = s.Location,
                    Path = s.Targets.Count == 1 && !s.Targets[0].RecycleBin ? s.Targets[0].Path : null,
                    Note = s.Note + (s.NeedsAdmin && !isAdmin ? "  Needs Run as administrator." : ""),
                    Percent = total > 0 ? 100.0 * s.Size / total : 0
                };
                var lvi = Row(it, it.Name, Fmt.Size(it.Size), it.Percent.ToString("0.0") + " %", it.Folder, it.Note);
                if (!s.Safe) lvi.ForeColor = Theme.WARN_FG;
                rows.Add(lvi);
            }
            rows.Sort((a, b) => p.List.Sorter.Compare(a, b));
            SetRows(p.List, rows, "Nothing to suggest: no caches, temporary files or leftovers worth deleting were found.");
            p.Caption.Text = rows.Count == 0 ? "Nothing to suggest." : string.Format(
                "{0} can probably be freed. White: safe, rebuilt or downloaded again when needed.  Amber: review first.  Select rows, then Add to delete list.",
                Fmt.Size(total));
            p.ChartSub = Fmt.Size(total) + " can be freed";
            p.ChartTotal = 0;
            if (ActivePage == p) RefreshChart();
        }

        // ---------------------------------------------------------------- catalogue

        static string Env(string name) { return Environment.GetEnvironmentVariable(name) ?? ""; }

        List<Suggestion> FindSuggestions(List<DirNode> scanned)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string sysDrive = Path.GetPathRoot(win);
            var list = new List<Suggestion>();

            long rbItems;
            long rb = DeleteJob.RecycleBinSize(out rbItems);
            if (rb > 0)
                list.Add(new Suggestion
                {
                    Name = "Recycle Bin", Location = string.Format("All drives ({0:N0} items)", rbItems), Safe = true, Size = rb,
                    Note = "Deleted items waiting in the bin. Emptying it frees the space for good.",
                    Targets = { new DeleteTarget { RecycleBin = true, Label = "Recycle Bin", Note = "empty the bin", Size = rb } }
                });

            Contents(list, "Temporary files", Path.GetTempPath(), true, false, "Files programs left behind. Files in use are skipped.");
            Contents(list, "Windows temporary files", Path.Combine(win, "Temp"), true, true, "Windows' own temporary files. Files in use are skipped.");
            Contents(list, "Windows Update downloads", Path.Combine(win, @"SoftwareDistribution\Download"), true, true,
                "Installed update packages. Windows downloads them again if it needs them.");
            Contents(list, "Delivery Optimization cache", Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
                true, true, "Update pieces shared with other PCs. Rebuilt as needed.");
            var dumps = new Suggestion { Name = "Crash dumps", Safe = true, Note = "Memory dumps from crashed programs and Windows. Only needed to investigate a crash." };
            AddContents(dumps, Path.Combine(local, "CrashDumps"));
            AddContents(dumps, Path.Combine(win, "Minidump"));
            AddFile(dumps, Path.Combine(win, "MEMORY.DMP"));
            AddContents(dumps, Path.Combine(progData, @"Microsoft\Windows\WER\ReportArchive"));
            AddContents(dumps, Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue"));
            Finish(list, dumps, true);
            var thumbs = new Suggestion { Name = "Thumbnail cache", Safe = true, Note = "Explorer's picture previews. Rebuilt when folders are opened; files in use are skipped." };
            string explorer = Path.Combine(local, @"Microsoft\Windows\Explorer");
            if (Directory.Exists(explorer))
                try { foreach (var f in Directory.GetFiles(explorer, "thumbcache_*.db")) AddFile(thumbs, f); } catch (Exception) { }
            Finish(list, thumbs, false);

            Browser(list, "Microsoft Edge cache", Path.Combine(local, @"Microsoft\Edge\User Data"));
            Browser(list, "Google Chrome cache", Path.Combine(local, @"Google\Chrome\User Data"));
            Browser(list, "Brave cache", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"));
            Browser(list, "Opera cache", Path.Combine(Env("APPDATA"), @"Opera Software\Opera Stable"));
            var firefox = new Suggestion { Name = "Firefox cache", Safe = true, Note = "Web pages stored to load faster. Close Firefox first; sites load a little slower once." };
            string ffProfiles = Path.Combine(local, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ffProfiles))
                try { foreach (var d in Directory.GetDirectories(ffProfiles)) AddContents(firefox, Path.Combine(d, "cache2")); } catch (Exception) { }
            Finish(list, firefox, false);

            const string dev = "Developer cache: downloaded packages. Downloaded again when a project needs them.";
            Contents(list, "npm cache", Path.Combine(local, "npm-cache"), true, false, dev);
            Contents(list, "Yarn cache", Path.Combine(local, @"Yarn\Cache"), true, false, dev);
            Contents(list, "pnpm store", Path.Combine(local, @"pnpm\store"), true, false, dev);
            Contents(list, "pip cache", Path.Combine(local, @"pip\Cache"), true, false, dev);
            Contents(list, "NuGet packages", Path.Combine(home, @".nuget\packages"), true, false, dev);
            Contents(list, "Gradle caches", Path.Combine(home, @".gradle\caches"), true, false, dev);
            Contents(list, "Maven repository", Path.Combine(home, @".m2\repository"), true, false, dev);
            Contents(list, "Cargo registry cache", Path.Combine(home, @".cargo\registry\cache"), true, false, dev);
            Contents(list, "Go build cache", Path.Combine(local, "go-build"), true, false, dev);

            // node_modules folders in the scanned trees (outermost only).
            var nm = new Suggestion { Name = "node_modules folders", Safe = true, Note = "JavaScript packages of projects. Restored with npm install (or yarn / pnpm)." };
            foreach (var r in scanned)
                foreach (var d in OutermostNamed(r, "node_modules"))
                {
                    nm.Targets.Add(new DeleteTarget { Path = d.FullPath, Label = d.Parent != null ? d.Parent.Name + @"\node_modules" : d.Name, IsDir = true, Dir = d, Size = d.Size, Note = "node_modules" });
                    nm.Size += d.Size;
                }
            if (nm.Targets.Count > 0) { nm.Location = string.Format("{0:N0} projects in the scanned drives", nm.Targets.Count); list.Add(nm); }

            // Old installers and disk images in Downloads: worth a look, not automatic.
            var dl = new Suggestion { Name = "Old installers in Downloads", Safe = false, Note = "Setup files and disk images older than 30 days. Keep the ones you still need." };
            string downloads = Path.Combine(home, "Downloads");
            if (Directory.Exists(downloads))
            {
                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".msix", ".iso", ".img", ".zip", ".7z", ".rar" };
                try
                {
                    foreach (var f in new DirectoryInfo(downloads).GetFiles())
                        if (exts.Contains(f.Extension) && f.LastWriteTime < DateTime.Now.AddDays(-30))
                        {
                            dl.Targets.Add(new DeleteTarget { Path = f.FullName, Label = f.Name, Size = f.Length, Note = "old download" });
                            dl.Size += f.Length;
                        }
                }
                catch (Exception) { }
                dl.Location = downloads;
            }
            if (dl.Targets.Count > 0) list.Add(dl);

            string old = Path.Combine(sysDrive, "Windows.old");
            if (Directory.Exists(old))
            {
                var s = new Suggestion
                {
                    Name = "Previous Windows installation", Location = old, Safe = false, NeedsAdmin = true,
                    Note = "Windows.old. After deleting it you can no longer go back to the previous version of Windows."
                };
                s.Size = MeasureFolder(old);
                s.Targets.Add(new DeleteTarget { Path = old, Label = "Windows.old", IsDir = true, Size = s.Size, Note = "previous Windows" });
                list.Add(s);
            }
            return list;
        }

        void Contents(List<Suggestion> list, string name, string dir, bool safe, bool admin, string note)
        {
            var s = new Suggestion { Name = name, Safe = safe, NeedsAdmin = admin, Note = note };
            AddContents(s, dir);
            Finish(list, s, false);
        }

        void Browser(List<Suggestion> list, string name, string userData)
        {
            var s = new Suggestion { Name = name, Safe = true, Note = "Web pages stored to load faster. Close the browser first; sites load a little slower once." };
            if (Directory.Exists(userData))
            {
                var profiles = new List<string> { userData };
                try { profiles.AddRange(Directory.GetDirectories(userData)); } catch (Exception) { }
                foreach (var p in profiles)
                    foreach (var c in new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" })
                        AddContents(s, Path.Combine(p, c));
            }
            Finish(list, s, false);
        }

        void AddContents(Suggestion s, string dir)
        {
            if (!Directory.Exists(dir)) return;
            long size = MeasureFolder(dir);
            if (size <= 0) return;
            s.Targets.Add(new DeleteTarget { Path = dir.TrimEnd('\\'), Label = s.Name, IsDir = true, ContentsOnly = true, Size = size, Note = "contents only" });
            s.Size += size;
        }

        void AddFile(Suggestion s, string file)
        {
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists || fi.Length == 0) return;
                s.Targets.Add(new DeleteTarget { Path = file, Label = fi.Name, Size = fi.Length, Note = s.Name });
                s.Size += fi.Length;
            }
            catch (Exception) { }
        }

        static void Finish(List<Suggestion> list, Suggestion s, bool multi)
        {
            if (s.Targets.Count == 0 || s.Size <= 0) return;
            s.Location = s.Targets.Count == 1 ? s.Targets[0].Path : string.Format("{0:N0} locations", s.Targets.Count);
            if (s.Targets.Count > 1) foreach (var t in s.Targets) if (t.Label == s.Name) t.Label = s.Name + ": " + Path.GetFileName(t.Path.TrimEnd('\\'));
            list.Add(s);
        }

        /// <summary>Size of a folder: from the scanned tree when it is there, otherwise read now.</summary>
        long MeasureFolder(string dir)
        {
            DirNode n = null;
            try { n = (DirNode)Invoke(new Func<DirNode>(() => FindScanned(dir, true))); } catch (Exception) { }
            if (n != null) return n.Size;
            try { var r = new Scanner().Run(dir); return r.Size; } catch (Exception) { return 0; }
        }

        static IEnumerable<DirNode> OutermostNamed(DirNode root, string name)
        {
            var stack = new Stack<DirNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n != root && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) { yield return n; continue; }
                if (n.Children != null) foreach (var c in n.Children) stack.Push(c);
            }
        }
    }
}
