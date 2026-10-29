using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>
    /// The views shown in tabs above the list and the chart. Every view is a list; the chart always shows the list that
    /// is open, slice for row in the same order, and hover is linked both ways.
    /// </summary>
    internal sealed partial class MainForm
    {
        sealed class Page
        {
            public string Title;
            public Panel Root;                // fills the list panel while the page is shown
            public Label Caption;             // one muted line: what the list shows
            public SizeListView List;
            public bool Stale = true;
            public bool FollowsFolder;        // refilled when another folder is selected in the tree
            public Action Fill;               // rebuilds the list from memory
            public string ChartTitle = "", ChartSub = "", ChartScope = "of the list";
            public long ChartTotal;           // 0 = the sum of the rows
            public string Empty = "";         // shown in the chart pane when the list is empty
        }

        readonly List<Page> pages = new List<Page>();
        Page pContents, pFiles, pTypes, pDupes, pSuggest, pChanges, pBasket;
        TabStrip viewTabs;
        Page ActivePage { get { return pages[viewTabs != null ? viewTabs.SelectedIndex : 0]; } }

        // Files view
        SearchBox fileSearch;
        ChoiceBox fileType, fileMinSize, fileAge;
        int filesGen;
        // Types view
        ChoiceBox typeGroup;
        int typesGen;

        static readonly long[] MinSizes = { 0, 1L << 20, 10L << 20, 100L << 20, 1L << 30 };
        static readonly int[] AgeMonths = { 0, 3, 6, 12, 24, 60 };

        void BuildViews(RoundedPanel listPanel)
        {
            pContents = AddPage("Contents", contents, null);
            pContents.FollowsFolder = true;
            pContents.Fill = () => { if (current != null) ShowContents(current); };

            // Files: the whole index under the selected folder, filtered; largest first.
            fileSearch = new SearchBox("Search names  (e.g. *.iso, invoice)");
            fileSearch.TextChangedDelayed += (s, e) => FillFiles();
            var types = new List<string> { "Any type" };
            types.AddRange(FileTypes.Categories);
            fileType = new ChoiceBox("", types.ToArray());
            fileMinSize = new ChoiceBox("", "Any size", "1 MB or more", "10 MB or more", "100 MB or more", "1 GB or more");
            fileAge = new ChoiceBox("", "Any date", "Older than 3 months", "Older than 6 months", "Older than 1 year", "Older than 2 years", "Older than 5 years");
            tips.SetToolTip(fileAge, "Files not changed (modified) for at least this long");
            foreach (var c in new[] { fileType, fileMinSize, fileAge }) c.SelectedIndexChanged += (s, e) => FillFiles();
            tips.SetToolTip(fileAge, "Files not changed (modified) for at least this long");
            tips.SetToolTip(fileSearch.Box, "Part of a name, or a pattern with * and ?. Several patterns: separate them with ; (Ctrl+F)");
            var files = NewList(new[] { "Name", "Size", "% of folder", "Folder", "Type", "Modified" },
                new[] { "name", "size", "size", "folder", "cat", "mod" }, new[] { 260, 90, 150, 360, 110, 140 }, 2);
            pFiles = AddPage("Files", files, MakeBar(fileSearch, fileType, fileMinSize, fileAge));
            pFiles.FollowsFolder = true;
            pFiles.ChartScope = "of folder";
            pFiles.Fill = FillFiles;

            // Types: space by extension or category under the selected folder.
            typeGroup = new ChoiceBox("Group by: ", "Extension", "Category");
            typeGroup.SelectedIndexChanged += (s, e) => FillTypes();
            var typeList = NewList(new[] { "Type", "Size", "% of folder", "Files", "Category" },
                new[] { "name", "size", "size", "files", "cat" }, new[] { 200, 90, 170, 90, 170 }, 2);
            pTypes = AddPage("File types", typeList, MakeBar(typeGroup));
            pTypes.FollowsFolder = true;
            pTypes.ChartScope = "of folder";
            pTypes.Fill = FillTypes;

            pDupes = BuildDuplicatesPage();
            pSuggest = BuildSuggestionsPage();
            pChanges = BuildChangesPage();
            pBasket = BuildBasketPage();

            viewTabs = new TabStrip(pages.Select(p => p.Title).ToArray()) { Dock = DockStyle.Top };
            viewTabs.SelectedIndexChanged += (s, e) => ShowPage();
            viewTabs.SetColor(pages.IndexOf(pSuggest), Theme.SUGGEST);
            foreach (var p in pages) listPanel.Controls.Add(p.Root);
            pContents.Root.Visible = true;
        }

        SizeListView NewList(string[] titles, string[] keys, int[] widths, int barColumn)
        {
            var lv = new SizeListView { ContextMenuStrip = menu };
            lv.SetColumns(titles, keys, widths);
            lv.BarColumn = barColumn;
            return lv;
        }

        Page AddPage(string title, SizeListView list, Control bar)
        {
            var p = new Page { Title = title, List = list };
            p.Root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SURFACE, Visible = false };
            p.Caption = new Label
            {
                Dock = DockStyle.Top, AutoEllipsis = true, UseMnemonic = false, Font = Theme.FONT_9, ForeColor = Theme.MUTED,
                BackColor = Theme.SURFACE, Height = Theme.FONT_9.Height + S(10), TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(S(4), 0, 0, 0)
            };
            // One line, or two when the text needs it (then it wraps; anything longer ends in an ellipsis).
            var cap = p.Caption;
            EventHandler fit = (s, e) =>
            {
                int w = Math.Max(10, cap.ClientSize.Width - cap.Padding.Horizontal);
                int th = TextRenderer.MeasureText(cap.Text, cap.Font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                int lines = th > cap.Font.Height * 1.5 ? 2 : 1;
                int hgt = cap.Font.Height * lines + S(10);
                if (cap.Height != hgt) cap.Height = hgt;
            };
            cap.TextChanged += fit;
            cap.SizeChanged += fit;
            p.Root.Controls.Add(new ScrollHost(list) { Dock = DockStyle.Fill });
            p.Root.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(4), BackColor = Theme.SURFACE });
            if (bar != null) p.Root.Controls.Add(bar);
            p.Root.Controls.Add(p.Caption);

            list.ColumnClick += (s, e) =>
            {
                if (list == (pDupes != null ? pDupes.List : null) || list == (pSuggest != null ? pSuggest.List : null)) return;   // groups and trees keep their order
                list.SortBy(e.Column);
                if (ActivePage.List == list) RefreshChart();
            };
            list.HotItemChanged += it =>
            {
                tips.SetToolTip(list, it != null ? HoverText(it.Tag as ItemInfo) : null);
                if (ActivePage.List != list) return;
                int i;
                chart.SetHover(it != null && rowSlice.TryGetValue(it, out i) ? i : -1);
            };
            list.DoubleClick += (s, e) => OnListDoubleClick(p);
            list.Enter += (s, e) => lastView = list;
            pages.Add(p);
            return p;
        }

        /// <summary>
        /// A row of filter or action controls above a list; controls tagged "right" sit at the right end. When the pane is
        /// too narrow the controls wrap onto further rows; a search box takes the spare width of its row.
        /// </summary>
        Panel MakeBar(params Control[] items)
        {
            var bar = new Panel { Dock = DockStyle.Top, BackColor = Theme.SURFACE };
            bar.Controls.AddRange(items);
            bar.Layout += (s, e) =>
            {
                int gap = S(6), W = Math.Max(S(100), bar.ClientSize.Width), h = Theme.FONT.Height + S(12);
                foreach (var c in items) h = Math.Max(h, c is SearchBox ? ((SearchBox)c).PreferredHeight : c.GetPreferredSize(Size.Empty).Height);
                Func<Control, int> pref = c => c is SearchBox ? S(220) : c.GetPreferredSize(Size.Empty).Width;
                var rows = new List<List<Control>> { new List<Control>() };
                int x = 0;
                var leftItems = items.Where(c => c.Visible && !"right".Equals(c.Tag)).ToList();
                var rightItems = items.Where(c => c.Visible && "right".Equals(c.Tag)).ToList();
                // Right-hand actions that don't fit on one row together flow like the others.
                bool rightFlows = rightItems.Sum(c => pref(c) + gap) - gap > W;
                if (rightFlows) leftItems.AddRange(rightItems);
                bool oneRow = leftItems.Sum(c => pref(c) + gap) + (rightFlows ? 0 : rightItems.Sum(c => pref(c) + gap)) <= W;
                foreach (var c in leftItems)
                {
                    // When the bar wraps, a search box gets the first row to itself.
                    if (!oneRow && c is SearchBox && rows[0].Count == 0) { rows[0].Add(c); rows.Add(new List<Control>()); x = 0; continue; }
                    int w = pref(c);
                    bool firstRight = rightFlows && c == rightItems[0];   // the right-hand actions start a row of their own
                    if (rows[rows.Count - 1].Count > 0 && (x + w > W || firstRight)) { rows.Add(new List<Control>()); x = 0; }
                    rows[rows.Count - 1].Add(c);
                    x += w + gap;
                }
                var right = rightFlows ? new List<Control>() : rightItems;
                int rightW = right.Sum(c => pref(c) + gap) - (right.Count > 0 ? gap : 0);
                bool rightOwnRow = right.Count > 0 && rows[rows.Count - 1].Count > 0 && x + rightW > W;
                int y = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    int avail = W - (i == rows.Count - 1 && right.Count > 0 && !rightOwnRow ? rightW + S(16) : 0);
                    int used = row.Sum(c => pref(c) + gap) - (row.Count > 0 ? gap : 0);
                    int extra = Math.Max(0, avail - used);
                    x = 0;
                    foreach (var c in row)
                    {
                        int w = pref(c) + (c is SearchBox ? extra : 0);
                        if (c is Label) c.Bounds = new Rectangle(x, y + (h - c.PreferredSize.Height) / 2, w, c.PreferredSize.Height);
                        else c.Bounds = new Rectangle(x, y, w, h);
                        x += w + gap;
                    }
                    if (i < rows.Count - 1 || rightOwnRow) y += h + gap;
                }
                x = W;
                for (int i = right.Count - 1; i >= 0; i--)
                {
                    int w = pref(right[i]);
                    x -= w;
                    right[i].Bounds = new Rectangle(Math.Max(0, x), y, w, h);
                    x -= gap;
                }
                int total = y + h + S(4);
                if (bar.Height != total) bar.Height = total;
            };
            return bar;
        }
        void ShowPage()
        {
            var p = ActivePage;
            foreach (var q in pages) q.Root.Visible = q == p;
            if (p.Stale) { p.Stale = false; p.Fill(); }
            else RefreshChart();
            lastView = p.List;
            UpdateButtons();
        }

        /// <summary>The chart follows the open view.</summary>
        void RefreshChart()
        {
            var p = ActivePage;
            if (p == pContents)
            {
                if (current != null && IsAttached(current)) UpdateChart(current);
                else if (active != null && active.Node == null) UpdateChartUnscanned(active);
                return;
            }
            if (p.List.Items.Count == 0)
            {
                rowSlice.Clear();
                chart.ShowLogo(p.ChartTitle, p.ChartSub, p.Empty);
                return;
            }
            long total = p.ChartTotal;
            if (total <= 0) foreach (ListViewItem it in p.List.Items) { var i = it.Tag as ItemInfo; if (i != null && i.GroupOf == null) total += Math.Max(0, i.ChartSize); }
            chart.SetData(p.ChartTitle, p.ChartSub, BuildSlices(p.List, total, p.ChartScope));
        }

        /// <summary>The scanned tree changed (scan, rescan, delete, measure): views refill when they are shown next.</summary>
        void ViewsChanged()
        {
            if (pages.Count == 0) return;
            suggestionsDirty = true;
            foreach (var p in pages) if (p != pContents) p.Stale = true;
            UpdateBasketTitle();
            var a = ActivePage;
            if (a != pContents && a.Stale) { a.Stale = false; a.Fill(); }
        }

        /// <summary>Another folder was opened (ShowContents): folder-based views follow it.</summary>
        void ContentsChanged()
        {
            foreach (var p in pages) if (p.FollowsFolder && p != pContents) p.Stale = true;
            var a = ActivePage;
            if (a == pContents) UpdateChart(current);
            else if (a.Stale) { a.Stale = false; a.Fill(); }
        }

        DirNode Scope() { return current != null && IsAttached(current) ? current : root; }

        void OnListDoubleClick(Page p)
        {
            var lvi = p.List.SelectedItems.Count == 1 ? p.List.SelectedItems[0] : null;
            var info = lvi != null ? lvi.Tag as ItemInfo : null;
            if (info == null) return;
            if (p == pTypes)
            {
                // Open the Files view with this type.
                if (info.Ext != null) { fileType.SelectedIndex = 0; fileSearch.Text = info.Ext.Length > 0 ? "*." + info.Ext : ""; }
                else { fileSearch.Text = ""; fileType.SelectedIndex = Math.Max(0, Array.IndexOf(FileTypes.Categories, info.Category) + 1); }
                viewTabs.SelectedIndex = pages.IndexOf(pFiles);
                FillFiles();
                return;
            }
            if (info.IsGroup) return;
            OpenSelected();
        }

        /// <summary>Tooltip for a row: the full name, then where it is (full path), so ellipsized names can be read.</summary>
        static string HoverText(ItemInfo it)
        {
            if (it == null || it.IsSummary) return null;
            if (it.IsGroup) return it.Name;
            if (it.Ext != null || (it.Category != null && it.File == null && it.Dir == null && it.Targets == null))
                return it.Name + "\n" + string.Format("{0:N0} files, {1}", it.Files, Fmt.Size(it.Size));
            string where;
            if (it.IsDir) where = it.Dir.FullPath;
            else if (it.File != null) where = it.File.FullPath;
            else if (it.Targets != null && it.Targets.Count == 1 && it.Targets[0].RecycleBin) where = "Recycle Bin on all drives";
            else if (it.Targets != null && it.Targets.Count > 1) where = it.Folder;
            else where = it.Path ?? it.Folder;
            if (string.IsNullOrEmpty(where) || where == it.Name) return it.Name;
            return it.Name + "\n" + where;
        }

        void ShowFilesSearch()
        {
            viewTabs.SelectedIndex = pages.IndexOf(pFiles);
            fileSearch.Box.Focus();
            fileSearch.Box.SelectAll();
        }

        static ListViewItem Row(ItemInfo info, params string[] cells)
        {
            var lvi = new ListViewItem(cells) { Tag = info };
            if (info.IsSummary) lvi.ForeColor = Theme.MUTED;
            return lvi;
        }

        static void SetRows(SizeListView lv, List<ListViewItem> rows, string empty)
        {
            lv.BeginUpdate();
            lv.ResetHover();
            lv.Items.Clear();
            lv.Items.AddRange(rows.ToArray());
            lv.EmptyText = rows.Count == 0 ? empty : null;
            lv.EndUpdate();
            lv.Invalidate();
        }

        // ---------------------------------------------------------------- Files view

        FileFilter CurrentFileFilter()
        {
            var f = new FileFilter();
            var pats = fileSearch.Text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            if (pats.Length > 0) f.Patterns = pats;
            if (fileType.SelectedIndex > 0) f.Category = FileTypes.Categories[fileType.SelectedIndex - 1];
            f.MinSize = MinSizes[fileMinSize.SelectedIndex];
            if (AgeMonths[fileAge.SelectedIndex] > 0) f.ModifiedBefore = DateTime.Now.AddMonths(-AgeMonths[fileAge.SelectedIndex]).ToFileTime();
            return f;
        }

        void FillFiles()
        {
            var p = pFiles;
            var scope = Scope();
            p.ChartTitle = "Files";
            if (scope == null)
            {
                p.Caption.Text = "Nothing scanned yet.";
                p.ChartSub = "";
                p.Empty = "Scan a drive or folder first.";
                SetRows(p.List, new List<ListViewItem>(), "Scan a drive or folder to search its files.");
                if (ActivePage == p) RefreshChart();
                return;
            }
            var filter = CurrentFileFilter();
            int gen = ++filesGen;
            p.Caption.Text = "Searching " + scope.FullPath + "...";
            long count = 0, size = 0;
            List<FileEntry> found = null;
            Task.Factory.StartNew(() => { found = FileIndex.Largest(scope, TopCount, filter, out count, out size); })
                .ContinueWith(t =>
                {
                    if (gen != filesGen || IsDisposed) return;
                    if (t.IsFaulted) { p.Stale = true; p.Caption.Text = "The scan changed while searching - search again."; return; }
                    var rows = new List<ListViewItem>();
                    long folder = Math.Max(1, scope.Size), shown = 0;
                    foreach (var fe in found)
                    {
                        var it = ItemInfo.ForFile(fe, folder);
                        it.Folder = fe.Dir.FullPath;
                        it.Category = FileTypes.CategoryOf(FileIndex.Extension(fe.Name));
                        shown += fe.Size;
                        rows.Add(Row(it, it.Name, Fmt.Size(it.Size), it.Percent.ToString("0.0") + " %", it.Folder, it.Category, Fmt.Date(it.Modified)));
                    }
                    if (count > found.Count)
                    {
                        var rest = new ItemInfo
                        {
                            IsSummary = true, Name = string.Format("[{0:N0} smaller matching files not listed]", count - found.Count),
                            Size = size - shown, Files = count - found.Count, Percent = 100.0 * (size - shown) / folder
                        };
                        rows.Add(Row(rest, rest.Name, Fmt.Size(rest.Size), rest.Percent.ToString("0.0") + " %", "", "", ""));
                    }
                    rows.Sort((a, b) => p.List.Sorter.Compare(a, b));
                    bool filtered = !filter.IsEmpty;
                    SetRows(p.List, rows, filtered ? "No files match the filters in " + scope.FullPath + "." : "No files in " + scope.FullPath + ".");
                    p.Caption.Text = string.Format("{0:N0} {1}files in {2}, {3}{4}", count, filtered ? "matching " : "", Fmt.Shorten(scope.FullPath, 70),
                        Fmt.Size(size), count > found.Count ? string.Format("  -  showing the largest {0:N0}", found.Count) : "");
                    p.ChartTitle = filtered ? "Matching files" : "Largest files";
                    p.ChartSub = scope.FullPath;
                    p.ChartTotal = 0;
                    p.ChartScope = "of the matches";
                    p.Empty = filtered ? "No files match." : "No files.";
                    if (ActivePage == p) RefreshChart();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------------------------------------------------------------- Types view

        void FillTypes()
        {
            var p = pTypes;
            var scope = Scope();
            p.ChartTitle = "File types";
            if (scope == null)
            {
                p.Caption.Text = "Nothing scanned yet.";
                p.ChartSub = "";
                p.Empty = "Scan a drive or folder first.";
                SetRows(p.List, new List<ListViewItem>(), "Scan a drive or folder to see its file types.");
                if (ActivePage == p) RefreshChart();
                return;
            }
            bool byCategory = typeGroup.SelectedIndex == 1;
            int gen = ++typesGen;
            p.Caption.Text = "Counting file types in " + scope.FullPath + "...";
            var sizes = new Dictionary<string, long[]>();
            Task.Factory.StartNew(() =>
            {
                foreach (var d in scope.Descendants())
                {
                    if (d.FileList == null) continue;
                    for (int i = 0; i < d.FileList.Length; i++)
                    {
                        string ext = FileIndex.Extension(d.FileList[i].Name);
                        string key = byCategory ? FileTypes.CategoryOf(ext) : ext;
                        long[] v;
                        if (!sizes.TryGetValue(key, out v)) sizes[key] = v = new long[2];
                        v[0] += FileIndex.Measure(ref d.FileList[i]);
                        v[1]++;
                    }
                }
            }).ContinueWith(t =>
            {
                if (gen != typesGen || IsDisposed) return;
                if (t.IsFaulted) { p.Stale = true; p.Caption.Text = "The scan changed while counting - open the view again."; return; }
                long total = Math.Max(1, scope.Size);
                var rows = new List<ListViewItem>();
                foreach (var kv in sizes)
                {
                    var it = new ItemInfo { Size = kv.Value[0], Files = kv.Value[1], Percent = 100.0 * kv.Value[0] / total };
                    if (byCategory) { it.Name = kv.Key; it.Category = kv.Key; }
                    else { it.Ext = kv.Key; it.Name = kv.Key.Length > 0 ? "." + kv.Key : "(no extension)"; it.Category = FileTypes.CategoryOf(kv.Key); }
                    rows.Add(Row(it, it.Name, Fmt.Size(it.Size), it.Percent.ToString("0.0") + " %", it.Files.ToString("N0"), byCategory ? "" : it.Category));
                }
                rows.Sort((a, b) => p.List.Sorter.Compare(a, b));
                SetRows(p.List, rows, "No files in " + scope.FullPath + ".");
                p.Caption.Text = string.Format("{0:N0} {1} in {2}  -  double-click one to list its files", sizes.Count,
                    byCategory ? "categories" : "file types", Fmt.Shorten(scope.FullPath, 80));
                p.ChartSub = scope.FullPath;
                p.ChartTotal = scope.Size;
                p.Empty = "No files.";
                if (ActivePage == p) RefreshChart();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
