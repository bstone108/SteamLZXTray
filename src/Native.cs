using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SteamLZXTray;

internal static class Native
{
    // ---- Errors ----
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INVALID_FUNCTION = 1;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_LOCK_VIOLATION = 33;
    public const int ERROR_HANDLE_DISK_FULL = 39;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_DISK_FULL = 112;
    public const int ERROR_USER_MAPPED_FILE = 1224;
    public const int ERROR_COMPRESSION_NOT_BENEFICIAL = 344;

    // ---- File access / attributes ----
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    public const uint FILE_SHARE_NONE = 0;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_COMPRESSED = 0x800;
    public const uint FILE_ATTRIBUTE_OFFLINE = 0x1000;
    public const uint FILE_ATTRIBUTE_ENCRYPTED = 0x4000;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    public const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x200;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    public const uint IO_REPARSE_TAG_WOF = 0x80000017;

    // ---- Volume ----
    public const uint FILE_FILE_COMPRESSION = 0x00000010;
    public const uint FILE_READ_ONLY_VOLUME = 0x00080000;

    // ---- WOF ----
    // CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9, fn, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0) = (9<<16)|(fn<<2)
    public const uint FSCTL_SET_EXTERNAL_BACKING = 0x0009030C;    // fn 195
    public const uint FSCTL_DELETE_EXTERNAL_BACKING = 0x00090314; // fn 197
    public const uint WOF_CURRENT_VERSION = 1;
    public const uint WOF_PROVIDER_FILE = 2;
    public const uint FILE_PROVIDER_CURRENT_VERSION = 1;
    public const uint FILE_PROVIDER_COMPRESSION_LZX = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct WOF_FILE_LZX_INPUT // WOF_EXTERNAL_INFO followed by FILE_PROVIDER_EXTERNAL_INFO_V1 (20 bytes)
    {
        public uint WofVersion;   // WOF_EXTERNAL_INFO.Version
        public uint WofProvider;  // WOF_EXTERNAL_INFO.Provider
        public uint FpVersion;    // FILE_PROVIDER_EXTERNAL_INFO_V1.Version
        public uint FpAlgorithm;  // FILE_PROVIDER_EXTERNAL_INFO_V1.Algorithm
        public uint FpFlags;      // FILE_PROVIDER_EXTERNAL_INFO_V1.Flags
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        ref WOF_FILE_LZX_INPUT lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileTime(SafeFileHandle hFile, out long lpCreationTime, out long lpLastAccessTime, out long lpLastWriteTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileTime(SafeFileHandle hFile, ref long lpCreationTime, ref long lpLastAccessTime, ref long lpLastWriteTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathNameW(string lpszFileName, StringBuilder lpszVolumePathName, int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformationW(string lpRootPathName, StringBuilder? lpVolumeNameBuffer, int nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer, int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetDriveTypeW(string lpRootPathName);

    // ---- Find files ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public uint ftCreationTimeLow, ftCreationTimeHigh;
        public uint ftLastAccessTimeLow, ftLastAccessTimeHigh;
        public uint ftLastWriteTimeLow, ftLastWriteTimeHigh;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0; // reparse tag when FILE_ATTRIBUTE_REPARSE_POINT is set
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }
    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;
    public const uint FIND_FIRST_EX_LARGE_FETCH = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp, IntPtr lpSearchFilter, uint dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClose(IntPtr hFindFile);

    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // ---- CPU / IO counters ----
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentThread();

    // ---- Priority ----
    public const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    public const int THREAD_PRIORITY_IDLE = -15;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    // ProcessIoPriority = 33; IO_PRIORITY_HINT IoPriorityVeryLow = 0
    public const int ProcessIoPriority = 33;
    [DllImport("ntdll.dll")]
    public static extern int NtSetInformationProcess(IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength);

    // EcoQoS on a single thread: SetThreadInformation(ThreadPowerThrottling = 3, THREAD_POWER_THROTTLING_STATE)
    public const int ThreadPowerThrottling = 3;
    public const uint THREAD_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint THREAD_POWER_THROTTLING_EXECUTION_SPEED = 1;
    [StructLayout(LayoutKind.Sequential)]
    public struct THREAD_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadInformation(IntPtr hThread, int threadInformationClass,
        ref THREAD_POWER_THROTTLING_STATE threadInformation, int threadInformationSize);

    // ---- Foreground / processes ----
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // ---- Lock screen / secure desktop / screensaver ----
    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const int UOI_NAME = 2;
    public const uint SPI_GETSCREENSAVERRUNNING = 0x0072;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex, StringBuilder pvInfo, uint nLength, out uint lpnLengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, [MarshalAs(UnmanagedType.Bool)] out bool pvParam, uint fWinIni);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    // ---- PDH (English counter names: locale independent) ----
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr hQuery);

    // ---- Icons ----
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---- Helpers ----
    /// <summary>Convert a normal absolute path to an extended-length (\\?\) path for Win32 calls.</summary>
    public static string Long(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }

    public static long CompressedSize(string path)
    {
        uint low = GetCompressedFileSizeW(Long(path), out uint high);
        if (low == 0xFFFFFFFF)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_SUCCESS) return -1;
        }
        return ((long)high << 32) | low;
    }

    public static string? VolumeRoot(string path)
    {
        var sb = new StringBuilder(1024);
        return GetVolumePathNameW(path, sb, sb.Capacity) ? sb.ToString() : null;
    }
}
