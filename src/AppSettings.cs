namespace SteamLZXTray;

internal enum ScheduleMode { Daily, Interval, ManualOnly }

/// <summary>User settings (settings.json). Property names are the file format; don't rename them.</summary>
internal sealed class AppSettings
{
    public int CpuThresholdPercent { get; set; } = 50;
    /// <summary>Max disk throughput (MB/s) on the drive being compressed from OTHER processes (own I/O excluded).</summary>
    public int DriveThresholdMBps { get; set; } = 50;
    public ScheduleMode ScheduleMode { get; set; } = ScheduleMode.Daily;
    public string DailyTime { get; set; } = "03:00";
    public int IntervalHours { get; set; } = 6;
    public bool WatchSteamUpdates { get; set; } = true;
    public int MinFileSizeKB { get; set; } = 4;
    public bool TreatFullscreenAsGame { get; set; } = true;
    /// <summary>Install newer GitHub releases automatically (on by default).</summary>
    public bool AutoInstallUpdates { get; set; } = true;
    /// <summary>Canonical Start Menu shortcut (on by default).</summary>
    public bool StartMenuShortcut { get; set; } = true;
    public List<string> CustomFolders { get; set; } = new();
    /// <summary>Drive letters never touched. Empty by default; add drives manually in Settings.</summary>
    public List<string> ExcludedDrives { get; set; } = new();

    public static AppSettings Load(Action<string>? log)
    {
        var s = AtomicJson.Load(AppPaths.Settings, () => new AppSettings(), log);
        // An explicit null in the file would otherwise surface as a NullReferenceException later.
        s.DailyTime ??= "03:00";
        s.CustomFolders ??= new();
        s.ExcludedDrives ??= new();
        return s;
    }

    public void Save() => AtomicJson.Save(AppPaths.Settings, this);

    /// <summary>Configured daily start time; 03:00 if <see cref="DailyTime"/> can't be parsed.</summary>
    public TimeSpan DailyTimeOfDay() => TimeSpan.TryParse(DailyTime, out var tod) ? tod : TimeSpan.FromHours(3);

    public bool IsExcluded(string path)
    {
        if (path.Length < 2 || path[1] != ':') return false;
        string drive = char.ToUpperInvariant(path[0]) + ":";
        return ExcludedDrives.Any(x => x.Trim().TrimEnd('\\').Equals(drive, StringComparison.OrdinalIgnoreCase));
    }
}
