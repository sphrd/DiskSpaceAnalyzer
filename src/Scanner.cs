using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DiskSpaceAnalyzer
{
    /// <summary>
    /// Parallel directory scanner. Worker threads pull directories from a shared LIFO queue and enumerate
    /// them with FindFirstFileEx (basic info + large fetch); a directory's totals are aggregated as soon as
    /// its last child finishes. Junctions and symbolic links are not followed. Every file goes into the index
    /// with its size on disk (rounded to clusters; compressed, sparse and system-compressed files are asked
    /// for their real allocation; online-only cloud files take no space).
    /// </summary>
    internal sealed unsafe class Scanner
    {
        public long FilesSeen, DirsSeen, BytesSeen;
        public volatile string Current = "";
        public string RootPath;
        /// <summary>How the last scan was done, for the status line.</summary>
        public string Method = "folder by folder";

        readonly BlockingCollection<DirNode> queue = new BlockingCollection<DirNode>(new ConcurrentStack<DirNode>());
        volatile bool cancel, finished;
        long cluster = 4096;

        public bool Cancelled { get { return cancel; } }

        public void Cancel() { cancel = true; }

        /// <summary>Scans path. allowMft: read the NTFS master file table when possible (drive root, admin rights).</summary>
        public DirNode Run(string path, bool allowMft)
        {
            RootPath = path;
            if (allowMft)
            {
                var mft = MftScanner.TryScan(path, this);
                if (mft != null) { Method = "read from the master file table"; return mft; }
                if (cancel) return new DirNode { Name = path };
            }
            return Run(path);
        }

        public DirNode Run(string path)
        {
            RootPath = path;
            cluster = Native.ClusterSize(path);
            var root = new DirNode { Name = path };
            queue.Add(root);
            int n = Math.Min(32, Math.Max(8, 2 * Environment.ProcessorCount));
            var threads = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                threads[i] = new Thread(Worker) { IsBackground = true, Name = "scan-" + i };
                threads[i].Start();
            }
            foreach (var t in threads) t.Join();
            if (cancel) AggregateAll(root);
            return root;
        }

        void Worker()
        {
            DirNode node;
            while (!finished && !cancel)
                if (queue.TryTake(out node, 50)) Process(node);
        }

        void Process(DirNode node)
        {
            string path = node.FullPath;
            Current = path;
            List<DirNode> subs = null;
            FileRec[] files = ReadFolder(path, node, ref subs, cluster, ref cancel);

            node.Children = subs;
            if (files != null)
            {
                node.FileList = files;
                long b = 0, a = 0;
                foreach (var f in files) { b += f.Bytes; a += f.Alloc; }
                node.LocalBytes = b; node.LocalAlloc = a; node.LocalFiles = files.Length;
                Interlocked.Add(ref BytesSeen, b);
                Interlocked.Add(ref FilesSeen, files.Length);
            }
            Interlocked.Increment(ref DirsSeen);

            if (subs != null && !cancel)
            {
                node.Pending = subs.Count;
                foreach (var s in subs) queue.Add(s);
            }
            else Complete(node);
        }

        /// <summary>
        /// Lists one folder: its files (returned) and, when subs is not null-guarded, its subfolders as new nodes.
        /// Returns null and marks the node denied when the folder cannot be read.
        /// </summary>
        internal static FileRec[] ReadFolder(string path, DirNode node, ref List<DirNode> subs, long cluster, ref bool cancel)
        {
            var files = new List<FileRec>();
            try
            {
                FindData fd;
                FindData* d = &fd;
                IntPtr h = Native.FindFirst(path, d);
                if (h == Native.InvalidHandle)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 2 && err != 18) { node.Denied = true; return null; }
                    return new FileRec[0];
                }
                try
                {
                    do
                    {
                        char* name = d->FileName;
                        uint attr = d->Attributes;
                        if ((attr & Native.AttrDirectory) != 0)
                        {
                            if (Native.IsDots(name) || (attr & Native.AttrReparsePoint) != 0) continue;
                            if (subs == null) subs = new List<DirNode>();
                            subs.Add(new DirNode { Name = new string(name), Parent = node, Modified = Native.WriteTime(d) });
                        }
                        else
                        {
                            long s = Native.Size(d);
                            var f = new FileRec { Name = new string(name), Bytes = s, Modified = Native.WriteTime(d), Attr = attr };
                            f.Alloc = Native.Allocated(s, attr, cluster, path, f.Name);
                            files.Add(f);
                        }
                    } while (!cancel && Native.FindNextFileW(h, d));
                }
                finally { Native.FindClose(h); }
            }
            catch (Exception) { node.Denied = true; return null; }
            return files.ToArray();
        }

        void Complete(DirNode node)
        {
            while (true)
            {
                node.Aggregate();
                var p = node.Parent;
                if (p == null) { finished = true; return; }
                if (Interlocked.Decrement(ref p.Pending) != 0) return;
                node = p;
            }
        }

        internal static void AggregateAll(DirNode root)
        {
            var order = new List<DirNode>(root.Descendants());
            for (int i = order.Count - 1; i >= 0; i--) order[i].Aggregate();
        }

        /// <summary>Lists the files (not folders) directly inside a directory, from the index when there is one.</summary>
        public static List<FileEntry> ListFiles(DirNode dir)
        {
            var list = new List<FileEntry>();
            if (dir.FileList != null)
            {
                for (int i = 0; i < dir.FileList.Length; i++) list.Add(FileEntry.From(dir, ref dir.FileList[i]));
                return list;
            }
            return list;
        }

        /// <summary>Reads a folder's own files again (not its subfolders) and updates the index and totals.</summary>
        public static bool RefreshFiles(DirNode dir)
        {
            List<DirNode> ignore = null;
            bool cancel = false;
            string path = dir.FullPath;
            var files = ReadFolder(path, new DirNode(), ref ignore, Native.ClusterSize(path), ref cancel);
            if (files == null) return false;
            dir.SetFiles(files);
            return true;
        }
    }
}
