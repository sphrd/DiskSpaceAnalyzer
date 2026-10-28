using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>
    /// Live updates: every scanned drive or folder is watched for changes, and the scan is updated in place every two
    /// seconds (a changed folder's own files are read again; new folders are scanned; removed ones dropped). Nothing is
    /// rescanned as a whole. When Windows reports that changes were missed, the status line suggests a rescan.
    /// </summary>
    internal sealed partial class MainForm
    {
        struct FsChange { public WatcherChangeTypes Kind; public string Path, OldPath; }

        readonly Dictionary<ScanRoot, FileSystemWatcher> watchers = new Dictionary<ScanRoot, FileSystemWatcher>();
        readonly ConcurrentQueue<FsChange> fsChanges = new ConcurrentQueue<FsChange>();
        readonly HashSet<ScanRoot> missedChanges = new HashSet<ScanRoot>();
        System.Windows.Forms.Timer liveTimer;
        DateTime ignoreChangesUntil = DateTime.MinValue;
        DateTime lastLiveRefresh = DateTime.MinValue;
        bool livePending;
        Label liveLabel;

        void StartWatching(ScanRoot r)
        {
            StopWatching(r);
            missedChanges.Remove(r);
            if (liveTimer == null)
            {
                liveTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                liveTimer.Tick += (s, e) => { try { ApplyChanges(); } catch (Exception) { livePending = false; } };   // a live update must never take the app down
                liveTimer.Start();
            }
            try
            {
                var w = new FileSystemWatcher(r.Path)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
                };
                FileSystemEventHandler onChange = (s, e) => Enqueue(e.ChangeType, e.FullPath, null);
                w.Created += onChange;
                w.Deleted += onChange;
                w.Changed += onChange;
                w.Renamed += (s, e) => Enqueue(WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath);
                w.Error += (s, e) => BeginInvoke(new Action(() => { if (DateTime.UtcNow > ignoreChangesUntil) { missedChanges.Add(r); UpdateLiveLabel(); } }));
                w.EnableRaisingEvents = true;
                watchers[r] = w;
            }
            catch (Exception) { }
            UpdateLiveLabel();
        }

        void StopWatching(ScanRoot r)
        {
            FileSystemWatcher w;
            if (!watchers.TryGetValue(r, out w)) return;
            watchers.Remove(r);
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch (Exception) { }
        }

        void Enqueue(WatcherChangeTypes kind, string path, string old)
        {
            // Our own deletions are applied directly; their echo is ignored.
            if (busy || DateTime.UtcNow < ignoreChangesUntil) return;
            if (fsChanges.Count > 200000) return;
            fsChanges.Enqueue(new FsChange { Kind = kind, Path = path, OldPath = old });
        }

        /// <summary>Called when our own delete or rescan finishes: changes until now are already in the scan.</summary>
        void IgnoreOwnChanges()
        {
            ignoreChangesUntil = DateTime.UtcNow.AddSeconds(3);
            FsChange c;
            while (fsChanges.TryDequeue(out c)) { }
        }

        void UpdateLiveLabel()
        {
            if (liveLabel == null) return;
            foreach (var r in watchers.Keys.ToList()) if (!roots.Contains(r) || r.Node == null) StopWatching(r);
            if (missedChanges.Count > 0)
            {
                liveLabel.Text = "Changes missed - rescan";
                liveLabel.ForeColor = Theme.WARN_FG;
                tips.SetToolTip(liveLabel, "Too many changes happened at once on " + string.Join(", ", missedChanges.Select(r => r.Path)) +
                    "; the scan may be out of date. Rescan (F5) to be sure.");
            }
            else
            {
                liveLabel.Text = watchers.Count > 0 ? "Live" : "";
                liveLabel.ForeColor = Theme.MUTED;
                tips.SetToolTip(liveLabel, "The scanned drives and folders are watched; files that appear, change or go away update the results within seconds.");
            }
        }

        void ApplyChanges()
        {
            if (busy || scanning || fsChanges.IsEmpty) { if (livePending && !busy && !scanning) RefreshAfterLive(); return; }
            var files = new HashSet<DirNode>();
            var added = new List<string>();
            int n = 0;
            bool structure = false;
            FsChange c;
            while (n++ < 20000 && fsChanges.TryDequeue(out c))
            {
                var parent = FindScanned(Native.ParentDir(c.Path), true);
                if (c.Kind == WatcherChangeTypes.Renamed && c.OldPath != null)
                {
                    var oldNode = FindScanned(c.OldPath, true);
                    if (oldNode != null && oldNode.Parent != null)
                    {
                        var newParent = FindScanned(Native.ParentDir(c.Path), true);
                        if (newParent == oldNode.Parent) { oldNode.Name = Native.FileName(c.Path); oldNode.Parent.Children.Sort(DirNode.BySizeDesc); structure = true; }
                        else { RemoveNode(oldNode); added.Add(c.Path); structure = true; }
                        continue;
                    }
                    var oldParent = FindScanned(Native.ParentDir(c.OldPath), true);
                    if (oldParent != null) files.Add(oldParent);
                }
                if (parent == null) continue;
                string name = Native.FileName(c.Path);
                var node = parent.Child(name);
                if (c.Kind == WatcherChangeTypes.Deleted)
                {
                    if (node != null) { RemoveNode(node); structure = true; }
                    else files.Add(parent);
                }
                else if (Native.IsDirectory(c.Path))
                {
                    if (node == null && c.Kind != WatcherChangeTypes.Changed) { added.Add(c.Path); structure = true; }
                }
                else files.Add(parent);
            }
            foreach (var d in files) if (IsAttached(d)) Scanner.RefreshFiles(d);
            foreach (var p in added.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var parent = FindScanned(Native.ParentDir(p), true);
                if (parent == null || parent.Child(Native.FileName(p)) != null || !Native.IsDirectory(p)) continue;
                var fresh = new Scanner().Run(p);   // usually small (a new folder); large copies arrive folder by folder anyway
                fresh.Name = Native.FileName(p);
                fresh.Parent = parent;
                fresh.Modified = DateTime.Now.ToFileTime();
                if (parent.Children == null) parent.Children = new List<DirNode>();
                parent.Children.Add(fresh);
                parent.AddUp(fresh.Bytes, fresh.Alloc, fresh.Files, fresh.Dirs + 1);
            }
            if (files.Count > 0 || added.Count > 0 || structure)
            {
                livePending = true;
                if (structure) foreach (var p in pages) if (p != pContents) p.Stale = true;
            }
            if (livePending && (DateTime.UtcNow - lastLiveRefresh).TotalSeconds >= 4) RefreshAfterLive();
        }

        /// <summary>Shows live changes without disturbing the user: values update in place, the selection and scroll position stay.</summary>
        void RefreshAfterLive()
        {
            livePending = false;
            lastLiveRefresh = DateTime.UtcNow;
            tree.Invalidate();
            UpdateDriveLabel();
            if (current == null || !IsAttached(current)) return;
            if (ActivePage != pContents) return;
            if (contents.SelectedItems.Count > 0 || contents.Focused && MouseButtons != MouseButtons.None) return;
            var top = contents.TopItem != null ? ((ItemInfo)contents.TopItem.Tag).Name : null;
            ShowContents(current);
            if (top != null)
                foreach (ListViewItem it in contents.Items)
                    if (((ItemInfo)it.Tag).Name == top) { try { contents.TopItem = it; } catch (Exception) { } break; }
        }
    }
}
