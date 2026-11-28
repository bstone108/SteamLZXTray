using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SteamLZXTray;

internal enum CompressResult { Compressed, InUse, NotBeneficial, Failed, Retryable, Gone }

internal readonly struct FileEntry
{
    public readonly string Name;
    public readonly uint Attributes;
    public readonly long Size;
    public readonly long MtimeTicks; // FILETIME (UTC, 100ns since 1601)
    public readonly uint ReparseTag;
    public bool IsDirectory => (Attributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
    public bool IsReparse => (Attributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    public FileEntry(string name, uint attr, long size, long mtime, uint tag)
    { Name = name; Attributes = attr; Size = size; MtimeTicks = mtime; ReparseTag = tag; }
}

internal static class FileOps
{
    /// <summary>Lists a directory (one level) via FindFirstFileExW, sorted OrdinalIgnoreCase. Includes reparse tags.</summary>
    public static List<FileEntry> List(string dir, out int error)
    {
        var list = new List<FileEntry>();
        error = 0;
        IntPtr h = Native.FindFirstFileExW(Native.Long(Path.Combine(dir, "*")), Native.FindExInfoBasic, out var fd,
            Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
        if (h == Native.INVALID_HANDLE_VALUE)
        {
            error = Marshal.GetLastWin32Error();
            if (error == Native.ERROR_FILE_NOT_FOUND || error == Native.ERROR_NO_MORE_FILES) error = 0;
            return list;
        }
        try
        {
            do
            {
                string n = fd.cFileName;
                if (n == "." || n == "..") continue;
                long size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                long mt = ((long)fd.ftLastWriteTimeHigh << 32) | fd.ftLastWriteTimeLow;
                uint tag = (fd.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? fd.dwReserved0 : 0;
                list.Add(new FileEntry(n, fd.dwFileAttributes, size, mt, tag));
            } while (Native.FindNextFileW(h, out fd));
        }
        finally { Native.FindClose(h); }
        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return list;
    }

    public static bool IsWofCompressed(in FileEntry e) => e.IsReparse && e.ReparseTag == Native.IO_REPARSE_TAG_WOF;

    /// <summary>Compress one file with WOF LZX (same as compact /c /exe:lzx). Preserves timestamps.</summary>
    public static CompressResult CompressLzx(string path, long size, out long saved, out string detail)
    {
        saved = 0; detail = "";
        string lp = Native.Long(path);

        // Exclusive open (share none): fails with sharing violation if anything else has it open.
        SafeFileHandle h = Native.CreateFileW(lp, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_NONE,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
        int err = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (h.IsInvalid && err == Native.ERROR_ACCESS_DENIED)
        {
            h.Dispose();
            // Read-only attribute / restrictive ACL: try read + write-attributes only.
            h = Native.CreateFileW(lp, Native.GENERIC_READ | Native.FILE_WRITE_ATTRIBUTES, Native.FILE_SHARE_NONE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
            err = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        }
        using (h)
        {
            if (h.IsInvalid)
            {
                detail = $"open failed (Win32 {err})";
                return err switch
                {
                    Native.ERROR_SHARING_VIOLATION or Native.ERROR_LOCK_VIOLATION or Native.ERROR_USER_MAPPED_FILE => CompressResult.InUse,
                    Native.ERROR_FILE_NOT_FOUND or Native.ERROR_PATH_NOT_FOUND => CompressResult.Gone,
                    Native.ERROR_ACCESS_DENIED => CompressResult.Failed,
                    _ => CompressResult.Retryable,
                };
            }

            bool haveTimes = Native.GetFileTime(h, out long ct, out long at, out long wt);

            var input = new Native.WOF_FILE_LZX_INPUT
            {
                WofVersion = Native.WOF_CURRENT_VERSION,
                WofProvider = Native.WOF_PROVIDER_FILE,
                FpVersion = Native.FILE_PROVIDER_CURRENT_VERSION,
                FpAlgorithm = Native.FILE_PROVIDER_COMPRESSION_LZX,
                FpFlags = 0,
            };
            bool ok = Native.DeviceIoControl(h, Native.FSCTL_SET_EXTERNAL_BACKING, ref input,
                Marshal.SizeOf<Native.WOF_FILE_LZX_INPUT>(), IntPtr.Zero, 0, out _, IntPtr.Zero);
            err = ok ? 0 : Marshal.GetLastWin32Error();

            // Restore original timestamps so backup tools (Kopia) see no change.
            // After SetFileTime on a handle, NTFS will not auto-update those times on close.
            if (haveTimes) Native.SetFileTime(h, ref ct, ref at, ref wt);

            if (!ok)
            {
                detail = $"FSCTL_SET_EXTERNAL_BACKING failed (Win32 {err})";
                return err switch
                {
                    Native.ERROR_COMPRESSION_NOT_BENEFICIAL => CompressResult.NotBeneficial,
                    Native.ERROR_SHARING_VIOLATION or Native.ERROR_LOCK_VIOLATION or Native.ERROR_USER_MAPPED_FILE => CompressResult.InUse,
                    // Out of space, or WOF unavailable (wof.sys not loaded / unsupported volume): retry later,
                    // never poison the skip list for these.
                    Native.ERROR_DISK_FULL or Native.ERROR_HANDLE_DISK_FULL
                        or Native.ERROR_INVALID_FUNCTION or Native.ERROR_NOT_SUPPORTED => CompressResult.Retryable,
                    _ => CompressResult.Failed,
                };
            }

        }

        // Measure after the exclusive handle is closed.
        long after = Native.CompressedSize(path);
        if (after >= 0 && after >= size)
        {
            // No savings: undo so the file stays a plain file, and remember it.
            using var h2 = Native.CreateFileW(lp, Native.GENERIC_READ | Native.FILE_WRITE_ATTRIBUTES, Native.FILE_SHARE_NONE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (!h2.IsInvalid && Native.GetFileTime(h2, out long c2, out long a2, out long w2))
            {
                Native.DeviceIoControl(h2, Native.FSCTL_DELETE_EXTERNAL_BACKING, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                Native.SetFileTime(h2, ref c2, ref a2, ref w2);
            }
            detail = "no savings";
            return CompressResult.NotBeneficial;
        }
        saved = after >= 0 ? size - after : 0;
        return CompressResult.Compressed;
    }
}
