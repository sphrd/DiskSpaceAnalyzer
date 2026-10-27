using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DiskSpaceAnalyzer
{
    /// <summary>A scanned directory. Only directories are kept in memory; files are listed on demand.</summary>
    internal sealed class DirNode
    {
        public string Name;
        public DirNode Parent;
        public List<DirNode> Children;
        public long Size, Files, Dirs;
        public long LocalSize;
        public int LocalFiles;
        public bool Denied;
        internal int Pending;

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
            long size = LocalSize, files = LocalFiles, dirs = 0;
            if (Children != null)
            {
                foreach (var c in Children) { size += c.Size; files += c.Files; dirs += c.Dirs + 1; }
                Children.Sort(BySizeDesc);
            }
            Size = size; Files = files; Dirs = dirs;
        }
    }

    internal sealed class FileEntry
    {
        public string Name;
        public DirNode Dir;
        public long Size;
        public long Modified;

        public string FullPath { get { return Native.Join(Dir.FullPath, Name); } }

        public static readonly Comparison<FileEntry> BySizeDesc = (a, b) => b.Size.CompareTo(a.Size);
    }

    /// <summary>Thread-safe bounded min-heap that keeps the N largest files seen.</summary>
    internal sealed class TopFiles
    {
        readonly FileEntry[] heap;
        readonly object gate = new object();
        int count;
        long threshold;

        public TopFiles(int capacity) { heap = new FileEntry[Math.Max(1, capacity)]; }

        public long Threshold { get { return Volatile.Read(ref threshold); } }

        public void Offer(FileEntry e)
        {
            lock (gate)
            {
                if (count < heap.Length)
                {
                    heap[count] = e;
                    SiftUp(count++);
                    if (count == heap.Length) Volatile.Write(ref threshold, heap[0].Size);
                }
                else if (e.Size > heap[0].Size)
                {
                    heap[0] = e;
                    SiftDown(0);
                    Volatile.Write(ref threshold, heap[0].Size);
                }
            }
        }

        public List<FileEntry> ToSortedList()
        {
            lock (gate)
            {
                var list = new List<FileEntry>(count);
                for (int i = 0; i < count; i++) list.Add(heap[i]);
                list.Sort(FileEntry.BySizeDesc);
                return list;
            }
        }

        void SiftUp(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (heap[p].Size <= heap[i].Size) break;
                var t = heap[p]; heap[p] = heap[i]; heap[i] = t; i = p;
            }
        }

        void SiftDown(int i)
        {
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < count && heap[l].Size < heap[m].Size) m = l;
                if (r < count && heap[r].Size < heap[m].Size) m = r;
                if (m == i) break;
                var t = heap[m]; heap[m] = heap[i]; heap[i] = t; i = m;
            }
        }
    }

    /// <summary>
    /// Parallel directory scanner. Worker threads pull directories from a shared LIFO queue and enumerate
    /// them with FindFirstFileEx (basic info + large fetch); a directory's totals are aggregated as soon as
    /// its last child finishes. Junctions and symbolic links are not followed.
    /// </summary>
    internal sealed unsafe class Scanner
    {
        public long FilesSeen, DirsSeen, BytesSeen;
        public volatile string Current = "";

        readonly BlockingCollection<DirNode> queue = new BlockingCollection<DirNode>(new ConcurrentStack<DirNode>());
        readonly TopFiles top;
        volatile bool cancel, finished;

        public Scanner(int topCount) { top = new TopFiles(topCount); }

        public bool Cancelled { get { return cancel; } }
        public List<FileEntry> TopFiles { get; private set; }

        public void Cancel() { cancel = true; }

        public DirNode Run(string path)
        {
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
            TopFiles = top.ToSortedList();
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
            long size = 0;
            int files = 0;
            try
            {
                FindData fd;
                FindData* d = &fd;
                IntPtr h = Native.FindFirst(path, d);
                if (h == Native.InvalidHandle)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 2 && err != 18) node.Denied = true;
                }
                else
                {
                    try
                    {
                        long threshold = top.Threshold;
                        do
                        {
                            char* name = d->FileName;
                            if ((d->Attributes & Native.AttrDirectory) != 0)
                            {
                                if (Native.IsDots(name) || (d->Attributes & Native.AttrReparsePoint) != 0) continue;
                                if (subs == null) subs = new List<DirNode>();
                                subs.Add(new DirNode { Name = new string(name), Parent = node });
                            }
                            else
                            {
                                long s = Native.Size(d);
                                size += s;
                                files++;
                                if (s > threshold)
                                {
                                    top.Offer(new FileEntry { Name = new string(name), Dir = node, Size = s, Modified = Native.WriteTime(d) });
                                    threshold = top.Threshold;
                                }
                            }
                        } while (!cancel && Native.FindNextFileW(h, d));
                    }
                    finally { Native.FindClose(h); }
                }
            }
            catch (Exception) { node.Denied = true; }

            node.LocalSize = size;
            node.LocalFiles = files;
            node.Children = subs;
            Interlocked.Add(ref BytesSeen, size);
            Interlocked.Add(ref FilesSeen, files);
            Interlocked.Increment(ref DirsSeen);

            if (subs != null && !cancel)
            {
                node.Pending = subs.Count;
                foreach (var s in subs) queue.Add(s);
            }
            else Complete(node);
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

        static void AggregateAll(DirNode root)
        {
            var order = new List<DirNode>();
            var stack = new Stack<DirNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                order.Add(n);
                if (n.Children != null) foreach (var c in n.Children) stack.Push(c);
            }
            for (int i = order.Count - 1; i >= 0; i--) order[i].Aggregate();
        }

        /// <summary>Lists the files (not folders) directly inside a directory.</summary>
        public static List<FileEntry> ListFiles(DirNode dir)
        {
            var list = new List<FileEntry>();
            FindData fd;
            FindData* d = &fd;
            IntPtr h = Native.FindFirst(dir.FullPath, d);
            if (h == Native.InvalidHandle) return list;
            try
            {
                do
                {
                    if ((d->Attributes & Native.AttrDirectory) != 0) continue;
                    list.Add(new FileEntry { Name = new string(d->FileName), Dir = dir, Size = Native.Size(d), Modified = Native.WriteTime(d) });
                } while (Native.FindNextFileW(h, d));
            }
            finally { Native.FindClose(h); }
            return list;
        }
    }
}
