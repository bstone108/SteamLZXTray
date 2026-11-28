using System.Text;

namespace SteamLZXTray;

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
            try
            {
                foreach (var line in File.ReadLines(AppPaths.SkipList, Encoding.UTF8))
                {
                    lines++;
                    var parts = line.Split('|', 4);
                    if (parts.Length == 4 && long.TryParse(parts[0], out var size) && long.TryParse(parts[1], out var mtime) && parts[3].Length > 2)
                        _map[parts[3]] = (size, mtime);
                    else
                        bad++;
                }
            }
            catch (Exception ex)
            {
                // Keep whatever was read; an unreadable file must not stop the app from starting.
                log($"Could not read skip list: {ex.Message}");
                return;
            }
            if (bad > 0 || lines > _map.Count * 2 + 100)
            {
                try { Compact(); }
                catch (Exception ex) { log($"Skip list compaction failed: {ex.Message}"); }
            }
            log($"Skip list loaded: {_map.Count} entries" + (bad > 0 ? $" ({bad} damaged lines dropped)" : ""));
        }
    }

    public bool ShouldSkip(string path, long size, long mtime)
    {
        lock (_lock)
            return _map.TryGetValue(path, out var e) && e.Size == size && e.Mtime == mtime;
    }

    /// <summary>Adds an entry. If the file can't be written, the entry still applies for this session.</summary>
    public void Add(string path, long size, long mtime, string reason)
    {
        lock (_lock)
        {
            _map[path] = (size, mtime);
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes($"{size}|{mtime}|{reason.Replace('|', '/')}|{path}\r\n");
                using var fs = new FileStream(AppPaths.SkipList, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            catch (Exception ex) { Log.Write($"Skip list write failed for {path}: {ex.Message}"); }
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

    /// <summary>Caller holds _lock. Rewrites the file with one line per live entry.</summary>
    private void Compact()
    {
        var sb = new StringBuilder();
        foreach (var kv in _map) sb.Append($"{kv.Value.Size}|{kv.Value.Mtime}|-|{kv.Key}\r\n");
        string tmp = AppPaths.SkipList + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        File.Move(tmp, AppPaths.SkipList, true);
    }
}
