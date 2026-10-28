using System;
using System.Runtime.InteropServices;

namespace DiskSpaceAnalyzer
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct FindData
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        public fixed char FileName[260];
        public fixed char AltName[14];
    }

    internal static unsafe class Native
    {
        public const uint AttrReadOnly = 0x1;
        public const uint AttrDirectory = 0x10;
        public const uint AttrNormal = 0x80;
        public const uint AttrReparsePoint = 0x400;
        public const uint AttrSparse = 0x200;
        public const uint AttrCompressed = 0x800;
        public const uint AttrOffline = 0x1000;
        public const uint AttrRecallOnOpen = 0x40000;
        public const uint AttrRecallOnDataAccess = 0x400000;
        /// <summary>Cloud placeholders (OneDrive "online-only" and similar): the data is not on this disk.</summary>
        public const uint AttrCloudMask = AttrOffline | AttrRecallOnOpen | AttrRecallOnDataAccess;
        public const uint InvalidAttributes = 0xFFFFFFFF;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern uint GetCompressedFileSizeW(string path, out uint high);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

        /// <summary>Allocation unit of the volume that holds path (4 KB when it cannot be read).</summary>
        public static long ClusterSize(string path)
        {
            try
            {
                string root = System.IO.Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root)) return 4096;
                if (!root.EndsWith("\\")) root += "\\";
                uint spc, bps, f, t;
                if (GetDiskFreeSpaceW(root, out spc, out bps, out f, out t) && spc > 0 && bps > 0) return (long)spc * bps;
            }
            catch (Exception) { }
            return 4096;
        }

        /// <summary>
        /// Size on disk of a file. Online-only cloud files take nothing; compressed, sparse and system-compressed
        /// (reparse point, e.g. Compact OS) files are asked for their real allocation; others are rounded up to clusters.
        /// </summary>
        public static long Allocated(long size, uint attr, long cluster, string dir, string name)
        {
            if ((attr & AttrCloudMask) != 0) return 0;
            if (size <= 0) return 0;
            if ((attr & (AttrCompressed | AttrSparse | AttrReparsePoint)) != 0)
            {
                uint high;
                uint low = GetCompressedFileSizeW(LongPath(Join(dir, name)), out high);
                if (low != 0xFFFFFFFF || Marshal.GetLastWin32Error() == 0)
                    size = ((long)high << 32) | low;
            }
            return (size + cluster - 1) / cluster * cluster;
        }
        public static readonly IntPtr InvalidHandle = new IntPtr(-1);

        const int FindExInfoBasic = 1;
        const int FindFirstExLargeFetch = 2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern IntPtr FindFirstFileExW(string fileName, int infoLevel, FindData* data, int searchOp, IntPtr filter, int flags);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool FindNextFileW(IntPtr handle, FindData* data);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern bool FindClose(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern bool DeleteFileW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern bool RemoveDirectoryW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern bool SetFileAttributesW(string path, uint attributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern uint GetFileAttributesW(string path);

        /// <summary>Adds the \\?\ prefix so paths longer than MAX_PATH work.</summary>
        public static string LongPath(string p)
        {
            if (p.StartsWith(@"\\?\")) return p;
            if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }

        public static string Join(string dir, string name)
        {
            return dir.EndsWith("\\") ? dir + name : dir + "\\" + name;
        }

        public static IntPtr FindFirst(string dir, FindData* data)
        {
            return FindFirstFileExW(LongPath(Join(dir, "*")), FindExInfoBasic, data, 0, IntPtr.Zero, FindFirstExLargeFetch);
        }

        public static bool IsDots(char* n)
        {
            return n[0] == '.' && (n[1] == '\0' || (n[1] == '.' && n[2] == '\0'));
        }

        public static long Size(FindData* d)
        {
            return ((long)d->SizeHigh << 32) | d->SizeLow;
        }

        public static long WriteTime(FindData* d)
        {
            return ((long)d->WriteHigh << 32) | d->WriteLow;
        }

        public static uint GetAttributes(string path)
        {
            return GetFileAttributesW(LongPath(path));
        }

        public static bool Exists(string path)
        {
            return GetAttributes(path) != InvalidAttributes;
        }

        public static bool SetAttributes(string path, uint attributes)
        {
            return SetFileAttributesW(LongPath(path), attributes);
        }

        public static bool DeleteFile(string path)
        {
            return DeleteFileW(LongPath(path));
        }

        public static bool RemoveDirectory(string path)
        {
            return RemoveDirectoryW(LongPath(path));
        }
    }
}
