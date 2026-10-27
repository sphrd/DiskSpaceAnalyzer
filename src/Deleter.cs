using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace DiskSpaceAnalyzer
{
    /// <summary>
    /// Permanent deletion (no Recycle Bin). Read-only attributes are cleared, long paths are supported and
    /// junctions/symbolic links are removed as links: their targets are never touched.
    /// </summary>
    internal sealed unsafe class DeleteJob
    {
        public volatile string Current = "";
        public long FilesDeleted;
        public readonly List<string> Errors = new List<string>();

        public void DeleteFile(string path)
        {
            uint attrs = Native.GetAttributes(path);
            if (attrs == Native.InvalidAttributes) { Error(path, Marshal.GetLastWin32Error()); return; }
            DeleteOneFile(path, attrs);
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
                        if (Native.IsDots(d->FileName)) continue;
                        string full = Native.Join(dir, new string(d->FileName));
                        uint a = d->Attributes;
                        if ((a & Native.AttrDirectory) == 0) DeleteOneFile(full, a);
                        else if ((a & Native.AttrReparsePoint) != 0) RemoveDir(full);
                        else subdirs.Add(full);
                    } while (Native.FindNextFileW(h, d));
                }
                finally { Native.FindClose(h); }
            }
            foreach (var s in subdirs) DeleteTree(s);
            RemoveDir(dir);
        }

        void DeleteOneFile(string path, uint attrs)
        {
            if ((attrs & Native.AttrReadOnly) != 0) Native.SetAttributes(path, Native.AttrNormal);
            if (Native.DeleteFile(path)) Interlocked.Increment(ref FilesDeleted);
            else Error(path, Marshal.GetLastWin32Error());
        }

        void RemoveDir(string path)
        {
            if (Native.RemoveDirectory(path)) return;
            int err = Marshal.GetLastWin32Error();
            if (err == 5)
            {
                Native.SetAttributes(path, Native.AttrNormal);
                if (Native.RemoveDirectory(path)) return;
                err = Marshal.GetLastWin32Error();
            }
            Error(path, err);
        }

        void Error(string path, int code)
        {
            lock (Errors) Errors.Add(path + "  -  " + new Win32Exception(code).Message);
        }
    }
}
