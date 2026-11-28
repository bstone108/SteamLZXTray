namespace SteamLZXTray;

/// <summary>Walking a unit's files, compressing them one at a time, and waiting for the PC to be idle in between.</summary>
internal sealed partial class Engine
{
    private DateTime _lastStateSave = DateTime.MinValue;

    /// <summary>Per-unit walk state.</summary>
    private sealed class WalkCtx
    {
        public WorkUnit Unit = null!;
        public long MinSize;
        public List<(string Full, string Rel, long Size)> InUse = new();
        public bool Aborted;
    }

    private UnitResult WalkUnit(WorkUnit u, string? resumeRel)
    {
        var ctx = new WalkCtx { Unit = u, MinSize = Math.Max(0, Settings.MinFileSizeKB) * 1024L };
        string[]? resume = string.IsNullOrEmpty(resumeRel) ? null : resumeRel.Split('\\');
        WalkDir(ctx, u.Root, "", resume, 0);
        if (ctx.Aborted) return UnitResult.Aborted;

        // One more try for files that were in use during this pass.
        if (ctx.InUse.Count > 0)
        {
            var retry = ctx.InUse.ToList();
            ctx.InUse.Clear();
            bool allDone = true;
            foreach (var (full, rel, size) in retry)
            {
                if (!WaitForIdle(u)) return UnitResult.Aborted;
                var r = FileOps.CompressLzx(full, size, out long saved, out _);
                _monitor.AddAttributedIo(size + Math.Max(0, size - saved));
                if (r == CompressResult.Compressed) AddSaved(saved);
                else if (r is CompressResult.InUse or CompressResult.Retryable) allDone = false;
                else if (r is CompressResult.NotBeneficial or CompressResult.Failed)
                    TrySkipList(full, rel, r.ToString());
            }
            if (!allDone) return UnitResult.Incomplete;
            lock (_lock) _state.CurrentUnitIncomplete = false;
        }
        lock (_lock) return _state.CurrentUnitIncomplete ? UnitResult.Incomplete : UnitResult.Completed;
    }

    private void WalkDir(WalkCtx ctx, string full, string rel, string[]? resume, int depth)
    {
        var entries = FileOps.List(full, out int err);
        if (err != 0)
        {
            Log.Write($"Cannot list {full} (Win32 {err})");
            return;
        }
        foreach (var e in entries)
        {
            if (ctx.Aborted || ShouldAbort)
            {
                ctx.Aborted = true;
                return;
            }
            string childFull = Path.Combine(full, e.Name);
            string childRel = rel.Length == 0 ? e.Name : rel + "\\" + e.Name;

            if (resume != null && depth < resume.Length)
            {
                int cmp = StringComparer.OrdinalIgnoreCase.Compare(e.Name, resume[depth]);
                if (cmp < 0) continue;                    // already handled before the interruption
                if (cmp == 0)
                {
                    if (e.IsDirectory && depth < resume.Length - 1)
                    {
                        if (!e.IsReparse && ctx.Unit.Recursive) WalkDir(ctx, childFull, childRel, resume, depth + 1);
                    }
                    resume = null;                         // the last completed file itself, or its subtree finished
                    continue;
                }
                resume = null;                             // past the resume point
            }

            if (e.IsDirectory)
            {
                if (e.IsReparse || !ctx.Unit.Recursive) continue; // never follow junctions/symlinks
                WalkDir(ctx, childFull, childRel, null, depth + 1);
            }
            else
            {
                if (!ProcessFile(ctx, childFull, childRel, e))
                {
                    ctx.Aborted = true;
                    return;
                }
            }
        }
    }

    private bool ProcessFile(WalkCtx ctx, string full, string rel, in FileEntry e)
    {
        if (!WaitForIdle(ctx.Unit)) return false;
        lock (_lock) { _curFile = rel; _state.RunFilesProcessed++; }

        const uint skipAttrs = Native.FILE_ATTRIBUTE_COMPRESSED | Native.FILE_ATTRIBUTE_ENCRYPTED | Native.FILE_ATTRIBUTE_OFFLINE
            | Native.FILE_ATTRIBUTE_RECALL_ON_OPEN | Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Native.FILE_ATTRIBUTE_SPARSE_FILE;

        if (e.Size < ctx.MinSize || FileOps.IsWofCompressed(e) || e.IsReparse || (e.Attributes & skipAttrs) != 0
            || _skip.ShouldSkip(full, e.Size, e.MtimeTicks))
        {
            Advance(rel, false);
            return true;
        }

        var r = FileOps.CompressLzx(full, e.Size, out long saved, out string detail);
        _monitor.AddAttributedIo(e.Size + Math.Max(0, e.Size - saved));
        switch (r)
        {
            case CompressResult.Compressed:
                AddSaved(saved);
                break;
            case CompressResult.InUse:
            case CompressResult.Retryable:
                if (r == CompressResult.Retryable) Log.Write($"Will retry {rel}: {detail}");
                ctx.InUse.Add((full, rel, e.Size));
                lock (_lock) _state.CurrentUnitIncomplete = true;
                Advance(rel, true);
                return true;
            case CompressResult.NotBeneficial:
            case CompressResult.Failed:
                _skip.Add(full, e.Size, e.MtimeTicks, r + ": " + detail);
                if (r == CompressResult.Failed) Log.Write($"Skip-listed {rel}: {detail}");
                break;
        }
        Advance(rel, r == CompressResult.Compressed);
        return true;
    }

    private void TrySkipList(string full, string rel, string reason)
    {
        try
        {
            var fi = new FileInfo(full);
            if (fi.Exists) _skip.Add(full, fi.Length, fi.LastWriteTimeUtc.ToFileTimeUtc(), reason);
        }
        catch (Exception ex) { Log.Write($"Skip-list add failed for {rel}: {ex.Message}"); }
    }

    private void AddSaved(long saved)
    {
        lock (_lock)
        {
            _state.RunFilesCompressed++;
            _state.TotalFilesCompressed++;
            _state.RunBytesSaved += saved;
            _state.TotalBytesSaved += saved;
        }
    }

    /// <summary>Record progress; persist at most every 2 s (or immediately when forced).</summary>
    private void Advance(string rel, bool force)
    {
        lock (_lock)
        {
            _state.LastCompletedRelPath = rel;
            if (force || (DateTime.UtcNow - _lastStateSave).TotalSeconds >= 2)
            {
                _state.Save();
                _lastStateSave = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Blocks until conditions allow work. Returns false if the run must stop.</summary>
    private bool WaitForIdle(WorkUnit u)
    {
        int goodStreak = 0;
        bool waited = false;
        while (true)
        {
            if (ShouldAbort) return false;
            AppSettings s = Settings;
            string reason = "";
            if (_paused) reason = "Paused";
            // Locked session or screensaver: the PC isn't in use, so skip the CPU and drive checks and just run
            // (our own background/idle CPU and I/O priority still apply).
            else if (_monitor.LockedOrScreensaver)
            {
                SetPhase(Phase.Working, "");
                return true;
            }
            else if (_monitor.GameReason is { Length: > 0 } game) reason = game;
            else if (_monitor.OtherCpuSmoothed > s.CpuThresholdPercent) reason = $"CPU busy ({_monitor.OtherCpuSmoothed:0}%)";
            else
            {
                double d = _monitor.OtherDiskBytesPerSec(u.Library.VolumeRoot);
                if (d >= 0 && d > s.DriveThresholdMBps * 1048576.0) reason = $"Drive busy ({d / 1048576.0:0.0} MB/s other I/O)";
            }

            if (reason.Length == 0)
            {
                // After a wait, require ~5 s of continuous good conditions before resuming.
                if (!waited || ++goodStreak >= 5)
                {
                    SetPhase(Phase.Working, "");
                    return true;
                }
            }
            else
            {
                if (!waited || ReasonCategory(reason) != ReasonCategory(CurrentWaitReason)) Log.Write("Waiting: " + reason);
                if (!waited) lock (_lock) _state.Save(); // keep position durable while we sit
                waited = true;
                goodStreak = 0;
                SetPhase(_paused ? Phase.Paused : Phase.Waiting, reason);
            }
            _wake.WaitOne(1000);
        }
    }

    /// <summary>Wait reason without its live numbers ("CPU busy (63%)" -> "CPU busy"), so a changing value doesn't re-log.</summary>
    private static string ReasonCategory(string reason)
    {
        int i = reason.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? reason[..i] : reason;
    }
}
