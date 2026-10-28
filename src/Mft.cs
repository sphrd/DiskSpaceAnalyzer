using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DiskSpaceAnalyzer
{
    /// <summary>
    /// Reads a whole NTFS drive from its master file table (MFT) instead of walking folders: one sequential read of the
    /// table, then the tree is built in memory. Needs administrator rights (the volume is opened for reading); returns null
    /// whenever it cannot be used (not elevated, not NTFS, not a drive root, unusual MFT layout), so the caller falls back
    /// to the folder scanner. Sizes come straight from the file records: file size = unnamed data stream; size on disk =
    /// allocation of all data streams (so Compact OS / WOF-compressed and sparse files are exact). A file with several hard
    /// links is listed in every folder that links it, but its size on disk is counted only once.
    /// </summary>
    internal static unsafe class MftScanner
    {
        const uint GENERIC_READ = 0x80000000;
        const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
        const long RefMask = 0x0000FFFFFFFFFFFF;
        const int RootRecord = 5;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inSize, byte* outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFilePointerEx(IntPtr h, long distance, out long newPos, int method);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(IntPtr h, byte* buffer, int toRead, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        /// <summary>True when path is a drive root such as C:\ (the only case the MFT reader handles).</summary>
        public static bool IsDriveRoot(string path)
        {
            return path != null && path.Length == 3 && path[1] == ':' && path[2] == '\\' && char.IsLetter(path[0]);
        }

        public static DirNode TryScan(string path, Scanner progress)
        {
            if (!IsDriveRoot(path)) { progress.MftNote = "not a drive root"; return null; }
            IntPtr h = CreateFileW(@"\\.\" + path.Substring(0, 2), GENERIC_READ, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) { progress.MftNote = "cannot open the volume (error " + Marshal.GetLastWin32Error() + "; needs administrator rights)"; return null; }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var reader = new Reader(h, path, progress);
                var root = reader.Run();
                progress.MftNote = root != null ? string.Format("read {0:N0} records in {1:0.00} s, built the tree in {2:0.00} s", reader.RecordCount, reader.ReadSeconds, sw.Elapsed.TotalSeconds - reader.ReadSeconds)
                    : "not used: " + reader.Why;
                return root;
            }
            catch (Exception ex) { progress.MftNote = "failed: " + ex.Message; return null; }
            finally { CloseHandle(h); }
        }

        struct Link { public int Record, Parent; public string Name; public byte Ns; }

        sealed class Reader
        {
            readonly IntPtr h;
            readonly string rootPath;
            readonly Scanner progress;
            uint bytesPerCluster, recordSize;
            long recordCount;

            // Per record (indexed by record number)
            byte[] flags;            // 1 = in use, 2 = directory
            long[] bytes, alloc, modified;
            uint[] attrs;
            int[] parent;
            string[] name;
            byte[] nameNs;
            readonly List<Link> extraLinks = new List<Link>();
            public string Why = "";
            public double ReadSeconds;
            public long RecordCount { get { return recordCount; } }

            public Reader(IntPtr h, string rootPath, Scanner progress)
            {
                this.h = h;
                this.rootPath = rootPath;
                this.progress = progress;
            }

            void Read(long offset, byte* buffer, int size)
            {
                long pos;
                if (!SetFilePointerEx(h, offset, out pos, 0)) throw new InvalidOperationException("seek failed");
                int done = 0;
                while (done < size)
                {
                    int n;
                    if (!ReadFile(h, buffer + done, size - done, out n, IntPtr.Zero) || n == 0) throw new InvalidOperationException("read failed");
                    done += n;
                }
            }

            public DirNode Run()
            {
                byte* vd = stackalloc byte[256];
                int ret;
                if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, vd, 256, out ret, IntPtr.Zero)) { Why = "not NTFS"; return null; }
                bytesPerCluster = *(uint*)(vd + 44);
                recordSize = *(uint*)(vd + 48);
                long mftValid = *(long*)(vd + 56);
                long mftLcn = *(long*)(vd + 64);
                if (bytesPerCluster == 0 || recordSize < 512 || recordSize > 65536) { Why = "unexpected volume data"; return null; }

                // Record 0 describes the MFT itself: where its data runs are on the disk.
                var runs = new List<KeyValuePair<long, long>>();
                byte[] rec0 = new byte[Math.Max(recordSize, bytesPerCluster)];
                fixed (byte* p = rec0)
                {
                    Read(mftLcn * bytesPerCluster, p, rec0.Length);
                    if (*(uint*)p != 0x454C4946 || !Fixup(p, recordSize)) { Why = "record 0 unreadable"; return null; }
                    if (!MftRuns(p, runs)) { Why = "the table's own layout is spread over several records"; return null; }
                }

                recordCount = mftValid / recordSize;
                if (recordCount <= RootRecord || recordCount > int.MaxValue) { Why = "unexpected table size"; return null; }
                int n = (int)recordCount;
                flags = new byte[n];
                bytes = new long[n];
                alloc = new long[n];
                modified = new long[n];
                attrs = new uint[n];
                parent = new int[n];
                name = new string[n];
                nameNs = new byte[n];

                const int Chunk = 4 << 20;
                int chunk = (int)Math.Max(Chunk - Chunk % Math.Max(recordSize, bytesPerCluster), Math.Max(recordSize, bytesPerCluster));
                byte[] buffer = new byte[chunk];
                long rec = 0;
                fixed (byte* buf = buffer)
                {
                    foreach (var run in runs)
                    {
                        long runBytes = run.Value * bytesPerCluster;
                        for (long pos = 0; pos < runBytes && rec < recordCount; pos += chunk)
                        {
                            if (progress.Cancelled) return null;
                            int size = (int)Math.Min(chunk, runBytes - pos);
                            long records = Math.Min(size / recordSize, recordCount - rec);
                            if (run.Key < 0) { rec += records; continue; }   // sparse run: no records stored
                            Read(run.Key * bytesPerCluster + pos, buf, size);
                            for (long i = 0; i < records; i++, rec++) Parse(buf + i * recordSize, (int)rec);
                            progress.Current = string.Format("Reading the master file table of {0}  ({1:0} %)", rootPath, 100.0 * rec / recordCount);
                        }
                    }
                }
                if (progress.Cancelled) { Why = "cancelled"; return null; }
                ReadSeconds = progress.Elapsed;
                return Build();
            }

            /// <summary>Data runs of the MFT's own unnamed $DATA attribute. False if they do not all live in record 0.</summary>
            bool MftRuns(byte* p, List<KeyValuePair<long, long>> runs)
            {
                int off = *(ushort*)(p + 20);
                uint used = Math.Min(*(uint*)(p + 24), recordSize);
                while (off + 16 <= used)
                {
                    byte* a = p + off;
                    uint type = *(uint*)a, len = *(uint*)(a + 4);
                    if (type == 0xFFFFFFFF || len == 0 || off + len > used) break;
                    if (type == 0x80 && a[8] != 0 && a[9] == 0)
                    {
                        long startVcn = *(long*)(a + 16), lastVcn = *(long*)(a + 24), allocSize = *(long*)(a + 40);
                        if (startVcn != 0 || (lastVcn + 1) * bytesPerCluster < allocSize) return false;   // continued elsewhere
                        byte* r = a + *(ushort*)(a + 32);
                        byte* end = a + len;
                        long lcn = 0;
                        while (r < end && *r != 0)
                        {
                            int lenSize = *r & 0xF, offSize = *r >> 4;
                            r++;
                            if (lenSize == 0 || lenSize > 8 || offSize > 8 || r + lenSize + offSize > end) return false;
                            long count = 0;
                            for (int i = 0; i < lenSize; i++) count |= (long)r[i] << (8 * i);
                            r += lenSize;
                            if (offSize == 0) { runs.Add(new KeyValuePair<long, long>(-1, count)); continue; }
                            long delta = 0;
                            for (int i = 0; i < offSize; i++) delta |= (long)r[i] << (8 * i);
                            if ((r[offSize - 1] & 0x80) != 0 && offSize < 8) delta |= -1L << (8 * offSize);   // sign-extend
                            r += offSize;
                            lcn += delta;
                            runs.Add(new KeyValuePair<long, long>(lcn, count));
                        }
                        return runs.Count > 0;
                    }
                    off += (int)len;
                }
                return false;
            }

            /// <summary>Applies the update sequence array (the last two bytes of every 512-byte block are stored there).</summary>
            static bool Fixup(byte* p, uint size)
            {
                int usaOff = *(ushort*)(p + 4), usaCount = *(ushort*)(p + 6);
                if (usaCount == 0 || usaOff + usaCount * 2 > size) return false;
                ushort* usa = (ushort*)(p + usaOff);
                ushort usn = usa[0];
                for (int i = 1; i < usaCount; i++)
                {
                    long at = i * 512L - 2;
                    if (at + 2 > size) break;
                    ushort* end = (ushort*)(p + at);
                    if (*end != usn) return false;
                    *end = usa[i];
                }
                return true;
            }

            void Parse(byte* p, int recNo)
            {
                if (*(uint*)p != 0x454C4946) return;          // "FILE"
                if (!Fixup(p, recordSize)) return;
                ushort f = *(ushort*)(p + 22);
                if ((f & 1) == 0) return;                      // not in use
                long baseRef = *(long*)(p + 32) & RefMask;
                if (baseRef >= recordCount) return;
                int target = baseRef != 0 ? (int)baseRef : recNo;
                if (baseRef == 0)
                {
                    flags[target] |= (byte)(1 | ((f & 2) != 0 ? 2 : 0));
                    if ((f & 2) != 0) progress.DirsSeen++; else progress.FilesSeen++;
                }

                int off = *(ushort*)(p + 20);
                uint used = Math.Min(*(uint*)(p + 24), recordSize);
                while (off + 16 <= used)
                {
                    byte* a = p + off;
                    uint type = *(uint*)a, len = *(uint*)(a + 4);
                    if (type == 0xFFFFFFFF || len < 16 || off + len > used) break;
                    bool resident = a[8] == 0;
                    switch (type)
                    {
                        case 0x10:   // $STANDARD_INFORMATION
                            if (resident && *(uint*)(a + 16) >= 36)
                            {
                                byte* v = a + *(ushort*)(a + 20);
                                modified[target] = *(long*)(v + 8);
                                attrs[target] = *(uint*)(v + 32);
                            }
                            break;
                        case 0x30:   // $FILE_NAME
                            if (resident)
                            {
                                byte* v = a + *(ushort*)(a + 20);
                                int nl = v[64];
                                if (66 + nl * 2 > *(uint*)(a + 16)) break;
                                AddName(target, (int)Math.Min(*(long*)v & RefMask, int.MaxValue), new string((char*)(v + 66), 0, nl), v[65]);
                            }
                            break;
                        case 0x80:   // $DATA (unnamed = the file's content; named = alternate streams, e.g. WOF-compressed data)
                            bool unnamed = a[9] == 0;
                            if (resident)
                            {
                                if (unnamed) { bytes[target] = *(uint*)(a + 16); progress.BytesSeen += *(uint*)(a + 16); }
                            }
                            else if (*(long*)(a + 16) == 0)    // first segment holds the sizes
                            {
                                ushort af = *(ushort*)(a + 12);
                                long allocSize = *(long*)(a + 40), dataSize = *(long*)(a + 48);
                                long onDisk = (af & 0x8001) != 0 && len >= 72 ? *(long*)(a + 64) : allocSize;
                                // NTFS's own files (records 0-15) have special named streams, e.g. $BadClus:$Bad spans the
                                // whole volume; only their main stream is counted.
                                if (unnamed || target >= 16) alloc[target] += onDisk;
                                if (unnamed) { bytes[target] = dataSize; progress.BytesSeen += dataSize; }
                            }
                            break;
                    }
                    off += (int)len;
                }
            }

            void AddName(int rec, int par, string nm, byte ns)
            {
                if (name[rec] == null) { name[rec] = nm; parent[rec] = par; nameNs[rec] = ns; return; }
                if (parent[rec] == par)
                {
                    if (nameNs[rec] == 2 && ns != 2) { name[rec] = nm; nameNs[rec] = ns; }   // prefer the long name over the 8.3 one
                    return;
                }
                extraLinks.Add(new Link { Record = rec, Parent = par, Name = nm, Ns = ns });
            }

            DirNode Build()
            {
                int n = (int)recordCount;
                var dirs = new DirNode[n];
                for (int r = 0; r < n; r++)
                    if (flags[r] == 3 && name[r] != null)
                        dirs[r] = new DirNode { Name = name[r], Modified = modified[r] };
                var root = dirs[RootRecord];
                if (root == null) return null;
                root.Name = rootPath;
                for (int r = 0; r < n; r++)
                {
                    var d = dirs[r];
                    if (d == null || r == RootRecord) continue;
                    int p = parent[r];
                    if (p < 0 || p >= n || p == r || dirs[p] == null) continue;   // orphan: not reachable from the root
                    d.Parent = dirs[p];
                    if (dirs[p].Children == null) dirs[p].Children = new List<DirNode>();
                    dirs[p].Children.Add(d);
                }

                var lists = new List<FileRec>[n];
                for (int r = 0; r < n; r++)
                {
                    if (flags[r] != 1 || name[r] == null) continue;
                    AddFile(lists, dirs, parent[r], new FileRec { Name = name[r], Bytes = bytes[r], Alloc = alloc[r], Modified = modified[r], Attr = attrs[r] });
                }
                // Further hard links: same file in another folder; its space on disk is already counted once.
                // One entry per (file, folder), preferring the long name over the 8.3 one.
                var best = new Dictionary<long, Link>();
                foreach (var l in extraLinks)
                {
                    if (flags[l.Record] != 1 || l.Parent == parent[l.Record]) continue;
                    long key = ((long)l.Record << 32) | (uint)l.Parent;
                    Link cur;
                    if (!best.TryGetValue(key, out cur) || (cur.Ns == 2 && l.Ns != 2)) best[key] = l;
                }
                foreach (var l in best.Values)
                    AddFile(lists, dirs, l.Parent, new FileRec { Name = l.Name, Bytes = bytes[l.Record], Alloc = 0, Modified = modified[l.Record], Attr = attrs[l.Record] });

                for (int r = 0; r < n; r++)
                {
                    var d = dirs[r];
                    if (d == null) continue;
                    var files = lists[r] != null ? lists[r].ToArray() : new FileRec[0];
                    d.FileList = files;
                    long b = 0, a = 0;
                    foreach (var fr in files) { b += fr.Bytes; a += fr.Alloc; }
                    d.LocalBytes = b; d.LocalAlloc = a; d.LocalFiles = files.Length;
                }
                Scanner.AggregateAll(root);
                return root;
            }

            static void AddFile(List<FileRec>[] lists, DirNode[] dirs, int par, FileRec f)
            {
                if (par < 0 || par >= dirs.Length || dirs[par] == null) return;
                if (lists[par] == null) lists[par] = new List<FileRec>();
                lists[par].Add(f);
            }
        }
    }
}
