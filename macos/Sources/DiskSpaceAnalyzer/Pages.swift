import AppKit

/// The views shown in tabs above the list and the chart. Every view is a list; the chart always shows the list that is
/// open, slice for row in the same order, and hover is linked both ways.
extension MainWindowController {
    static let minSizes: [Int64] = [0, 1 << 20, 10 << 20, 100 << 20, 1 << 30]
    static let ageMonths = [0, 3, 6, 12, 24, 60]
    static let maxOwnSlices = 12          // at most this many rows get a slice of their own...
    static let minOwnShare = 0.015        // ...and only if they hold at least 1.5 % of the total

    var activePage: Page { pages[min(max(0, viewTabs.selectedIndex), pages.count - 1)] }

    func buildViews() {
        pContents = Page("Contents", [Column("name", "Name", 230), Column("size", "Size", 84, true), Column("size", "% of folder", 150, true),
                                      Column("change", "Change", 84, true), Column("files", "Files", 74, true), Column("dirs", "Folders", 66, true),
                                      Column("mod", "Modified", 128)], barColumn: 2, bar: nil)
        pContents.followsFolder = true
        pContents.fill = { [unowned self] in if let c = self.current, self.isAttached(c) { self.showContents(c) } }
        pages.append(pContents)

        // Files: the whole index under the selected folder, filtered; largest first.
        fileSearch.onChange = { [unowned self] in self.fillFiles() }
        fileSearch.toolTip = "Part of a name, or a pattern with * and ?. Several patterns: separate them with ; (⌘F)"
        fileAge.tip = "Files not changed (modified) for at least this long"
        for c in [fileType, fileMinSize, fileAge] { c.onChange = { [unowned self] _ in self.fillFiles() } }
        pFiles = Page("Files", [Column("name", "Name", 230), Column("size", "Size", 84, true), Column("size", "% of folder", 150, true),
                                Column("folder", "Folder", 330), Column("cat", "Type", 110), Column("mod", "Modified", 128)],
                      barColumn: 2, bar: ActionBar([fileSearch, fileType, fileMinSize, fileAge]))
        pFiles.followsFolder = true
        pFiles.chartScope = "of folder"
        pFiles.fill = { [unowned self] in self.fillFiles() }
        pages.append(pFiles)

        // File types: space by extension or category under the selected folder.
        typeGroup.onChange = { [unowned self] _ in self.fillTypes() }
        pTypes = Page("File types", [Column("name", "Type", 180), Column("size", "Size", 84, true), Column("size", "% of folder", 160, true),
                                     Column("files", "Files", 84, true), Column("cat", "Category", 170)],
                      barColumn: 2, bar: ActionBar([typeGroup]))
        pTypes.followsFolder = true
        pTypes.chartScope = "of folder"
        pTypes.fill = { [unowned self] in self.fillTypes() }
        pages.append(pTypes)

        pDupes = buildDuplicatesPage(); pages.append(pDupes)
        pSuggest = buildSuggestionsPage(); pages.append(pSuggest)
        pChanges = buildChangesPage(); pages.append(pChanges)
        pBasket = buildBasketPage(); pages.append(pBasket)
    }

    func showPage() {
        let p = activePage
        for q in pages { q.root.isHidden = q !== p }
        listPanel.needsLayout = true
        if p.stale { p.stale = false; p.fill() } else { refreshChart() }
        lastView = p.table
        updateButtons()
    }

    /// The chart follows the open view.
    func refreshChart() {
        let p = activePage
        if p === pContents {
            if let c = current, isAttached(c) { updateChart(c) }
            else if let a = active, a.node == nil { updateChartUnscanned(a) }
            return
        }
        if p.items.isEmpty {
            rowSlice = [:]
            chart.showLogo(p.chartTitle, p.chartSub, p.empty)
            return
        }
        var total = p.chartTotal
        if total <= 0 { for it in p.items where it.groupOf == nil { total += max(0, it.chartSize) } }
        chart.setData(p.chartTitle, p.chartSub, buildSlices(p, total, p.chartScope))
    }

    /// The scanned tree changed (scan, rescan, delete, measure): views refill when they are shown next.
    func viewsChanged() {
        if pages.isEmpty { return }
        for p in pages where p !== pContents { p.stale = true }
        updateBasketTitle()
        let a = activePage
        if a !== pContents && a.stale { a.stale = false; a.fill() }
    }

    /// Another folder was opened: folder-based views follow it.
    func contentsChanged() {
        for p in pages where p.followsFolder && p !== pContents { p.stale = true }
        let a = activePage
        if a === pContents { if let c = current { updateChart(c) } }
        else if a.stale { a.stale = false; a.fill() }
    }

    func scope() -> DirNode? {
        if let c = current, isAttached(c) { return c }
        return active?.node ?? roots.first { $0.node != nil }?.node
    }

    /// Slices for a list exactly as displayed: one slice per row in row order, except that runs of consecutive small rows
    /// are merged into one "smaller items" slice. Rows of a group (duplicates) join their group's slice. Fills rowSlice
    /// so hover links rows and slices in both directions.
    func buildSlices(_ p: Page, _ total0: Int64, _ scopeText: String) -> [ChartSlice] {
        rowSlice = [:]
        let rows = p.items
        let total = max(Int64(1), total0)
        let candidates = rows.indices.filter { rows[$0].groupOf == nil && rows[$0].chartSize > 0 && Double(rows[$0].chartSize) >= MainWindowController.minOwnShare * Double(total) }
        let own = Set(candidates.sorted { rows[$0].chartSize > rows[$1].chartSize }.prefix(MainWindowController.maxOwnSlices))
        var slices: [ChartSlice] = []
        var runs: [ChartSlice] = []
        var headSlice: [ObjectIdentifier: Int] = [:]
        var run: ChartSlice?
        var colour = 0
        for (i, info) in rows.enumerated() {
            if let g = info.groupOf, let head = headSlice[ObjectIdentifier(g)] {
                slices[head].rows.append(i)
                rowSlice[i] = head
                continue
            }
            let size = info.chartSize
            if own.contains(i) {
                run = nil
                let s = ChartSlice(info.name, size, T.CHART[colour % T.CHART.count])
                colour += 1
                s.detail = Fmt.pct(100 * Double(size) / Double(total)) + " " + scopeText
                if info.isDir, let d = info.dir, isAttached(d) { s.dir = d }
                s.rows = [i]
                slices.append(s)
            } else {
                if run == nil { let n = ChartSlice("", 0, T.CHART_OTHER); slices.append(n); runs.append(n); run = n }
                run!.rows.append(i)
                run!.size += max(0, size)
            }
            rowSlice[i] = slices.count - 1
            headSlice[ObjectIdentifier(info)] = slices.count - 1
        }
        for s in runs {
            let n = s.rows.filter { rows[$0].groupOf == nil }.count
            s.label = n == 1 ? rows[s.rows[0]].name : "\(Fmt.count(n)) smaller items"
            s.detail = Fmt.pct(100 * Double(s.size) / Double(total)) + " " + scopeText
        }
        return slices
    }

    /// Folder contents chart with two outer rings; a drive root also gets "System / not readable" and "Free space".
    func updateChart(_ d: DirNode) {
        let r = rootOf(d.top)
        if let r = r { refreshCapacity(r) }
        var slices = buildSlices(pContents, d.size, "of folder")
        var title = d.name, sub = d.fullPath
        if d.parent == nil, let r = r {
            title = r.displayName
            if r.isDrive && r.total > 0 {
                if r.used - d.size > 0 {
                    let s = ChartSlice("System / not readable", r.used - d.size, T.PILL)
                    s.detail = Fmt.pct(100 * Double(r.used - d.size) / Double(r.total)) + " of drive"
                    slices.append(s)
                }
                let f = ChartSlice("Free space", r.free, T.CHART_FREE)
                f.empty = true
                f.detail = Fmt.pct(100 * Double(r.free) / Double(r.total)) + " of drive"
                slices.append(f)
                sub = "\(Fmt.size(r.used)) used of \(Fmt.size(r.total))  -  \(Fmt.size(r.free)) free"
            }
        }
        chart.setData(title, sub, slices, rings: 2)
    }

    /// Nothing scanned yet: the chart pane shows the app logo; it morphs into the chart once the scan is done.
    func updateChartUnscanned(_ r: ScanRoot) {
        rowSlice = [:]
        let sub = r.total > 0 ? "\(Fmt.size(r.used)) used of \(Fmt.size(r.total))  -  \(Fmt.size(r.free)) free" : r.path
        chart.showLogo(r.displayName, sub, r.scanning ? "Scanning... the chart appears when the scan is done." : "Not scanned yet. Press Scan, or double-click the drive.")
    }

    func onListDoubleClick(_ p: Page) {
        let sel = p.selectedItems
        guard sel.count == 1 else { return }
        let info = sel[0]
        if p === pTypes {
            // Open the Files view with this type.
            if let e = info.ext { fileType.selectedIndex = 0; fileSearch.text = e.isEmpty ? "" : "*." + e }
            else { fileSearch.text = ""; fileType.selectedIndex = (FileTypes.categories.firstIndex(of: info.category) ?? -1) + 1 }
            viewTabs.selectedIndex = pages.firstIndex { $0 === pFiles } ?? 1
            fillFiles()
            return
        }
        if info.isGroup || info.isSummary { return }
        openSelected()
    }

    func showFilesSearch() {
        viewTabs.selectedIndex = pages.firstIndex { $0 === pFiles } ?? 1
        fileSearch.focus()
    }

    func setStale(_ p: Page) { p.stale = true; if activePage === p { p.stale = false; p.fill() } }

    // MARK: Files view

    func currentFileFilter() -> FileFilter {
        let f = FileFilter()
        f.patterns = fileSearch.text.split(whereSeparator: { $0 == ";" || $0 == "," })
            .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }.filter { !$0.isEmpty }
        if fileType.selectedIndex > 0 { f.category = FileTypes.categories[fileType.selectedIndex - 1] }
        f.minSize = MainWindowController.minSizes[fileMinSize.selectedIndex]
        let months = MainWindowController.ageMonths[fileAge.selectedIndex]
        if months > 0, let d = Calendar.current.date(byAdding: .month, value: -months, to: Date()) { f.modifiedBefore = Int64(d.timeIntervalSince1970) }
        return f
    }

    func fillFiles() {
        let p = pFiles!
        p.chartTitle = "Files"
        guard let sc = scope() else {
            p.setCaption("Nothing scanned yet.")
            p.chartSub = ""
            p.empty = "Scan a drive or folder first."
            p.setRows([], "Scan a drive or folder to search its files.")
            if activePage === p { refreshChart() }
            return
        }
        let filter = currentFileFilter()
        filesGen += 1
        let gen = filesGen
        let scopePath = sc.fullPath
        p.setCaption("Searching " + scopePath + "...")
        readInBackground({ () -> ([ItemInfo], Int64, Int64, Int) in
            let found = FileIndex.largest(sc, MainWindowController.topCount, filter)
            let folder = max(Int64(1), sc.size)
            var rows: [ItemInfo] = []
            var shown: Int64 = 0
            for fe in found.files {
                let it = ItemInfo.forFile(fe, folder)
                it.folder = fe.dir.fullPath
                it.category = FileTypes.category(FileIndex.ext(fe.name))
                shown += fe.size
                it.cells = [it.name, Fmt.size(it.size), Fmt.pct(it.percent), it.folder, it.category, Fmt.date(it.modified)]
                rows.append(it)
            }
            if found.count > Int64(found.files.count) {
                let rest = ItemInfo()
                rest.isSummary = true
                rest.name = "[\(Fmt.count(found.count - Int64(found.files.count))) smaller matching files not listed]"
                rest.size = found.size - shown; rest.files = found.count - Int64(found.files.count)
                rest.percent = 100 * Double(found.size - shown) / Double(folder)
                rest.cells = [rest.name, Fmt.size(rest.size), Fmt.pct(rest.percent), "", "", ""]
                rows.append(rest)
            }
            return (rows, found.count, found.size, found.files.count)
        }, { [weak self] res in
            guard let self = self, gen == self.filesGen else { return }
            let (rows, count, size, shownCount) = res
            let filtered = !filter.isEmpty
            p.setRows(rows, filtered ? "No files match the filters in \(scopePath)." : "No files in \(scopePath).")
            p.setCaption("\(Fmt.count(count)) \(filtered ? "matching " : "")files in \(Fmt.shorten(scopePath, 70)), \(Fmt.size(size))"
                         + (count > Int64(shownCount) ? "  -  showing the largest \(Fmt.count(shownCount))" : ""))
            p.chartTitle = filtered ? "Matching files" : "Largest files"
            p.chartSub = scopePath
            p.chartTotal = 0
            p.chartScope = "of the matches"
            p.empty = filtered ? "No files match." : "No files."
            if self.activePage === p { self.refreshChart() }
        })
    }

    // MARK: File types view

    func fillTypes() {
        let p = pTypes!
        p.chartTitle = "File types"
        guard let sc = scope() else {
            p.setCaption("Nothing scanned yet.")
            p.chartSub = ""
            p.empty = "Scan a drive or folder first."
            p.setRows([], "Scan a drive or folder to see its file types.")
            if activePage === p { refreshChart() }
            return
        }
        let byCategory = typeGroup.selectedIndex == 1
        typesGen += 1
        let gen = typesGen
        let scopePath = sc.fullPath
        p.setCaption("Counting file types in " + scopePath + "...")
        readInBackground({ () -> ([ItemInfo], Int64) in
            var sizes: [String: (Int64, Int64)] = [:]
            for d in sc.descendants() {
                guard let list = d.fileList else { continue }
                for f in list {
                    let e = FileIndex.ext(f.name)
                    let key = byCategory ? FileTypes.category(e) : e
                    let v = sizes[key] ?? (0, 0)
                    sizes[key] = (v.0 + FileIndex.measure(f), v.1 + 1)
                }
            }
            let total = max(Int64(1), sc.size)
            var rows: [ItemInfo] = []
            for (k, v) in sizes {
                let it = ItemInfo()
                it.size = v.0; it.files = v.1; it.percent = 100 * Double(v.0) / Double(total)
                if byCategory { it.name = k; it.category = k }
                else { it.ext = k; it.name = k.isEmpty ? "(no extension)" : "." + k; it.category = FileTypes.category(k) }
                it.cells = [it.name, Fmt.size(it.size), Fmt.pct(it.percent), Fmt.count(it.files), byCategory ? "" : it.category]
                rows.append(it)
            }
            return (rows, sc.size)
        }, { [weak self] res in
            guard let self = self, gen == self.typesGen else { return }
            p.setRows(res.0, "No files in \(scopePath).")
            p.setCaption("\(Fmt.count(res.0.count)) \(byCategory ? "categories" : "file types") in \(Fmt.shorten(scopePath, 80))  -  double-click one to list its files")
            p.chartSub = scopePath
            p.chartTotal = res.1
            p.empty = "No files."
            if self.activePage === p { self.refreshChart() }
        })
    }
}
