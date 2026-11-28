namespace SteamLZXTray;

/// <summary>Units finished at least once (gamelog.json). Property names are the file format; don't rename them.</summary>
internal sealed class GameLog
{
    /// <summary>Key: "libraryPath|appid".</summary>
    public Dictionary<string, GameLogEntry> Steam { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Key: full path of a custom folder's top-level subfolder.</summary>
    public Dictionary<string, FolderLogEntry> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static GameLog Load(Action<string>? log)
    {
        var g = AtomicJson.Load(AppPaths.GameLog, () => new GameLog(), log);
        // Re-wrap with case-insensitive comparers after deserialization (and tolerate an explicit null).
        g.Steam = new Dictionary<string, GameLogEntry>(g.Steam ?? new(), StringComparer.OrdinalIgnoreCase);
        g.Folders = new Dictionary<string, FolderLogEntry>(g.Folders ?? new(), StringComparer.OrdinalIgnoreCase);
        return g;
    }

    public void Save() => AtomicJson.Save(AppPaths.GameLog, this);
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
