using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCManagerCS
{
    /// <summary>Win32 API 선언 (WMI 등 무거운 라이브러리 대신 직접 호출)</summary>
    internal static class Native
    {
        // ── 파일 / 볼륨 ─────────────────────────────────────────
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
        public const int ERROR_HANDLE_EOF = 38;

        [StructLayout(LayoutKind.Sequential)]
        public struct MFT_ENUM_DATA_V0
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref MFT_ENUM_DATA_V0 inBuf, int inSize,
            IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

        public const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
        public const int FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

        // FILETIME은 4바이트 정렬 → Pack = 4 필수 (long 기본 8바이트 정렬 시 필드가 밀림)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        public struct WIN32_FIND_DATA
        {
            public int dwFileAttributes;
            public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow;
            public uint dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        public const int FindExInfoBasic = 1;
        public const int FIND_FIRST_EX_LARGE_FETCH = 2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstFileEx(string fileName, int infoLevel, out WIN32_FIND_DATA data,
            int searchOp, IntPtr filter, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool FindNextFile(IntPtr h, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll")]
        public static extern bool FindClose(IntPtr h);

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public int dwFileAttributes;
            public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetFileAttributesEx(string name, int level, out WIN32_FILE_ATTRIBUTE_DATA data);

        // ── 전원 / 메모리 ────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        // ── 셸 아이콘 ────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        public const uint SHGFI_SYSICONINDEX = 0x4000, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_ICON = 0x100;

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr h);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string path, int attrs, ref SHFILEINFO info, int size, uint flags);

        // ── 윈도우 메시지 ───────────────────────────────────────
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref HDITEM l);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr h, string app, string idList);

        public const int EM_SETCUEBANNER = 0x1501;
        public const int LVM_SETIMAGELIST = 0x1003, LVSIL_SMALL = 1;
        public const int LVM_GETHEADER = 0x101F;
        public const int HDM_GETITEM = 0x120B, HDM_SETITEM = 0x120C;
        public const int HDI_FORMAT = 0x4, HDF_SORTUP = 0x400, HDF_SORTDOWN = 0x200;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct HDITEM
        {
            public int mask, cxy;
            public IntPtr pszText, hbm;
            public int cchTextMax, fmt;
            public IntPtr lParam;
            public int iImage, iOrder;
            public uint type;
            public IntPtr pvFilter;
            public uint state;
        }
    }
}
