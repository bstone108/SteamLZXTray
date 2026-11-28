namespace SteamLZXTray;

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
