using System;
using System.Collections.Generic;
using System.Linq;
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
                    Folder = t.RecycleBin ? "All drives" : t.ContentsOnly ? t.Path : System.IO.Path.GetDirectoryName(t.Path) ?? t.Path
                };
                rows.Add(Row(it, it.Name, Fmt.Size(it.Size), it.Percent.ToString("0.0") + " %", it.Folder, it.Note));
            }
            rows.Sort((a, b) => p.List.Sorter.Compare(a, b));
            SetRows(p.List, rows, "The delete list is empty.\nSelect files or folders in any view and press Add to delete list (Insert), then review and delete them here.");
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
            basket.RemoveAll(t => t.RecycleBin ? done.Errors.Count == 0 : !Native.Exists(t.Path) || (t.ContentsOnly && done.Errors.Count == 0));
            BasketChanged();
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
            if (job != null && basket.Count > 0)
            {
                basket.RemoveAll(t => !t.RecycleBin && !Native.Exists(t.Path));
                BasketChanged();
            }
        }

        /// <summary>Asks, deletes (or recycles) with the busy overlay, then updates the scan. Null when cancelled.</summary>
        async Task<DeleteJob> ConfirmAndDelete(List<DeleteTarget> targets, bool recycle)
        {
            if (targets.Any(t => !t.RecycleBin && roots.Any(r => r.Node != null && SamePath(r.Node.FullPath, t.Path)) && !t.ContentsOnly))
            {
                MessageBox.Show(this, "A scanned drive or folder itself cannot be deleted here.\nSelect items inside it instead.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            long total = targets.Sum(t => t.Size);
            string list = string.Join("\n", targets.Take(12).Select(t => "    " +
                (t.RecycleBin ? "Empty the Recycle Bin" : Fmt.Shorten(t.Path, 90) + (t.ContentsOnly ? "  (contents)" : "")) + "   (" + Fmt.Size(t.Size) + ")"));
            if (targets.Count > 12) list += string.Format("\n    ... and {0:N0} more", targets.Count - 12);
            string msg = recycle
                ? string.Format("Move {0} item{1} ({2}) to the Recycle Bin?\n\n{3}\n\nYou can restore them from the Recycle Bin. The space is freed when the bin is emptied.",
                    targets.Count, targets.Count == 1 ? "" : "s", Fmt.Size(total), list)
                : string.Format("Permanently delete {0} item{1} ({2})?\n\n{3}\n\nThis bypasses the Recycle Bin and CANNOT be undone.",
                    targets.Count, targets.Count == 1 ? "" : "s", Fmt.Size(total), list);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (targets.Any(t => !t.ContentsOnly && !t.RecycleBin && (IsWithin(t.Path, win) || IsWithin(t.Path, pf) || IsWithin(t.Path, pf86))))
                msg += "\n\nWARNING: this includes Windows or Program Files content. Deleting it can break Windows or installed programs.";
            if (SelfTestDir == null && MessageBox.Show(this, msg, AppName + (recycle ? " - move to Recycle Bin" : " - confirm permanent delete"), MessageBoxButtons.YesNo,
                recycle ? MessageBoxIcon.Question : MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return null;

            DirNode target = current;
            foreach (var t in targets)
            {
                var dn = t.ContentsOnly || t.RecycleBin ? null : t.Dir != null && IsAttached(t.Dir) ? t.Dir : FindScanned(t.Path, true);
                if (dn != null && current != null && current.IsUnder(dn)) target = dn.Parent;
            }

            long before = roots.Where(r => r.Node != null).Sum(r => r.Node.Size);
            var job = new DeleteJob();
            deleteJob = job;
            SetBusy(true);
            try
            {
                await RunThread(() => job.Run(targets, recycle));
                job.Current = "Updating the scan...";
                await ApplyDeleted(targets);
            }
            finally { deleteJob = null; SetBusy(false); IgnoreOwnChanges(); }

            if (target == null || !IsAttached(target)) target = root;
            current = null;
            RefreshTree(target);
            ViewsChanged();
            UpdateDriveLabel();
            if (lastView != null && lastView.Visible) lastView.Focus();
            long freed = before - roots.Where(r => r.Node != null).Sum(r => r.Node.Size);
            string skipped = job.Skipped > 0 ? string.Format(", {0:N0} in use were skipped", job.Skipped) : "";
            statusLabel.Text = recycle
                ? string.Format("Moved {0:N0} item{1} ({2}) to the Recycle Bin{3}{4}.", job.Recycled, job.Recycled == 1 ? "" : "s", Fmt.Size(freed), skipped,
                    job.Errors.Count > 0 ? string.Format(", {0:N0} could not be moved", job.Errors.Count) : "")
                : string.Format("Freed {0}. {1:N0} files deleted{2}{3}.", Fmt.Size(Math.Max(0, freed)), job.FilesDeleted, skipped,
                    job.Errors.Count > 0 ? string.Format(", {0:N0} items could not be deleted", job.Errors.Count) : "");

            if (job.Errors.Count > 0 && SelfTestDir == null)
            {
                string err = string.Join("\n", job.Errors.Take(20).Select(e => Fmt.Shorten(e, 160)));
                if (job.Errors.Count > 20) err += string.Format("\n... and {0:N0} more", job.Errors.Count - 20);
                MessageBox.Show(this, "Some items could not be " + (recycle ? "moved" : "deleted") + " (in use, or access denied" +
                    (isAdmin ? "" : " - try Run as administrator") + "):\n\n" + err, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return job;
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
                    var d = t.File != null && IsAttached(t.File.Dir) ? t.File.Dir : FindScanned(System.IO.Path.GetDirectoryName(t.Path), true);
                    if (d != null && !Native.Exists(t.Path)) d.RemoveFileRec(System.IO.Path.GetFileName(t.Path));
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
