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
        public const uint InvalidAttributes = 0xFFFFFFFF;
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

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);

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

        public static System.Drawing.Icon StockIcon(bool folder)
        {
            const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
            var info = new SHFILEINFO();
            IntPtr r = SHGetFileInfoW(folder ? "folder" : "file", folder ? AttrDirectory : AttrNormal, ref info,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(info.hIcon).Clone();
            DestroyIcon(info.hIcon);
            return icon;
        }
    }
}
