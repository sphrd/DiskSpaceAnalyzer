using System;
using System.Collections.Generic;
using System.Text;

namespace DiskSpaceAnalyzer
{
    /// <summary>One file in the in-memory index (kept per folder, so searches never touch the disk).</summary>
    internal struct FileRec
    {
        public string Name;
        public long Bytes;      // file size
        public long Alloc;      // size on disk (clusters actually used; 0 for online-only cloud files)
        public long Modified;   // last write, FILETIME
        public uint Attr;

        public bool OnlineOnly { get { return (Attr & Native.AttrCloudMask) != 0; } }
    }

    /// <summary>A scanned folder with its totals and the index of its own files.</summary>
    internal sealed class DirNode
    {
        /// <summary>Which number every view uses: false = file size, true = size on disk.</summary>
        public static bool OnDisk;

        public string Name;
        public DirNode Parent;
        public List<DirNode> Children;
        public long Bytes, Alloc, Files, Dirs;
        public long LocalBytes, LocalAlloc;
        public int LocalFiles;
        public FileRec[] FileList;     // null when the folder could not be read
        public long Modified;
        public bool Denied;
        internal int Pending;

        public long Size { get { return OnDisk ? Alloc : Bytes; } }
        public long LocalSize { get { return OnDisk ? LocalAlloc : LocalBytes; } }

        public string FullPath
        {
            get
            {
                if (Parent == null) return Name;
                var parts = new List<string>();
                var n = this;
                while (n.Parent != null) { parts.Add(n.Name); n = n.Parent; }
                var sb = new StringBuilder(n.Name.TrimEnd('\\'));
                for (int i = parts.Count - 1; i >= 0; i--) sb.Append('\\').Append(parts[i]);
                return sb.ToString();
            }
        }

        public bool IsUnder(DirNode ancestor)
        {
            for (var n = this; n != null; n = n.Parent)
                if (n == ancestor) return true;
            return false;
        }

        public DirNode Top
        {
            get { var n = this; while (n.Parent != null) n = n.Parent; return n; }
        }

        public static readonly Comparison<DirNode> BySizeDesc = (a, b) => b.Size.CompareTo(a.Size);

        /// <summary>Recomputes this node's totals from its own files and its (already final) children.</summary>
        public void Aggregate()
        {
            long bytes = LocalBytes, alloc = LocalAlloc, files = LocalFiles, dirs = 0;
            if (Children != null)
            {
                foreach (var c in Children) { bytes += c.Bytes; alloc += c.Alloc; files += c.Files; dirs += c.Dirs + 1; }
                Children.Sort(BySizeDesc);
            }
            Bytes = bytes; Alloc = alloc; Files = files; Dirs = dirs;
        }

        /// <summary>Adds a change to this folder and all its ancestors, and keeps sibling order by size.</summary>
        public void AddUp(long bytes, long alloc, long files, long dirs)
        {
            for (var a = this; a != null; a = a.Parent)
            {
                a.Bytes += bytes; a.Alloc += alloc; a.Files += files; a.Dirs += dirs;
                if (a.Children != null) a.Children.Sort(BySizeDesc);
            }
        }

        /// <summary>This folder and every folder below it (iterative; trees can be very deep).</summary>
        public IEnumerable<DirNode> Descendants()
        {
            var stack = new Stack<DirNode>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                if (n.Children != null) for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }

        /// <summary>Re-sorts every child list (after switching between file size and size on disk).</summary>
        public void ResortAll()
        {
            foreach (var n in Descendants()) if (n.Children != null) n.Children.Sort(BySizeDesc);
        }

        public DirNode Child(string name)
        {
            if (Children != null)
                foreach (var c in Children)
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        /// <summary>Removes one file of this folder from the index and the totals. Returns false if it is not indexed.</summary>
        public bool RemoveFileRec(string name)
        {
            if (FileList == null) return false;
            for (int i = 0; i < FileList.Length; i++)
            {
                if (!string.Equals(FileList[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                var f = FileList[i];
                var list = new FileRec[FileList.Length - 1];
                Array.Copy(FileList, 0, list, 0, i);
                Array.Copy(FileList, i + 1, list, i, FileList.Length - i - 1);
                FileList = list;
                LocalBytes -= f.Bytes; LocalAlloc -= f.Alloc; LocalFiles--;
                AddUp(-f.Bytes, -f.Alloc, -1, 0);
                return true;
            }
            return false;
        }

        /// <summary>Replaces this folder's own file list (children unchanged) and updates the totals up the tree.</summary>
        public void SetFiles(FileRec[] files)
        {
            long b = 0, a = 0;
            foreach (var f in files) { b += f.Bytes; a += f.Alloc; }
            long db = b - LocalBytes, da = a - LocalAlloc, df = files.Length - LocalFiles;
            FileList = files;
            LocalBytes = b; LocalAlloc = a; LocalFiles = files.Length;
            AddUp(db, da, df, 0);
        }
    }

    /// <summary>A file taken out of the index for display (a row in a list).</summary>
    internal sealed class FileEntry
    {
        public string Name;
        public DirNode Dir;
        public long Bytes, Alloc, Modified;
        public uint Attr;

        public long Size { get { return DirNode.OnDisk ? Alloc : Bytes; } }
        public bool OnlineOnly { get { return (Attr & Native.AttrCloudMask) != 0; } }
        public string FullPath { get { return Native.Join(Dir.FullPath, Name); } }

        public static FileEntry From(DirNode d, ref FileRec f)
        {
            return new FileEntry { Name = f.Name, Dir = d, Bytes = f.Bytes, Alloc = f.Alloc, Modified = f.Modified, Attr = f.Attr };
        }

        public static readonly Comparison<FileEntry> BySizeDesc = (a, b) => b.Size.CompareTo(a.Size);
    }

    /// <summary>Queries over the in-memory file index.</summary>
    internal static class FileIndex
    {
        public static long Measure(ref FileRec f) { return DirNode.OnDisk ? f.Alloc : f.Bytes; }

        /// <summary>The n largest files under root (by the current measure) that pass the filter, largest first.</summary>
        public static List<FileEntry> Largest(DirNode root, int n, FileFilter filter, out long matchCount, out long matchSize)
        {
            matchCount = 0; matchSize = 0;
            var heap = new List<KeyValuePair<long, FileEntry>>();
            long threshold = -1;
            foreach (var d in root.Descendants())
            {
                if (d.FileList == null) continue;
                if (filter != null && !filter.FolderOk(d)) continue;
                for (int i = 0; i < d.FileList.Length; i++)
                {
                    if (filter != null && !filter.Match(ref d.FileList[i])) continue;
                    long s = Measure(ref d.FileList[i]);
                    matchCount++; matchSize += s;
                    if (heap.Count >= n && s <= threshold) continue;
                    var e = FileEntry.From(d, ref d.FileList[i]);
                    HeapPush(heap, s, e, n);
                    threshold = heap.Count >= n ? heap[0].Key : -1;
                }
            }
            var result = new List<FileEntry>(heap.Count);
            foreach (var kv in heap) result.Add(kv.Value);
            result.Sort(FileEntry.BySizeDesc);
            return result;
        }

        // Min-heap on Key, capped at n entries.
        static void HeapPush(List<KeyValuePair<long, FileEntry>> h, long key, FileEntry v, int n)
        {
            if (h.Count < n)
            {
                h.Add(new KeyValuePair<long, FileEntry>(key, v));
                int i = h.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (h[p].Key <= h[i].Key) break;
                    var t = h[p]; h[p] = h[i]; h[i] = t; i = p;
                }
                return;
            }
            h[0] = new KeyValuePair<long, FileEntry>(key, v);
            int j = 0;
            while (true)
            {
                int l = 2 * j + 1, r = l + 1, m = j;
                if (l < h.Count && h[l].Key < h[m].Key) m = l;
                if (r < h.Count && h[r].Key < h[m].Key) m = r;
                if (m == j) break;
                var t = h[m]; h[m] = h[j]; h[j] = t; j = m;
            }
        }

        public static string Extension(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1 || name.Length - dot > 12) return "";
            return name.Substring(dot + 1).ToLowerInvariant();
        }
    }

    /// <summary>Filter for the Files view: name pattern, file type, minimum size, not modified for a while.</summary>
    internal sealed class FileFilter
    {
        public string[] Patterns;       // lower case; '*' and '?' wildcards, otherwise "contains"
        public string Category;         // null = any
        public long MinSize;
        public long ModifiedBefore;     // FILETIME; 0 = any

        public bool IsEmpty
        {
            get { return (Patterns == null || Patterns.Length == 0) && Category == null && MinSize <= 0 && ModifiedBefore <= 0; }
        }

        public bool FolderOk(DirNode d) { return true; }

        public bool Match(ref FileRec f)
        {
            if (MinSize > 0 && FileIndex.Measure(ref f) < MinSize) return false;
            if (ModifiedBefore > 0 && (f.Modified <= 0 || f.Modified >= ModifiedBefore)) return false;
            if (Category != null)
            {
                string ext = FileIndex.Extension(f.Name);
                if (Category != null && FileTypes.CategoryOf(ext) != Category) return false;
            }
            if (Patterns != null && Patterns.Length > 0)
            {
                bool any = false;
                foreach (var p in Patterns) if (NameMatches(f.Name, p)) { any = true; break; }
                if (!any) return false;
            }
            return true;
        }

        public static bool NameMatches(string name, string pattern)
        {
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                return name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            return Wildcard(name, 0, pattern, 0);
        }

        static bool Wildcard(string s, int si, string p, int pi)
        {
            int star = -1, mark = 0;
            while (si < s.Length)
            {
                if (pi < p.Length && (p[pi] == '?' || char.ToLowerInvariant(p[pi]) == char.ToLowerInvariant(s[si]))) { si++; pi++; }
                else if (pi < p.Length && p[pi] == '*') { star = pi++; mark = si; }
                else if (star >= 0) { pi = star + 1; si = ++mark; }
                else return false;
            }
            while (pi < p.Length && p[pi] == '*') pi++;
            return pi == p.Length;
        }
    }

    /// <summary>File categories by extension (for the Types view and the Files filter).</summary>
    internal static class FileTypes
    {
        public static readonly string[] Categories =
        {
            "Videos", "Images", "Audio", "Archives", "Disk images", "Installers", "Documents", "Code", "Programs and libraries", "Databases", "Other"
        };

        static readonly Dictionary<string, string> map = Build();

        static Dictionary<string, string> Build()
        {
            var m = new Dictionary<string, string>();
            Add(m, "Videos", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg m2ts mts ts 3gp vob");
            Add(m, "Images", "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd svg ico");
            Add(m, "Audio", "mp3 wav flac aac ogg wma m4a opus aiff");
            Add(m, "Archives", "zip rar 7z tar gz tgz bz2 xz zst cab lz4 lzma");
            Add(m, "Disk images", "iso img vhd vhdx vmdk vdi qcow2 dmg wim esd ova");
            Add(m, "Installers", "msi msix msixbundle appx appxbundle msp msu");
            Add(m, "Documents", "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md csv epub");
            Add(m, "Code", "c h cpp hpp cs java py js ts tsx jsx go rs rb php swift kt json xml yml yaml html css scss sql sh ps1");
            Add(m, "Programs and libraries", "exe dll sys so dylib jar pyd lib a o obj pdb node");
            Add(m, "Databases", "db sqlite sqlite3 mdb accdb mdf ldf ndf bak edb");
            return m;
        }

        static void Add(Dictionary<string, string> m, string cat, string exts)
        {
            foreach (var e in exts.Split(' ')) m[e] = cat;
        }

        public static string CategoryOf(string ext)
        {
            string c;
            return ext.Length > 0 && map.TryGetValue(ext, out c) ? c : "Other";
        }
    }
}
