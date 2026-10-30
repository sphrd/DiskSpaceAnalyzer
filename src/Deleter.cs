using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace DiskSpaceAnalyzer
{
    /// <summary>Something to delete: a file, a folder, only the contents of a folder, or the Recycle Bin itself.</summary>
    internal sealed class DeleteTarget
    {
        public string Path = "", Label = "", Note = "";
        public bool IsDir, ContentsOnly, RecycleBin;
        /// <summary>Ticked in a selection tree (suggestions).</summary>
        public bool Checked;
        public long Size;
        public DirNode Dir;        // the scanned folder, when known
        public FileEntry File;     // the scanned file, when known

        public static List<DeleteTarget> From(ItemInfo it)
        {
            if (it.Targets != null) return it.Targets;
            var list = new List<DeleteTarget>();
            if (it.IsDir) list.Add(new DeleteTarget { Path = it.Dir.FullPath, Label = it.Dir.Name, IsDir = true, Dir = it.Dir, Size = it.Dir.Size });
            else if (it.File != null) list.Add(new DeleteTarget { Path = it.File.FullPath, Label = it.File.Name, File = it.File, Size = it.File.Size });
            return list;
        }
    }

    /// <summary>
    /// Deletion, permanent or to the Recycle Bin. Read-only attributes are cleared, long paths are supported and
    /// junctions/symbolic links are removed as links: their targets are never touched. When only the contents of a
    /// folder are deleted (temporary files, caches), files in use are skipped quietly and counted.
    /// </summary>
    internal sealed unsafe class DeleteJob
    {
        public volatile string Current = "";
        public long FilesDeleted, Recycled, Skipped, BytesDeleted;
        public readonly List<string> Errors = new List<string>();
        /// <summary>Every file and folder that is gone, as it happens (the window removes their rows live).</summary>
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Gone = new System.Collections.Concurrent.ConcurrentQueue<string>();
        /// <summary>Targets that are finished (whatever could be deleted is gone).</summary>
        public readonly System.Collections.Concurrent.ConcurrentQueue<DeleteTarget> Finished = new System.Collections.Concurrent.ConcurrentQueue<DeleteTarget>();
        /// <summary>Stop: the current file is finished, nothing more is deleted.</summary>
        public volatile bool Stop;
        bool quiet;

        public void Run(List<DeleteTarget> targets, bool recycle)
        {
            foreach (var t in targets)
            {
                if (Stop) return;
                Current = t.RecycleBin ? "Recycle Bin" : t.Path;
                if (t.RecycleBin) { EmptyRecycleBin(); continue; }
                quiet = t.ContentsOnly;
                if (recycle) Recycle(t.ContentsOnly ? Children(t.Path) : new List<string> { t.Path });
                else if (t.ContentsOnly) DeleteContents(t.Path);
                else if (t.IsDir) DeleteDirectory(t.Path);
                else DeleteFile(t.Path);
                quiet = false;
                if (!Stop) Finished.Enqueue(t);
            }
        }

        static List<string> Children(string dir)
        {
            var list = new List<string>();
            FindData fd;
            FindData* d = &fd;
            IntPtr h = Native.FindFirst(dir, d);
            if (h == Native.InvalidHandle) return list;
            try
            {
                do { if (!Native.IsDots(d->FileName)) list.Add(Native.Join(dir, new string(d->FileName))); }
                while (Native.FindNextFileW(h, d));
            }
            finally { Native.FindClose(h); }
            return list;
        }

        /// <summary>Deletes everything inside a folder and keeps the folder.</summary>
        public void DeleteContents(string dir)
        {
            FindData fd;
            FindData* d = &fd;
            var subdirs = new List<string>();
            IntPtr h = Native.FindFirst(dir, d);
            if (h == Native.InvalidHandle) return;
            try
            {
                do
                {
                    if (Stop) break;
                    if (Native.IsDots(d->FileName)) continue;
                    string full = Native.Join(dir, new string(d->FileName));
                    uint a = d->Attributes;
                    if ((a & Native.AttrDirectory) == 0) DeleteOneFile(full, a, ((long)d->SizeHigh << 32) | d->SizeLow);
                    else if ((a & Native.AttrReparsePoint) != 0) RemoveDir(full);
                    else subdirs.Add(full);
                } while (Native.FindNextFileW(h, d));
            }
            finally { Native.FindClose(h); }
            foreach (var s in subdirs) { if (Stop) return; DeleteTree(s); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom, pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHEmptyRecycleBinW(IntPtr hwnd, string root, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHQueryRecycleBinW(string root, ref SHQUERYRBINFO info);

        /// <summary>Size and item count of the Recycle Bin on all drives (0, 0 when it cannot be read).</summary>
        public static long RecycleBinSize(out long items)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
            items = 0;
            if (SHQueryRecycleBinW(null, ref info) != 0) return 0;
            items = info.i64NumItems;
            return info.i64Size;
        }

        void EmptyRecycleBin()
        {
            long items;
            RecycleBinSize(out items);
            int hr = SHEmptyRecycleBinW(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);   // no confirmation, no progress, no sound
            if (hr == 0) FilesDeleted += items;
            else if (items > 0) lock (Errors) Errors.Add("Recycle Bin  -  could not be emptied (0x" + hr.ToString("X8") + ")");
        }

        /// <summary>Moves items to the Recycle Bin (silently; Windows still warns before deleting an item too big for the bin).</summary>
        void Recycle(List<string> paths)
        {
            const uint FO_DELETE = 3;
            const ushort Flags = 0x4 | 0x10 | 0x40 | 0x400 | 0x4000;   // SILENT, NOCONFIRMATION, ALLOWUNDO, NOERRORUI, WANTNUKEWARNING
            for (int i = 0; i < paths.Count && !Stop; i += 100)
            {
                var batch = paths.GetRange(i, Math.Min(100, paths.Count - i));
                Current = batch[0];
                var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = string.Join("\0", batch) + "\0", fFlags = Flags };
                SHFileOperationW(ref op);
                foreach (var p in batch)
                {
                    if (!Native.Exists(p)) { Interlocked.Increment(ref Recycled); Gone.Enqueue(p); }
                    else if (quiet) Interlocked.Increment(ref Skipped);
                    else lock (Errors) Errors.Add(p + "  -  could not be moved to the Recycle Bin");
                }
            }
        }

        public void DeleteFile(string path)
        {
            uint attrs = Native.GetAttributes(path);
            if (attrs == Native.InvalidAttributes) { Error(path, Marshal.GetLastWin32Error()); return; }
            DeleteOneFile(path, attrs, SizeOf(path));
        }

        static long SizeOf(string path)
        {
            try { return new System.IO.FileInfo(Native.LongPath(path)).Length; } catch (Exception) { }
            try { return new System.IO.FileInfo(path).Length; } catch (Exception) { return 0; }
        }

        public void DeleteDirectory(string path)
        {
            uint attrs = Native.GetAttributes(path);
            if (attrs == Native.InvalidAttributes) { Error(path, Marshal.GetLastWin32Error()); return; }
            if ((attrs & Native.AttrReparsePoint) != 0) RemoveDir(path);
            else DeleteTree(path);
        }

        void DeleteTree(string dir)
        {
            Current = dir;
            var subdirs = new List<string>();
            FindData fd;
            FindData* d = &fd;
            IntPtr h = Native.FindFirst(dir, d);
            if (h != Native.InvalidHandle)
            {
                try
                {
                    do
                    {
                        if (Stop) break;
                        if (Native.IsDots(d->FileName)) continue;
                        string full = Native.Join(dir, new string(d->FileName));
                        uint a = d->Attributes;
                        if ((a & Native.AttrDirectory) == 0) DeleteOneFile(full, a, ((long)d->SizeHigh << 32) | d->SizeLow);
                        else if ((a & Native.AttrReparsePoint) != 0) RemoveDir(full);
                        else subdirs.Add(full);
                    } while (Native.FindNextFileW(h, d));
                }
                finally { Native.FindClose(h); }
            }
            foreach (var s in subdirs) { if (Stop) return; DeleteTree(s); }
            if (!Stop) RemoveDir(dir);
        }

        void DeleteOneFile(string path, uint attrs, long size)
        {
            if ((attrs & Native.AttrReadOnly) != 0) Native.SetAttributes(path, Native.AttrNormal);
            if (Native.DeleteFile(path)) { Interlocked.Increment(ref FilesDeleted); Interlocked.Add(ref BytesDeleted, Math.Max(0, size)); Gone.Enqueue(path); }
            else Error(path, Marshal.GetLastWin32Error());
        }

        void RemoveDir(string path)
        {
            if (Native.RemoveDirectory(path)) { Gone.Enqueue(path); return; }
            int err = Marshal.GetLastWin32Error();
            if (err == 5)
            {
                Native.SetAttributes(path, Native.AttrNormal);
                if (Native.RemoveDirectory(path)) { Gone.Enqueue(path); return; }
                err = Marshal.GetLastWin32Error();
            }
            Error(path, err);
        }

        void Error(string path, int code)
        {
            if (quiet) { Interlocked.Increment(ref Skipped); return; }
            lock (Errors) Errors.Add(path + "  -  " + new Win32Exception(code).Message);
        }
    }
}
