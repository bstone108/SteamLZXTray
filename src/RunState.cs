namespace SteamLZXTray;

internal enum RunKind { None, Scheduled, Manual, Triggered }

/// <summary>
/// Pass progress and totals (state.json), saved often so a power cut resumes where it stopped.
/// Property names are the file format; don't rename them. Older files simply lack the newer properties.
/// </summary>
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
    /// <summary>True if PassStartSnapshot was taken when this pass began (or when a legacy pass was restarted).</summary>
    public bool HasPassStartSnapshot { get; set; }
    /// <summary>Set when a legacy pass is reset from scratch; this pass does not skip games via the game log.</summary>
    public bool IgnoreGameLogForPass { get; set; }
    /// <summary>Every Steam game's ACF fingerprint at the start of the current pass. Reused on resume; never retaken mid-pass.</summary>
    public Dictionary<string, SteamGameState> PassStartSnapshot { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>True if LastKnownSteamState has been seeded (empty is a valid baseline when you have no games).</summary>
    public bool HasLastKnownSteamState { get; set; }
    /// <summary>Last observed ACF fingerprint of every Steam game, persisted across restarts for startup and rescan compares.</summary>
    public Dictionary<string, SteamGameState> LastKnownSteamState { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static RunState Load(Action<string>? log) => Normalize(AtomicJson.Load(AppPaths.State, () => new RunState(), log));

    /// <summary>Re-wraps dictionaries with case-insensitive keys and replaces nulls left by older or hand-edited files.</summary>
    internal static RunState Normalize(RunState s)
    {
        s.PassStartSnapshot = new Dictionary<string, SteamGameState>(s.PassStartSnapshot ?? new(), StringComparer.OrdinalIgnoreCase);
        s.LastKnownSteamState = new Dictionary<string, SteamGameState>(s.LastKnownSteamState ?? new(), StringComparer.OrdinalIgnoreCase);
        s.PendingTriggerKeys ??= new();
        s.TriggeredUnitKeys ??= new();
        return s;
    }

    public void Save() => AtomicJson.Save(AppPaths.State, this);
}

/// <summary>Install-state fingerprint of one Steam game (buildid, LastUpdated, SizeOnDisk, StateFlags), stored in state.json.</summary>
internal sealed class SteamGameState
{
    /// <summary>ACF StateFlags value for a fully installed, not-updating game.</summary>
    public const int FullyInstalled = 4;

    public string Name { get; set; } = "";
    public string BuildId { get; set; } = "";
    public string LastUpdated { get; set; } = "";
    public string SizeOnDisk { get; set; } = "";
    public int StateFlags { get; set; }

    public static SteamGameState FromAcf(AcfInfo a) => new()
    {
        Name = a.Name,
        BuildId = a.BuildId,
        LastUpdated = a.LastUpdated,
        SizeOnDisk = a.SizeOnDisk,
        StateFlags = a.StateFlags,
    };

    public bool SameAs(SteamGameState other) =>
        BuildId == other.BuildId
        && LastUpdated == other.LastUpdated
        && SizeOnDisk == other.SizeOnDisk
        && StateFlags == other.StateFlags;
}
