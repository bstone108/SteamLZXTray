using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamLZXTray;

/// <summary>Locations of everything the app keeps under %LOCALAPPDATA%\SteamLZXTray.</summary>
internal static class AppPaths
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamLZXTray");
    public static string Settings => Path.Combine(Dir, "settings.json");
    public static string State => Path.Combine(Dir, "state.json");
    public static string GameLog => Path.Combine(Dir, "gamelog.json");
    public static string SkipList => Path.Combine(Dir, "skiplist");
    public static string Logs => Path.Combine(Dir, "logs");

    public static void Ensure()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(Logs);
    }
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
