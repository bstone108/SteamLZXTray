namespace SteamLZXTray;

internal enum Phase { Idle, Working, Waiting, Paused }

/// <summary>Point-in-time snapshot of the engine for the UI and tray (copied under the engine lock).</summary>
internal sealed class EngineStatus
{
    public Phase Phase;
    public string WaitReason = "";
    public RunKind Run;
    public string Library = "", Game = "", File = "";
    public long RunFilesProcessed, RunFilesCompressed, RunBytesSaved, TotalBytesSaved, TotalFilesCompressed;
    public int SkipListCount, PendingTriggers;
    public double CpuOther, DiskOtherMBps = -1, DiskTotalMBps = -1;
    public DateTime? LastRunCompletedUtc;
    public string NextSchedule = "";
}
