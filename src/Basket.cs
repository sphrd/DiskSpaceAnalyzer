using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>
    /// Delete list: items collected from any view, reviewed together, then moved to the Recycle Bin or deleted
    /// permanently. Also the shared deletion flow (confirmation, progress, updating the scan afterwards).
    /// </summary>
    internal sealed partial class MainForm
    {
        readonly List<DeleteTarget> basket = new List<DeleteTarget>();
        RoundButton btnBasketRemove, btnBasketClear, btnBasketRecycle, btnBasketDelete;

        Page BuildBasketPage()
        {
            btnBasketRemove = Btn("Remove from list", ButtonKind.Secondary, "Take the selected items off the list (they stay on the disk)", RemoveFromBasket);
            btnBasketClear = Btn("Clear list", ButtonKind.Secondary, "Empty the delete list (nothing is deleted)", () => { basket.Clear(); BasketChanged(); });
            btnBasketRecycle = Btn("Move to Recycle Bin", ButtonKind.Secondary,
                "Move everything on the list to the Recycle Bin: restorable; the space is freed when the bin is emptied", () => DeleteBasket(true));
            btnBasketDelete = Btn("Delete permanently", ButtonKind.Warn, "Delete everything on the list now, bypassing the Recycle Bin", () => DeleteBasket(false));
            btnBasketRecycle.Tag = "right";
            btnBasketDelete.Tag = "right";
            var list = NewList(new[] { "Name", "Size", "% of list", "Location", "Note" },
                new[] { "name", "size", "size", "folder", "note" }, new[] { 240, 90, 150, 360, 220 }, 2);
            var p = AddPage("Delete list", list, MakeBar(btnBasketRemove, btnBasketClear, btnBasketRecycle, btnBasketDelete));
            p.ChartTitle = "Delete list";
            p.Empty = "Nothing on the delete list.";
            p.Fill = FillBasket;
            return p;
        }

        void FillBasket()
        {
            var p = pBasket;
            long total = basket.Sum(t => t.Size);
            var rows = new List<ListViewItem>();
            foreach (var t in basket)
            {
                var it = new ItemInfo
                {
                    Targets = new List<DeleteTarget> { t }, Name = t.Label, Size = t.Size, FolderIcon = t.IsDir,
                    Path = t.RecycleBin ? null : t.Path, Note = t.Note, Percent = total > 0 ? 100.0 * t.Size / total : 0,
                    Folder = t.RecycleBin ? "All drives" : t.ContentsOnly ? t.Path : Native.ParentDir(t.Path) ?? t.Path
                };
                rows.Add(Row(it, it.Name, Fmt.Size(it.Size), it.Percent.ToString("0.0") + " %", it.Folder, it.Note));
            }
            rows.Sort((a, b) => p.List.Sorter.Compare(a, b));
            SetRows(p.List, rows, "The delete list is empty.\nSelect files or folders in any view and press Add to delete list (Ctrl+D), then review and delete them here.");
            p.Caption.Text = basket.Count == 0 ? "Collect items here from any view, review them, then delete them together."
                : string.Format("{0:N0} item{1}, {2}.  Move to Recycle Bin can be undone (the space is freed when the bin is emptied); Delete permanently frees it now.",
                    basket.Count, basket.Count == 1 ? "" : "s", Fmt.Size(total));
            p.ChartSub = basket.Count > 0 ? Fmt.Size(total) + " to free" : "";
            p.ChartTotal = 0;
            btnBasketRemove.Enabled = btnBasketClear.Enabled = basket.Count > 0 && !busy;
            btnBasketRecycle.Enabled = btnBasketDelete.Enabled = basket.Count > 0 && !busy && !scanning;
            if (ActivePage == p) RefreshChart();
        }

        void UpdateBasketTitle()
        {
            if (viewTabs == null) return;
            viewTabs.SetTitle(pages.IndexOf(pBasket), basket.Count > 0 ? string.Format("Delete list ({0:N0})", basket.Count) : "Delete list");
        }

        void BasketChanged()
        {
            UpdateBasketTitle();
            pBasket.Stale = true;
            if (ActivePage == pBasket) { pBasket.Stale = false; FillBasket(); }
        }

        void AddSelectionToBasket()
        {
            if (busy) return;
            var sel = GetSelection().Where(i => i.CanDelete).ToList();
            if (sel.Count == 0 || lastView == pBasket.List) return;
            int added = 0;
            foreach (var it in sel)
                foreach (var t in DeleteTarget.From(it))
                    if (AddTarget(t)) added++;
            BasketChanged();
            statusLabel.Text = added == 0 ? "Already on the delete list."
                : string.Format("Added {0:N0} item{1} to the delete list: now {2:N0} item{3}, {4}. Open the Delete list tab to review and delete them.",
                    added, added == 1 ? "" : "s", basket.Count, basket.Count == 1 ? "" : "s", Fmt.Size(basket.Sum(t => t.Size)));
        }

        /// <summary>Adds a target unless it is already covered; drops entries that the new folder covers.</summary>
        bool AddTarget(DeleteTarget t)
        {
            if (t.RecycleBin)
            {
                if (basket.Any(b => b.RecycleBin)) return false;
                basket.Add(t);
                return true;
            }
            foreach (var b in basket)
            {
                if (b.RecycleBin) continue;
                if (SamePath(b.Path, t.Path) && b.ContentsOnly == t.ContentsOnly) return false;
                if (b.IsDir && !SamePath(b.Path, t.Path) && IsWithin(t.Path, b.Path)) return false;   // inside a folder already listed
                if (b.IsDir && !b.ContentsOnly && SamePath(b.Path, t.Path)) return false;              // the whole folder is listed
            }
            if (t.IsDir) basket.RemoveAll(b => !b.RecycleBin && !SamePath(b.Path, t.Path) && IsWithin(b.Path, t.Path));
            if (t.IsDir && !t.ContentsOnly) basket.RemoveAll(b => !b.RecycleBin && SamePath(b.Path, t.Path) && b.ContentsOnly);
            basket.Add(t);
            return true;
        }

        void RemoveFromBasket()
        {
            var sel = pBasket.List.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as ItemInfo).Where(i => i != null && i.Targets != null).ToList();
            if (sel.Count == 0) return;
            foreach (var it in sel) foreach (var t in it.Targets) basket.Remove(t);
            BasketChanged();
        }

        async void DeleteBasket(bool recycle)
        {
            if (busy || scanning || basket.Count == 0) return;
            var targets = basket.ToList();
            var done = await ConfirmAndDelete(targets, recycle);
            if (done == null) return;
            AfterDeleteRemeasure(targets);
        }

        void DeleteSelected() { DeleteSelection(false); }

        void RecycleSelected() { DeleteSelection(true); }

        async void DeleteSelection(bool recycle)
        {
            if (busy || scanning) return;
            var sel = GetSelection().Where(i => i.CanDelete).ToList();
            if (sel.Count == 0) return;
            var targets = sel.SelectMany(DeleteTarget.From).ToList();
            var job = await ConfirmAndDelete(targets, recycle);
            if (job != null) AfterDeleteRemeasure(targets);
        }

        /// <summary>Asks (themed confirmation with every item), deletes (or recycles) with the red delete strip while the open
        /// list drops what is gone, then updates the scan. Null when cancelled.</summary>
        async Task<DeleteJob> ConfirmAndDelete(List<DeleteTarget> targets, bool recycle)
        {
            if (targets.Any(t => !t.RecycleBin && roots.Any(r => r.Node != null && SamePath(r.Node.FullPath, t.Path)) && !t.ContentsOnly))
            {
                Alert(AlertIcon.Warn, "A scanned drive or folder can't be deleted here", "Select items inside it instead.", null, null, "OK");
                return null;
            }
            long total = targets.Sum(t => t.Size);
            string n = string.Format("{0:N0} item{1}", targets.Count, targets.Count == 1 ? "" : "s");
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string note = targets.Any(t => !t.ContentsOnly && !t.RecycleBin && (IsWithin(t.Path, win) || IsWithin(t.Path, pf) || IsWithin(t.Path, pf86)))
                ? "This includes Windows or Program Files content. Deleting it can break Windows or installed programs." : null;
            if (SelfTestDir == null)
            {
                string pick = recycle
                    ? Alert(AlertIcon.Warn, "Move " + n + " to the Recycle Bin?",
                        Fmt.Size(total) + " go to the Recycle Bin. You can restore them from there; the space is freed when the bin is emptied.",
                        note, TargetLines(targets), "Move to Recycle Bin", "Cancel")
                    : Alert(AlertIcon.Danger, "Delete " + n + " permanently?",
                        Fmt.Size(total) + " will be deleted. This bypasses the Recycle Bin and cannot be undone.",
                        note, TargetLines(targets), "Delete permanently", "Cancel");
                if (pick != (recycle ? "Move to Recycle Bin" : "Delete permanently")) return null;
            }

            DirNode target = current;
            foreach (var t in targets)
            {
                var dn = t.ContentsOnly || t.RecycleBin ? null : t.Dir != null && IsAttached(t.Dir) ? t.Dir : FindScanned(t.Path, true);
                if (dn != null && current != null && current.IsUnder(dn)) target = dn.Parent;
            }

            long before = roots.Where(r => r.Node != null).Sum(r => r.Node.Size);
            var job = new DeleteJob();
            await RunDelete(job, targets, recycle, null);

            if (target == null || !IsAttached(target)) target = root;
            current = null;
            RefreshTree(target);
            ViewsChanged();
            UpdateDriveLabel();
            if (lastView != null && lastView.Visible) lastView.Focus();
            long freed = before - roots.Where(r => r.Node != null).Sum(r => r.Node.Size);
            string skipped = job.Skipped > 0 ? string.Format(", {0:N0} in use were skipped", job.Skipped) : "";
            string stopped = job.Stop ? " Stopped: the rest was not touched." : "";
            statusLabel.Text = (recycle
                ? string.Format("Moved {0:N0} item{1} ({2}) to the Recycle Bin{3}{4}.", job.Recycled, job.Recycled == 1 ? "" : "s", Fmt.Size(freed), skipped,
                    job.Errors.Count > 0 ? string.Format(", {0:N0} could not be moved", job.Errors.Count) : "")
                : string.Format("Freed {0}. {1:N0} files deleted{2}{3}.", Fmt.Size(Math.Max(0, freed)), job.FilesDeleted, skipped,
                    job.Errors.Count > 0 ? string.Format(", {0:N0} items could not be deleted", job.Errors.Count) : "")) + stopped;

            if (job.Errors.Count > 0 && SelfTestDir == null) ShowDeleteErrors(job.Errors, recycle ? "moved to the Recycle Bin" : "deleted");
            return job;
        }

        /// <summary>One line per target for the confirmation's details box: path, then (after a tab) its size.</summary>
        static List<string> TargetLines(IEnumerable<DeleteTarget> targets)
        {
            return targets.Select(t => (t.RecycleBin ? "Empty the Recycle Bin" : t.Path + (t.ContentsOnly ? "  (its contents)" : "")) + "\t" + Fmt.Size(t.Size)).ToList();
        }

        /// <summary>Themed report of what could not be deleted: every item with the reason, in a scrollable box.</summary>
        void ShowDeleteErrors(IList<string> errors, string verb)
        {
            var lines = errors.Select(e => { int i = e.LastIndexOf("  -  ", StringComparison.Ordinal); return i > 0 ? e.Substring(0, i) + "\t" + e.Substring(i + 5) : e; }).ToList();
            Alert(AlertIcon.Warn, string.Format("{0:N0} item{1} could not be {2}", lines.Count, lines.Count == 1 ? "" : "s", verb),
                "They are in use by a running program, or access was denied. Everything else was " + verb + ".",
                isAdmin ? null : "Run as administrator (toolbar) to remove items that need it.", lines, "OK");
        }

        /// <summary>Every prompt of the app goes through here: the themed AlertForm. Returns the button pressed (null = closed).</summary>
        internal string Alert(AlertIcon icon, string heading, string body, string note, IList<string> details, params string[] buttons)
        {
            using (var f = new AlertForm(icon, heading, body, note, details, buttons))
            {
                f.ShowDialog(this);
                return f.Result;
            }
        }

        // ---------------------------------------------------------------- deleting, live

        readonly HashSet<string> liveGone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<DeleteTarget> liveDone = new HashSet<DeleteTarget>();
        long deleteTotalFiles;
        bool deleteRecycle;
        /// <summary>Self-test: rows of the open list that left it while the job was still running.</summary>
        internal int LiveRowsRemoved;

        /// <summary>
        /// Runs a delete job with the red delete strip under the toolbar. The window stays browsable (tabs, scrolling, hover)
        /// while actions that change the disk are disabled; the open list (any tab) drops rows as their files go.
        /// </summary>
        async Task RunDelete(DeleteJob job, List<DeleteTarget> targets, bool recycle, Action alsoOnThread)
        {
            deleteJob = job;
            deleteRecycle = recycle;
            liveGone.Clear();
            liveDone.Clear();
            LiveRowsRemoved = 0;
            deleteTotalFiles = 0;
            foreach (var t in targets)
            {
                if (t.RecycleBin) continue;
                if (!t.IsDir && !t.ContentsOnly) { deleteTotalFiles++; continue; }
                var dn = t.Dir != null && IsAttached(t.Dir) ? t.Dir : FindScanned(t.Path, true);
                if (dn != null) deleteTotalFiles += dn.Files;
            }
            BeginOwnChanges(targets.Where(x => !x.RecycleBin).Select(x => x.Path));
            busy = true;
            deleteBar.Start(recycle ? "Moving to the Recycle Bin..." : "Deleting permanently...");
            uiTimer.Start();
            UpdateButtons();
            try
            {
                await RunThread(() =>
                {
                    job.Run(targets, recycle);
                    if (alsoOnThread != null && !job.Stop) alsoOnThread();
                });
                DrainLive(job);
                deleteBar.SetProgress("Updating the scan...", "", -1);
                await ApplyDeleted(targets);
            }
            finally
            {
                deleteJob = null;
                busy = false;
                deleteBar.Stop();
                if (!scanning) uiTimer.Stop();
                UpdateButtons();
                EndOwnChanges();
                liveGone.Clear();
                liveDone.Clear();
                if (drivesChanged) BeginInvoke(new Action(RefreshDrives));
            }
        }

        void UpdateDeleteProgress(DeleteJob j)
        {
            long files = Interlocked.Read(ref j.FilesDeleted), recycled = Interlocked.Read(ref j.Recycled), bytes = Interlocked.Read(ref j.BytesDeleted);
            int errors;
            lock (j.Errors) errors = j.Errors.Count;
            string text;
            if (j.Stop) text = "Stopping... (finishing the current file)";
            else if (deleteRecycle) text = string.Format("Moving to the Recycle Bin: {0:N0} item{1} moved", recycled, recycled == 1 ? "" : "s");
            else text = string.Format("Deleting permanently: {0:N0} file{1} removed, {2} freed", files, files == 1 ? "" : "s", Fmt.Size(bytes));
            if (errors > 0) text += string.Format("  --  {0:N0} could not be deleted", errors);
            double frac = !deleteRecycle && deleteTotalFiles > 0 ? Math.Min(0.99, (double)(files + errors) / deleteTotalFiles) : -1;
            deleteBar.SetProgress(text, Fmt.Shorten(j.Current, 110), frac);
            DrainLive(j);
        }

        /// <summary>Takes what the job has deleted so far and removes those rows from the open list and the folder contents.</summary>
        void DrainLive(DeleteJob j)
        {
            bool any = false;
            string p;
            while (j.Gone.TryDequeue(out p)) { liveGone.Add(p.TrimEnd('\\')); any = true; }
            DeleteTarget t;
            while (j.Finished.TryDequeue(out t)) { liveDone.Add(t); any = true; }
            if (!any) return;
            var page = ActivePage;
            int removed = RemoveGoneRows(page != null ? page.List : null);
            if (page == null || page.List != contents) removed += RemoveGoneRows(contents);
            LiveRowsRemoved += removed;
            if (removed > 0 && page != null)
            {
                if (page.List == contents && current != null) UpdateChart(current);
                else RefreshChart();
            }
        }

        int RemoveGoneRows(SizeListView lv)
        {
            if (lv == null || lv.Items.Count == 0) return 0;
            var gone = new List<int>();
            for (int i = 0; i < lv.Items.Count; i++)
                if (RowGone(lv.Items[i].Tag as ItemInfo)) gone.Add(i);
            if (gone.Count == 0) return 0;
            lv.BeginUpdate();
            lv.ResetHover();
            for (int k = gone.Count - 1; k >= 0; k--) lv.Items.RemoveAt(gone[k]);
            lv.EndUpdate();
            return gone.Count;
        }

        bool RowGone(ItemInfo it)
        {
            if (it == null || it.IsSummary || it.IsGroup || it.Data is InstalledApp) return false;
            if (it.Targets != null && it.Targets.Count > 0)
                return it.Targets.All(t => liveDone.Contains(t) || (!t.RecycleBin && !t.ContentsOnly && PathGone(t.Path)));
            if (it.IsDir && it.Dir != null) return PathGone(it.Dir.FullPath);
            if (it.File != null) return PathGone(it.File.FullPath);
            return false;
        }

        /// <summary>The path, or a folder it is in, has been deleted.</summary>
        bool PathGone(string path)
        {
            if (string.IsNullOrEmpty(path) || liveGone.Count == 0) return false;
            for (string q = path.TrimEnd('\\'); !string.IsNullOrEmpty(q); q = Native.ParentDir(q))
                if (liveGone.Contains(q)) return true;
            return false;
        }
        /// <summary>
        /// After a delete: every target that was attempted, and every delete-list or suggestion entry for the same path, is
        /// measured again from the updated scan. Gone (or emptied) targets leave the delete list and the suggestions; targets
        /// with something left (files in use, access denied) keep the remaining size and say so.
        /// </summary>
        void AfterDeleteRemeasure(List<DeleteTarget> attempted)
        {
            var all = new List<DeleteTarget>(attempted);
            all.AddRange(basket);
            if (suggestions != null) foreach (var s in suggestions) all.AddRange(s.Targets);
            var gone = new HashSet<DeleteTarget>();
            foreach (var t in all.Distinct())
            {
                bool tried = attempted.Contains(t) || attempted.Any(a => !a.RecycleBin && !t.RecycleBin && SamePath(a.Path, t.Path)) ||
                    (t.RecycleBin && attempted.Any(a => a.RecycleBin));
                if (!tried) continue;
                long? left = MeasureTarget(t);
                if (left == null || (left.Value == 0 && (t.ContentsOnly || t.RecycleBin || t.IsDir))) { gone.Add(t); continue; }
                t.Size = left.Value;
                t.Note = "partly deleted -- the rest is in use or access was denied";
            }
            basket.RemoveAll(gone.Contains);
            if (suggestions != null) foreach (var s in suggestions) { s.Targets.RemoveAll(gone.Contains); s.Remeasure(); }
            BasketChanged();
            if (suggestions != null && ActivePage == pSuggest) ShowSuggestions();
            else if (pSuggest != null) pSuggest.Stale = true;
        }

        /// <summary>What is left of a target now (null = it no longer exists).</summary>
        long? MeasureTarget(DeleteTarget t)
        {
            if (t.RecycleBin) { long n; return DeleteJob.RecycleBinSize(out n); }
            if (t.IsDir)
            {
                if (!Native.IsDirectory(t.Path)) return null;
                var n = FindScanned(t.Path, true);
                if (n != null) return n.Size;
                try { return new Scanner().Run(t.Path).Size; } catch (Exception) { return 0; }
            }
            if (!Native.Exists(t.Path)) return null;
            var d = FindScanned(Native.ParentDir(t.Path), true);
            if (d != null && d.FileList != null)
                for (int i = 0; i < d.FileList.Length; i++)
                    if (string.Equals(d.FileList[i].Name, Native.FileName(t.Path), StringComparison.OrdinalIgnoreCase)) return FileIndex.Measure(ref d.FileList[i]);
            return t.Size;
        }

        /// <summary>The deepest scanned folder holding path; with exact, only a folder whose path is exactly path.</summary>
        DirNode FindScanned(string path, bool exact)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var r in roots)
            {
                if (r.Node == null || !IsWithin(path, r.Node.FullPath.TrimEnd('\\'))) continue;
                var n = FindByPath(r.Node, path);
                if (n != null && (!exact || SamePath(n.FullPath, path))) return n;
            }
            return null;
        }

        /// <summary>Brings the scanned trees up to date after a deletion: removes what is gone, rescans folders that remain.</summary>
        async Task ApplyDeleted(List<DeleteTarget> targets)
        {
            foreach (var t in targets)
            {
                if (t.RecycleBin) continue;
                if (!t.IsDir)
                {
                    var d = t.File != null && IsAttached(t.File.Dir) ? t.File.Dir : FindScanned(Native.ParentDir(t.Path), true);
                    if (d != null && !Native.Exists(t.Path)) d.RemoveFileRec(Native.FileName(t.Path));
                    continue;
                }
                var dn = t.Dir != null && IsAttached(t.Dir) ? t.Dir : FindScanned(t.Path, true);
                if (dn == null) continue;
                if (!Native.Exists(t.Path)) { if (dn.Parent != null) RemoveNode(dn); continue; }
                string p = t.Path;
                DirNode fresh = null;
                var s = new Scanner();
                await RunThread(() => fresh = s.Run(p));
                if (IsAttached(dn)) ReplaceNode(dn, fresh);
            }
        }
    }
}
