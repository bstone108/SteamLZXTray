using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamLZXTray;

internal static class AppPaths
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamLZXTray");
    public static string Settings => Path.Combine(Dir, "settings.json");
    public static string State => Path.Combine(Dir, "state.json");
    public static string GameLog => Path.Combine(Dir, "gamelog.json");
    public static string SkipList => Path.Combine(Dir, "skiplist");
    public static string Logs => Path.Combine(Dir, "logs");
    public static void Ensure() { Directory.CreateDirectory(Dir); Directory.CreateDirectory(Logs); }
}

/// <summary>Power-loss tolerant JSON files: write .tmp, FlushFileBuffers, ReplaceFile (keeps .bak).</summary>
internal static class AtomicJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object WriteLock = new();

    public static void Save<T>(string path, T value)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        string tmp = path + ".tmp";
        string bak = path + ".bak";
        lock (WriteLock)
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true); // FlushFileBuffers
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, bak, ignoreMetadataErrors: true); }
                catch (IOException)
                {
                    // ReplaceFile can fail if the target is damaged; fall back to copy-then-move.
                    try { File.Copy(path, bak, true); } catch { /* best effort */ }
                    File.Move(tmp, path, true);
                }
            }
            else
            {
                File.Move(tmp, path, true);
            }
        }
    }

    /// <summary>Load file; if missing/corrupt/truncated try .bak; else default.</summary>
    public static T Load<T>(string path, Func<T> fallback, Action<string>? log = null) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak", path + ".tmp" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var bytes = File.ReadAllBytes(candidate);
                if (bytes.Length == 0 || bytes.All(b => b == 0)) throw new InvalidDataException("empty/zeroed file");
                var v = JsonSerializer.Deserialize<T>(bytes, Options);
                if (v != null)
                {
                    if (candidate != path) log?.Invoke($"Recovered {Path.GetFileName(path)} from {Path.GetFileName(candidate)}");
                    return v;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not read {candidate}: {ex.Message}");
            }
        }
        return fallback();
    }
}

internal enum ScheduleMode { Daily, Interval, ManualOnly }

internal sealed class AppSettings
{
    public int CpuThresholdPercent { get; set; } = 30;
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

    public static AppSettings Load(Action<string>? log) => AtomicJson.Load(AppPaths.Settings, () => new AppSettings(), log);
    public void Save() => AtomicJson.Save(AppPaths.Settings, this);

    public bool IsExcluded(string path)
    {
        if (path.Length < 2 || path[1] != ':') return false;
        string d = char.ToUpperInvariant(path[0]) + ":";
        return ExcludedDrives.Any(x => x.Trim().TrimEnd('\\').Equals(d, StringComparison.OrdinalIgnoreCase));
    }
}

internal enum RunKind { None, Scheduled, Manual, Triggered }

internal sealed class RunState
{
    public RunKind ActiveRun { get; set; } = RunKind.None;
    public DateTime? RunStartedUtc { get; set; }
    public string? CurrentUnitKey { get; set; }
    /// <summary>Relative path (inside the current unit) of the last fully handled file.</summary>
    public string? LastCompletedRelPath { get; set; }
    /// <summary>True if the current unit had in-use files skipped (persisted so a power cut can't lose it).</summary>
    public bool CurrentUnitIncomplete { get; set; }
    public List<string> TriggeredUnitKeys { get; set; } = new();
    public List<string> PendingTriggerKeys { get; set; } = new();
    public DateTime? LastScheduledRunUtc { get; set; }
    public DateTime? LastRunCompletedUtc { get; set; }
    public long TotalBytesSaved { get; set; }
    public long TotalFilesCompressed { get; set; }
    public long RunBytesSaved { get; set; }
    public long RunFilesProcessed { get; set; }
    public long RunFilesCompressed { get; set; }

    public static RunState Load(Action<string>? log) => AtomicJson.Load(AppPaths.State, () => new RunState(), log);
    public void Save() => AtomicJson.Save(AppPaths.State, this);
}

internal sealed class GameLogEntry
{
    public string Name { get; set; } = "";
    public string BuildId { get; set; } = "";
    public string Library { get; set; } = "";
    public DateTime CompletedUtc { get; set; }
}

internal sealed class FolderLogEntry
{
    public long LatestMtimeTicks { get; set; }
    public long FileCount { get; set; }
    public DateTime CompletedUtc { get; set; }
}

internal sealed class GameLog
{
    /// <summary>Key: "libraryPath|appid".</summary>
    public Dictionary<string, GameLogEntry> Steam { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Key: full path of a custom folder's top-level subfolder.</summary>
    public Dictionary<string, FolderLogEntry> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static GameLog Load(Action<string>? log)
    {
        var g = AtomicJson.Load(AppPaths.GameLog, () => new GameLog(), log);
        // re-wrap with case-insensitive comparers after deserialization
        g.Steam = new Dictionary<string, GameLogEntry>(g.Steam, StringComparer.OrdinalIgnoreCase);
        g.Folders = new Dictionary<string, FolderLogEntry>(g.Folders, StringComparer.OrdinalIgnoreCase);
        return g;
    }
    public void Save() => AtomicJson.Save(AppPaths.GameLog, this);
}

/// <summary>
/// Append-only skip list ("size|mtimeTicks|reason|path" per line), fsync'd per append.
/// A torn last line after power loss is simply ignored.
/// </summary>
internal sealed class SkipList
{
    private readonly Dictionary<string, (long Size, long Mtime)> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _map.Count; } }

    public void Load(Action<string> log)
    {
        lock (_lock)
        {
            _map.Clear();
            if (!File.Exists(AppPaths.SkipList)) return;
            int lines = 0, bad = 0;
            foreach (var line in File.ReadLines(AppPaths.SkipList, Encoding.UTF8))
            {
                lines++;
                var parts = line.Split('|', 4);
                if (parts.Length == 4 && long.TryParse(parts[0], out var size) && long.TryParse(parts[1], out var mt) && parts[3].Length > 2)
                    _map[parts[3]] = (size, mt);
                else bad++;
            }
            if (bad > 0 || lines > _map.Count * 2 + 100) Compact();
            log($"Skip list loaded: {_map.Count} entries" + (bad > 0 ? $" ({bad} damaged lines dropped)" : ""));
        }
    }

    public bool ShouldSkip(string path, long size, long mtime)
    {
        lock (_lock)
            return _map.TryGetValue(path, out var e) && e.Size == size && e.Mtime == mtime;
    }

    public void Add(string path, long size, long mtime, string reason)
    {
        lock (_lock)
        {
            _map[path] = (size, mtime);
            string line = $"{size}|{mtime}|{reason.Replace('|', '/')}|{path}\r\n";
            byte[] b = Encoding.UTF8.GetBytes(line);
            using var fs = new FileStream(AppPaths.SkipList, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            fs.Write(b, 0, b.Length);
            fs.Flush(true);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            try { File.Delete(AppPaths.SkipList); } catch { /* ignore */ }
        }
    }

    private void Compact()
    {
        var sb = new StringBuilder();
        foreach (var kv in _map) sb.Append($"{kv.Value.Size}|{kv.Value.Mtime}|-|{kv.Key}\r\n");
        string tmp = AppPaths.SkipList + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var b = Encoding.UTF8.GetBytes(sb.ToString());
            fs.Write(b, 0, b.Length);
            fs.Flush(true);
        }
        File.Move(tmp, AppPaths.SkipList, true);
    }
}

/// <summary>Rotating file log (2 MB x 5) plus an in-memory ring for the UI.</summary>
internal static class Log
{
    private static readonly object Lock = new();
    private static readonly LinkedList<string> Ring = new();
    private const int RingMax = 500;
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int Keep = 5;
    private static string FilePath => Path.Combine(AppPaths.Logs, "SteamLZXTray.log");
    public static long Version { get; private set; }

    public static void Write(string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";
        lock (Lock)
        {
            Ring.AddLast(line);
            while (Ring.Count > RingMax) Ring.RemoveFirst();
            Version++;
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes) Rotate();
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never crash the app */ }
        }
    }

    public static string[] Snapshot() { lock (Lock) return Ring.ToArray(); }

    private static void Rotate()
    {
        for (int i = Keep - 1; i >= 1; i--)
        {
            string src = FilePath + "." + i, dst = FilePath + "." + (i + 1);
            if (File.Exists(src)) File.Move(src, dst, true);
        }
        File.Move(FilePath, FilePath + ".1", true);
        string oldest = FilePath + "." + (Keep + 1);
        if (File.Exists(oldest)) File.Delete(oldest);
    }
}
