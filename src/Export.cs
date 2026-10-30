using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace DiskSpaceAnalyzer
{
    /// <summary>Export: an HTML report (self-contained, opens in any browser) or CSV for spreadsheets.</summary>
    internal sealed partial class MainForm
    {
        async void ExportScan()
        {
            var scope = Scope();
            if (scope == null || busy) return;
            string name = string.Join("_", (scope.Parent == null && active != null ? active.Path : scope.Name).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim('_');
            int format;
            string file;
            using (var dlg = new SaveFileDialog
            {
                Title = "Export " + scope.FullPath,
                Filter = "HTML report (*.html)|*.html|CSV - every folder (*.csv)|*.csv|CSV - the list shown now (*.csv)|*.csv",
                FileName = "Disk usage " + (name.Length > 0 ? name : "scan") + " " + DateTime.Now.ToString("yyyy-MM-dd"),
                OverwritePrompt = true
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                format = dlg.FilterIndex;
                file = dlg.FileName;
            }
            var page = ActivePage;
            string listCsv = format == 3 ? ListCsv(page.List) : null;
            statusLabel.Text = "Exporting to " + file + "...";
            Exception error = null;
            string ver = Updater.Short(Updater.Current);
            var r = RootOf(scope.Top);
            string driveLine = scope.Parent == null && r != null && r.IsDrive && r.Total > 0
                ? string.Format("{0} used of {1}, {2} free", Fmt.Size(r.Total - r.Free), Fmt.Size(r.Total), Fmt.Size(r.Free)) : "";
            try
            {
                await RunThread(() =>
                {
                    if (format == 1) File.WriteAllText(file, HtmlReport(scope, ver, driveLine), new UTF8Encoding(false));
                    else if (format == 2) FoldersCsv(scope, file);
                    else File.WriteAllText(file, listCsv, new UTF8Encoding(true));
                });
            }
            catch (Exception ex) { error = ex; }
            if (error != null)
            {
                statusLabel.Text = "Export failed.";
                Alert(AlertIcon.Warn, "Could not save the export", error.Message, null, null, "OK");
                return;
            }
            statusLabel.Text = "Exported to " + file;
            if (format == 1) try { Process.Start(file); } catch (Exception) { }
        }

        static string Csv(string s)
        {
            if (s == null) return "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r', ';' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        static string ListCsv(SizeListView lv)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", lv.Titles.Select(Csv)));
            foreach (ListViewItem it in lv.Items)
                sb.AppendLine(string.Join(",", Enumerable.Range(0, lv.Titles.Length).Select(i => Csv(i < it.SubItems.Count ? it.SubItems[i].Text : ""))));
            return sb.ToString();
        }

        static void FoldersCsv(DirNode scope, string file)
        {
            using (var w = new StreamWriter(file, false, new UTF8Encoding(true)))
            {
                w.WriteLine("Path,Size (bytes),Size on disk (bytes),Files,Folders,Modified");
                foreach (var d in scope.Descendants())
                    w.WriteLine(string.Join(",", Csv(d.FullPath), d.Bytes.ToString(CultureInfo.InvariantCulture), d.Alloc.ToString(CultureInfo.InvariantCulture),
                        d.Files.ToString(CultureInfo.InvariantCulture), d.Dirs.ToString(CultureInfo.InvariantCulture),
                        d.Modified > 0 ? DateTime.FromFileTime(d.Modified).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : ""));
            }
        }

        static string Hex(Color c) { return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }

        static string H(string s) { return WebUtility.HtmlEncode(s ?? ""); }

        static string HtmlReport(DirNode scope, string version, string driveLine)
        {
            var sb = new StringBuilder();
            string measure = DirNode.OnDisk ? "size on disk" : "file size";
            sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Disk usage - ").Append(H(scope.FullPath)).Append("</title><style>");
            sb.AppendFormat("body{{background:{0};color:{1};font:14px 'Segoe UI',system-ui,sans-serif;margin:32px auto;max-width:1100px;padding:0 24px}}", Hex(Theme.BG), Hex(Theme.FG));
            sb.AppendFormat("h1{{font-size:22px;margin:0 0 4px}}h2{{font-size:16px;margin:32px 0 10px}}.muted{{color:{0}}}", Hex(Theme.MUTED));
            sb.AppendFormat(".card{{background:{0};border:1px solid {1};border-radius:12px;padding:16px 20px;margin-top:16px}}", Hex(Theme.SURFACE), Hex(Theme.BORDER));
            sb.Append("table{width:100%;border-collapse:collapse}td,th{padding:6px 8px;text-align:left;white-space:nowrap}td.p{white-space:normal;word-break:break-all}");
            sb.AppendFormat("th{{color:{0};font-weight:normal;border-bottom:1px solid {1}}}td.n,th.n{{text-align:right}}", Hex(Theme.MUTED), Hex(Theme.BORDER));
            sb.AppendFormat(".bar{{background:{0};border-radius:4px;height:8px;width:160px}}.bar i{{display:block;background:{1};border-radius:4px;height:8px}}", Hex(Theme.BORDER), Hex(Theme.ACCENT));
            sb.AppendFormat(".top{{display:flex;gap:32px;align-items:center}}a{{color:{0}}}.sw{{display:inline-block;width:10px;height:10px;border-radius:3px;margin-right:8px}}", Hex(Theme.ACCENT));
            sb.Append("</style></head><body>");
            sb.Append("<h1>").Append(H(scope.FullPath)).Append("</h1>");
            sb.Append("<div class=\"muted\">Disk usage report, ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append(" &middot; sizes are ").Append(measure).Append("</div>");

            // Summary and donut of the folder's contents.
            var parts = new List<KeyValuePair<string, long>>();
            if (scope.Children != null) foreach (var c in scope.Children.Take(10)) if (c.Size > 0) parts.Add(new KeyValuePair<string, long>(c.Name, c.Size));
            long rest = scope.Size - parts.Sum(p => p.Value);
            if (rest > 0) parts.Add(new KeyValuePair<string, long>("Other folders and files", rest));
            sb.Append("<div class=\"card top\">").Append(DonutSvg(parts)).Append("<div>");
            sb.Append("<div style=\"font-size:26px\">").Append(Fmt.Size(scope.Size)).Append("</div>");
            sb.AppendFormat("<div class=\"muted\">{0:N0} files in {1:N0} folders &middot; file size {2}, size on disk {3}</div>", scope.Files, scope.Dirs, Fmt.Size(scope.Bytes), Fmt.Size(scope.Alloc));
            if (driveLine.Length > 0) sb.Append("<div class=\"muted\">Drive: ").Append(H(driveLine)).Append("</div>");
            sb.Append("<table style=\"margin-top:12px\">");
            for (int i = 0; i < parts.Count; i++)
                sb.AppendFormat("<tr><td><span class=\"sw\" style=\"background:{0}\"></span>{1}</td><td class=\"n\">{2}</td><td class=\"n muted\">{3:0.0} %</td></tr>",
                    i < parts.Count - 1 || rest <= 0 ? Hex(Theme.CHART[i % Theme.CHART.Length]) : Hex(Theme.CHART_OTHER), H(parts[i].Key), Fmt.Size(parts[i].Value),
                    100.0 * parts[i].Value / Math.Max(1, scope.Size));
            sb.Append("</table></div></div>");

            // Largest folders (two levels).
            sb.Append("<h2>Largest folders</h2><div class=\"card\"><table><tr><th>Folder</th><th class=\"n\">Size</th><th>% of ").Append(H(scope.Name.TrimEnd('\\'))).Append("</th><th class=\"n\">Files</th></tr>");
            var folders = new List<DirNode>();
            if (scope.Children != null)
                foreach (var c in scope.Children.Take(25))
                {
                    folders.Add(c);
                    if (c.Children != null) folders.AddRange(c.Children.Take(3).Where(g => g.Size >= c.Size / 10));
                }
            foreach (var d in folders)
            {
                double pct = 100.0 * d.Size / Math.Max(1, scope.Size);
                string rel = d.Parent == scope ? H(d.Name) : "<span class=\"muted\">" + H(d.Parent.Name) + "\\</span>" + H(d.Name);
                sb.AppendFormat("<tr><td class=\"p\" style=\"padding-left:{0}px\">{1}</td><td class=\"n\">{2}</td><td><div class=\"bar\"><i style=\"width:{3:0.#}%\"></i></div></td><td class=\"n muted\">{4:N0}</td></tr>",
                    d.Parent == scope ? 8 : 28, rel, Fmt.Size(d.Size), Math.Min(100, pct).ToString("0.#", CultureInfo.InvariantCulture), d.Files);
            }
            sb.Append("</table></div>");

            // Largest files.
            long n, total;
            var files = FileIndex.Largest(scope, 50, null, out n, out total);
            sb.Append("<h2>Largest files</h2><div class=\"card\"><table><tr><th>File</th><th class=\"n\">Size</th><th>Modified</th></tr>");
            foreach (var f in files)
                sb.AppendFormat("<tr><td class=\"p\">{0}</td><td class=\"n\">{1}</td><td class=\"muted\">{2}</td></tr>", H(f.FullPath), Fmt.Size(f.Size), Fmt.Date(f.Modified));
            sb.Append("</table></div>");

            // File types by category.
            var cats = new Dictionary<string, long[]>();
            foreach (var d in scope.Descendants())
            {
                if (d.FileList == null) continue;
                for (int i = 0; i < d.FileList.Length; i++)
                {
                    string c = FileTypes.CategoryOf(FileIndex.Extension(d.FileList[i].Name));
                    long[] v;
                    if (!cats.TryGetValue(c, out v)) cats[c] = v = new long[2];
                    v[0] += FileIndex.Measure(ref d.FileList[i]);
                    v[1]++;
                }
            }
            sb.Append("<h2>File types</h2><div class=\"card\"><table><tr><th>Category</th><th class=\"n\">Size</th><th>Share</th><th class=\"n\">Files</th></tr>");
            foreach (var kv in cats.OrderByDescending(k => k.Value[0]))
            {
                double pct = 100.0 * kv.Value[0] / Math.Max(1, scope.Size);
                sb.AppendFormat("<tr><td>{0}</td><td class=\"n\">{1}</td><td><div class=\"bar\"><i style=\"width:{2}%\"></i></div></td><td class=\"n muted\">{3:N0}</td></tr>",
                    H(kv.Key), Fmt.Size(kv.Value[0]), Math.Min(100, pct).ToString("0.#", CultureInfo.InvariantCulture), kv.Value[1]);
            }
            sb.Append("</table></div>");
            sb.Append("<p class=\"muted\" style=\"margin-top:32px\">Made with <a href=\"https://github.com/sphrd/DiskSpaceAnalyzer\">Disk Space Analyzer</a> ").Append(H(version)).Append(".</p>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        static string DonutSvg(List<KeyValuePair<string, long>> parts)
        {
            const double R = 90, r = 52, C = 100;
            double total = Math.Max(1, parts.Sum(p => (double)p.Value));
            var sb = new StringBuilder("<svg width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">");
            double a = -Math.PI / 2;
            for (int i = 0; i < parts.Count; i++)
            {
                double sweep = 2 * Math.PI * parts[i].Value / total;
                if (sweep <= 0) continue;
                if (sweep >= 2 * Math.PI - 1e-6) sweep = 2 * Math.PI - 1e-4;
                double b = a + sweep;
                int large = sweep > Math.PI ? 1 : 0;
                Func<double, double, string> P = (rad, ang) => (C + rad * Math.Cos(ang)).ToString("0.##", CultureInfo.InvariantCulture) + " " + (C + rad * Math.Sin(ang)).ToString("0.##", CultureInfo.InvariantCulture);
                string color = i < parts.Count - 1 || parts[i].Key != "Other folders and files" ? Hex(Theme.CHART[i % Theme.CHART.Length]) : Hex(Theme.CHART_OTHER);
                sb.AppendFormat("<path fill=\"{0}\" stroke=\"{1}\" stroke-width=\"1.5\" d=\"M {2} A {3} {3} 0 {4} 1 {5} L {6} A {7} {7} 0 {4} 0 {8} Z\"><title>{9}</title></path>",
                    color, Hex(Theme.SURFACE), P(R, a), R.ToString(CultureInfo.InvariantCulture), large, P(R, b), P(r, b), r.ToString(CultureInfo.InvariantCulture), P(r, a), H(parts[i].Key));
                a = b;
            }
            return sb.Append("</svg>").ToString();
        }
    }
}
