using System.Text;
using Microsoft.Win32;

namespace SteamLZXTray;

/// <summary>
/// Samples system CPU, per-volume disk throughput (PDH, English counter names) and the
/// foreground window once per second on its own thread.
/// </summary>
internal sealed class IdleMonitor : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly object _lock = new();

    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _diskCounters = new(StringComparer.OrdinalIgnoreCase);     // Disk Bytes/sec
    private readonly Dictionary<string, double> _diskBytesPerSec = new(StringComparer.OrdinalIgnoreCase);  // total, raw
    private readonly HashSet<string> _wantedDrives = new(StringComparer.OrdinalIgnoreCase);

    private long _lastIdle, _lastKernel, _lastUser;
    private ulong _lastOwnIo;
    private long _lastAttributed;
    private long _attributedBytes; // bytes we know we read/wrote (WOF writes may not show in process counters)
    private DateTime _lastSample = DateTime.UtcNow;

    private string[] _gameRoots = Array.Empty<string>();
    private readonly int _ownPid = Environment.ProcessId;

    public double CpuPercent { get; private set; }
    /// <summary>System CPU minus this process's CPU, smoothed (EMA ~4 s).</summary>
    public double OtherCpuSmoothed { get; private set; }
    private TimeSpan _lastOwnCpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
    private readonly Dictionary<string, double> _diskSmoothed = new(StringComparer.OrdinalIgnoreCase);
    public double OwnIoBytesPerSec { get; private set; }
    public bool GameInForeground { get; private set; }
    public string GameReason { get; private set; } = "";
    public bool TreatFullscreenAsGame { get; set; } = true;

    public IdleMonitor()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "IdleMonitor" };
        _thread.Start();
    }

    public void AddAttributedIo(long bytes) => Interlocked.Add(ref _attributedBytes, bytes);

    public void SetGameRoots(IEnumerable<string> roots)
    {
        _gameRoots = roots.Select(r => r.TrimEnd('\\') + "\\").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void WatchDrive(string volumeRoot)
    {
        string? d = DriveOf(volumeRoot);
        if (d == null) return;
        lock (_lock) _wantedDrives.Add(d);
    }

    public static string? DriveOf(string path) =>
        path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) + ":" : null;

    /// <summary>Total drive throughput (all processes), for the status readout only. -1 if unknown.</summary>
    public double TotalDiskBytesPerSec(string volumeRoot)
    {
        string? d = DriveOf(volumeRoot);
        if (d == null) return -1;
        lock (_lock) return _diskBytesPerSec.TryGetValue(d, out var v) ? v : -1;
    }

    /// <summary>Disk bytes/sec on the volume from other processes (own I/O subtracted). -1 if unknown.</summary>
    public double OtherDiskBytesPerSec(string volumeRoot)
    {
        string? d = DriveOf(volumeRoot);
        if (d == null) return -1;
        lock (_lock)
        {
            return _diskSmoothed.TryGetValue(d, out var v) ? v : -1;
        }
    }

    private void Loop()
    {
        try { Native.PdhOpenQueryW(null, IntPtr.Zero, out _query); } catch (Exception ex) { Log.Write("PDH unavailable: " + ex.Message); }
        Native.GetSystemTimes(out _lastIdle, out _lastKernel, out _lastUser);
        Native.GetProcessIoCounters(Native.GetCurrentProcess(), out var io0);
        _lastOwnIo = io0.ReadTransferCount + io0.WriteTransferCount;
        while (!_stop)
        {
            Thread.Sleep(1000);
            try { Sample(); } catch (Exception ex) { Log.Write("Idle sampler error: " + ex.Message); }
        }
    }

    private void Sample()
    {
        var now = DateTime.UtcNow;
        double secs = Math.Max(0.2, (now - _lastSample).TotalSeconds);
        _lastSample = now;

        // CPU (kernel time includes idle time)
        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long di = idle - _lastIdle, dk = kernel - _lastKernel, du = user - _lastUser;
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
            long total = dk + du;
            if (total > 0) CpuPercent = Math.Clamp(100.0 * (total - di) / total, 0, 100);
        }
        using (var me = System.Diagnostics.Process.GetCurrentProcess())
        {
            var own = me.TotalProcessorTime;
            double ownPct = 100.0 * (own - _lastOwnCpu).TotalSeconds / (secs * Environment.ProcessorCount);
            _lastOwnCpu = own;
            double other = Math.Max(0, CpuPercent - ownPct);
            OtherCpuSmoothed = OtherCpuSmoothed * 0.75 + other * 0.25;
        }

        // Own I/O: max(process counters, bytes we attributed to ourselves)
        if (Native.GetProcessIoCounters(Native.GetCurrentProcess(), out var io))
        {
            ulong cur = io.ReadTransferCount + io.WriteTransferCount;
            double procRate = (cur - _lastOwnIo) / secs;
            _lastOwnIo = cur;
            long attr = Interlocked.Read(ref _attributedBytes);
            double attrRate = (attr - _lastAttributed) / secs;
            _lastAttributed = attr;
            OwnIoBytesPerSec = Math.Max(procRate, attrRate);
        }

        // Disk counters
        if (_query != IntPtr.Zero)
        {
            string[] wanted;
            lock (_lock) wanted = _wantedDrives.ToArray();
            foreach (var d in wanted)
            {
                if (_diskCounters.ContainsKey(d)) continue;
                uint r = Native.PdhAddEnglishCounterW(_query, $@"\LogicalDisk({d})\Disk Bytes/sec", IntPtr.Zero, out var hc);
                if (r == 0) _diskCounters[d] = hc;
                else { _diskCounters[d] = IntPtr.Zero; Log.Write($"Disk counter for {d} unavailable (PDH 0x{r:X8}); drive-busy check disabled for it."); }
            }
            if (Native.PdhCollectQueryData(_query) == 0)
            {
                double own = OwnIoBytesPerSec;
                lock (_lock)
                    foreach (var kv in _diskCounters)
                    {
                        if (kv.Value == IntPtr.Zero) continue;
                        if (Native.PdhGetFormattedCounterValue(kv.Value, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, out _, out var v) == 0
                            && (v.CStatus == 0 || v.CStatus == 1)) // PDH_CSTATUS_VALID_DATA / NEW_DATA
                        {
                            double total = Math.Max(0, v.doubleValue);
                            _diskBytesPerSec[kv.Key] = total;
                            double otherNow = Math.Max(0, total - own); // exclude our own I/O
                            _diskSmoothed[kv.Key] = _diskSmoothed.TryGetValue(kv.Key, out var prev) ? prev * 0.6 + otherNow * 0.4 : otherNow;
                        }
                    }
            }
        }

        // Foreground game
        (GameInForeground, GameReason) = DetectGame();
    }

    private (bool, string) DetectGame()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("RunningAppID") is int id && id != 0) return (true, $"Steam game running (AppID {id})");
        }
        catch { }

        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (false, "");
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _ownPid) return (false, "");

        string? image = ProcessImage(pid);
        if (image != null)
        {
            foreach (var root in _gameRoots)
                if (image.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return (true, "Game in foreground: " + Path.GetFileName(image));
        }

        if (TreatFullscreenAsGame && IsFullscreen(hwnd))
            return (true, "Fullscreen app in foreground" + (image != null ? ": " + Path.GetFileName(image) : ""));
        return (false, "");
    }

    private static string? ProcessImage(uint pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return Native.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        var cls = new StringBuilder(256);
        Native.GetClassNameW(hwnd, cls, cls.Capacity);
        string c = cls.ToString();
        if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!Native.GetWindowRect(hwnd, out var wr)) return false;
        IntPtr mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (mon == IntPtr.Zero || !Native.GetMonitorInfoW(mon, ref mi)) return false;
        return wr.Left <= mi.rcMonitor.Left && wr.Top <= mi.rcMonitor.Top &&
               wr.Right >= mi.rcMonitor.Right && wr.Bottom >= mi.rcMonitor.Bottom;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(3000);
        if (_query != IntPtr.Zero) { Native.PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}
