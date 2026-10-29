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
        public bool Expanded;

        /// <summary>0 none, 1 all, 2 some of the targets checked.</summary>
        public int CheckState
        {
            get
            {
                int n = Targets.Count(t => t.Checked);
                return n == 0 ? 0 : n == Targets.Count ? 1 : 2;
            }
        }

        public void Remeasure() { Size = Targets.Sum(t => t.Size); }
    }

    internal sealed partial class MainForm
    {
        int suggestGen;
        List<Suggestion> suggestions;
        /// <summary>The scanned data changed since the suggestions were found (scan, rescan, live change, delete).</summary>
        bool suggestionsDirty;
        DateTime lastSuggestFind = DateTime.MinValue;

        RoundButton btnSugAdd, btnSugRecycle, btnSugDelete;

        Page BuildSuggestionsPage()
        {
            var all = Btn("Select all", ButtonKind.Secondary, "Tick every suggestion and every location in it", () => CheckAllSuggestions(true));
            var none = Btn("Deselect all", ButtonKind.Secondary, "Untick everything", () => CheckAllSuggestions(false));
            var refresh = Btn("Look again", ButtonKind.Secondary, "Measure the suggestions again", () => { suggestions = null; FillSuggestions(); });
            btnSugAdd = Btn("Add to delete list", ButtonKind.Secondary, "Put the ticked items on the delete list to review them with the rest", AddCheckedSuggestions);
            btnSugRecycle = Btn("Move to Recycle Bin", ButtonKind.Secondary, "Move the ticked items to the Recycle Bin (can be undone)", () => DeleteCheckedSuggestions(true));
            btnSugDelete = Btn("Delete permanently", ButtonKind.Warn, "Delete the ticked items now, bypassing the Recycle Bin", () => DeleteCheckedSuggestions(false));
            foreach (var b in new[] { btnSugAdd, btnSugRecycle, btnSugDelete }) b.Tag = "right";
            var list = NewList(new[] { "Name", "Size", "% of suggestions", "Location", "What deleting means" },
                new[] { "name", "size", "size", "folder", "note" }, new[] { 260, 90, 150, 300, 420 }, 2);
            list.GlyphClicked += SuggestionGlyph;
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Space) { ToggleSelectedSuggestions(); e.Handled = true; }
                else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Left) { ExpandSelectedSuggestions(e.KeyCode == Keys.Right); e.Handled = true; }
            };
            var p = AddPage("Suggestions", list, MakeBar(all, none, refresh, btnSugAdd, btnSugRecycle, btnSugDelete));
            p.ChartTitle = "Cleanup suggestions";
            p.ChartScope = "of the suggestions";
            p.Empty = "Nothing to suggest.";
            p.Fill = FillSuggestions;
            return p;
        }

        void FillSuggestions()
        {
            var p = pSuggest;
            if (suggestions != null && !suggestionsDirty) { ShowSuggestions(); return; }
            suggestionsDirty = false;
            lastSuggestFind = DateTime.UtcNow;
            // Keep the user's choices across a new search: ticks by path, expanded suggestions by name.
            var ticks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var open = new HashSet<string>();
            if (suggestions != null)
                foreach (var s in suggestions)
                {
                    if (s.Expanded) open.Add(s.Name);
                    foreach (var tg in s.Targets) ticks[tg.RecycleBin ? "<bin>" : tg.Path] = tg.Checked;
                }
            int gen = ++suggestGen;
            p.Caption.Text = "Looking for space that can be freed...";
            SetRows(p.List, new List<ListViewItem>(), "Looking for space that can be freed...");
            UpdateSuggestionButtons();
            var scanned = roots.Where(r => r.Node != null).Select(r => r.Node).ToList();
            List<Suggestion> found = null;
            Task.Factory.StartNew(() => { found = FindSuggestions(scanned); })
                .ContinueWith(t =>
                {
                    if (gen != suggestGen || IsDisposed) return;
                    suggestions = t.IsFaulted ? new List<Suggestion>() : found;
                    // Safe suggestions start ticked, "review first" ones unticked; earlier choices are kept.
                    foreach (var s in suggestions)
                    {
                        s.Expanded = open.Contains(s.Name);
                        foreach (var tg in s.Targets)
                        {
                            bool was;
                            tg.Checked = ticks.TryGetValue(tg.RecycleBin ? "<bin>" : tg.Path, out was) ? was : s.Safe;
                        }
                    }
                    ShowSuggestions();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// The suggestions as a tree: one row per suggestion (chevron, checkbox) and, when expanded, one row per location or
        /// file it would delete. Children share their suggestion's chart slice. Row order: largest suggestion first.
        /// </summary>
        void ShowSuggestions()
        {
            var p = pSuggest;
            foreach (var s in suggestions) s.Remeasure();
            long total = suggestions.Sum(s => s.Size);
            var lv = p.List;
            string top = lv.TopItem != null ? ((ItemInfo)lv.TopItem.Tag).Name : null;
            var rows = new List<ListViewItem>();
            foreach (var s in suggestions.Where(x => x.Size > 0).OrderByDescending(x => x.Size))
            {
                var head = new ItemInfo
                {
                    Targets = s.Targets, Data = s, Name = s.Name, Size = s.Size, FolderIcon = true, Folder = s.Location,
                    Path = s.Targets.Count == 1 && !s.Targets[0].RecycleBin ? s.Targets[0].Path : null,
                    Note = s.Note + (s.NeedsAdmin && !isAdmin ? "  Needs Run as administrator." : ""),
                    Percent = total > 0 ? 100.0 * s.Size / total : 0, Check = s.CheckState, Expandable = s.Targets.Count > 0, Expanded = s.Expanded
                };
                var lvi = Row(head, head.Name, Fmt.Size(head.Size), head.Percent.ToString("0.0") + " %", head.Folder, head.Note);
                if (!s.Safe) lvi.ForeColor = Theme.WARN_FG;
                rows.Add(lvi);
                if (!s.Expanded) continue;
                foreach (var tg in s.Targets.Where(x => x.Size > 0).OrderByDescending(x => x.Size))
                {
                    var it = new ItemInfo
                    {
                        Targets = new List<DeleteTarget> { tg }, Data = tg, GroupOf = head, Name = tg.Label, Size = tg.Size,
                        FolderIcon = tg.IsDir || tg.RecycleBin, Folder = tg.RecycleBin ? "All drives" : tg.Path, Path = tg.RecycleBin ? null : tg.Path,
                        Note = tg.ContentsOnly ? "contents only" : tg.Note, Percent = total > 0 ? 100.0 * tg.Size / total : 0, Check = tg.Checked ? 1 : 0
                    };
                    var c = Row(it, it.Name, Fmt.Size(it.Size), it.Percent.ToString("0.0") + " %", it.Folder, it.Note);
                    if (!s.Safe) c.ForeColor = Theme.WARN_FG;
                    rows.Add(c);
                }
            }
            SetRows(lv, rows, "Nothing to suggest: no caches, temporary files or leftovers worth deleting were found.");
            if (top != null)
                foreach (ListViewItem it in lv.Items)
                    if (((ItemInfo)it.Tag).Name == top) { try { lv.TopItem = it; } catch (Exception) { } break; }
            var ticked = CheckedSuggestionTargets();
            p.Caption.Text = rows.Count == 0 ? "Nothing to suggest." : string.Format(
                "{0} can probably be freed. Checked: {1:N0} item{2}, {3}.  White: safe, rebuilt or downloaded again when needed.  Amber: review first.",
                Fmt.Size(total), ticked.Count, ticked.Count == 1 ? "" : "s", Fmt.Size(ticked.Sum(t => t.Size)));
            p.ChartSub = Fmt.Size(total) + " can be freed";
            p.ChartTotal = 0;
            UpdateSuggestionButtons();
            if (ActivePage == p) RefreshChart();
        }

        List<DeleteTarget> CheckedSuggestionTargets()
        {
            if (suggestions == null) return new List<DeleteTarget>();
            return suggestions.SelectMany(s => s.Targets).Where(t => t.Checked && t.Size > 0).ToList();
        }

        void UpdateSuggestionButtons()
        {
            if (btnSugAdd == null) return;
            bool any = CheckedSuggestionTargets().Count > 0;
            btnSugAdd.Enabled = any && !busy;
            btnSugRecycle.Enabled = btnSugDelete.Enabled = any && !busy && !scanning;
        }

        void SuggestionGlyph(ListViewItem lvi, bool checkbox)
        {
            var info = (ItemInfo)lvi.Tag;
            var s = info.Data as Suggestion;
            if (!checkbox) { if (s != null) { s.Expanded = !s.Expanded; ShowSuggestions(); } return; }
            if (s != null) { bool on = s.CheckState != 1; foreach (var t in s.Targets) t.Checked = on; }
            else { var t = info.Data as DeleteTarget; if (t != null) t.Checked = !t.Checked; }
            ShowSuggestions();
        }

        void ToggleSelectedSuggestions()
        {
            var sel = pSuggest.List.SelectedItems.Cast<ListViewItem>().Select(i => (ItemInfo)i.Tag).ToList();
            if (sel.Count == 0) return;
            var targets = sel.SelectMany(i => i.Data is Suggestion ? ((Suggestion)i.Data).Targets : i.Data is DeleteTarget ? new List<DeleteTarget> { (DeleteTarget)i.Data } : new List<DeleteTarget>()).Distinct().ToList();
            bool on = targets.Any(t => !t.Checked);
            foreach (var t in targets) t.Checked = on;
            var keep = sel.Select(i => i.Name).ToList();
            ShowSuggestions();
            foreach (ListViewItem it in pSuggest.List.Items) it.Selected = keep.Contains(((ItemInfo)it.Tag).Name);
        }

        void ExpandSelectedSuggestions(bool expand)
        {
            var sel = pSuggest.List.SelectedItems.Cast<ListViewItem>().Select(i => (ItemInfo)i.Tag).ToList();
            var ss = sel.Select(i => i.Data as Suggestion ?? (i.GroupOf != null ? i.GroupOf.Data as Suggestion : null)).Where(x => x != null).Distinct().ToList();
            if (ss.Count == 0) return;
            foreach (var s in ss) s.Expanded = expand;
            ShowSuggestions();
            foreach (ListViewItem it in pSuggest.List.Items) it.Selected = ss.Contains(((ItemInfo)it.Tag).Data as Suggestion);
        }

        void CheckAllSuggestions(bool on)
        {
            if (suggestions == null) return;
            foreach (var s in suggestions) foreach (var t in s.Targets) t.Checked = on;
            ShowSuggestions();
        }

        void AddCheckedSuggestions()
        {
            var ticked = CheckedSuggestionTargets();
            int added = ticked.Count(AddTarget);
            BasketChanged();
            statusLabel.Text = string.Format("Added {0:N0} suggested item{1} to the delete list: now {2:N0} item{3}, {4}.",
                added, added == 1 ? "" : "s", basket.Count, basket.Count == 1 ? "" : "s", Fmt.Size(basket.Sum(t => t.Size)));
        }

        async void DeleteCheckedSuggestions(bool recycle)
        {
            if (busy || scanning) return;
            var ticked = CheckedSuggestionTargets();
            if (ticked.Count == 0) return;
            var job = await ConfirmAndDelete(ticked, recycle);
            if (job == null) return;
            AfterDeleteRemeasure(ticked);
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
