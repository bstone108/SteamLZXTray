using System.Runtime.InteropServices;

namespace SteamLZXTray;

/// <summary>Worker loop, schedule, pass lifecycle (start, run, legacy reset, finish) and completion tracking.</summary>
internal sealed partial class Engine
{
    private static void ApplyLowPriority()
    {
        IntPtr t = Native.GetCurrentThread();
        bool bg = Native.SetThreadPriority(t, Native.THREAD_MODE_BACKGROUND_BEGIN); // very low I/O + low memory priority
        bool idle = Native.SetThreadPriority(t, Native.THREAD_PRIORITY_IDLE);
        var eco = new Native.THREAD_POWER_THROTTLING_STATE
        {
            Version = Native.THREAD_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = Native.THREAD_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = Native.THREAD_POWER_THROTTLING_EXECUTION_SPEED,
        };
        bool ecoOk = Native.SetThreadInformation(t, Native.ThreadPowerThrottling, ref eco, Marshal.SizeOf<Native.THREAD_POWER_THROTTLING_STATE>());
        int ioPrio = 0; // IoPriorityVeryLow
        int nt = Native.NtSetInformationProcess(Native.GetCurrentProcess(), Native.ProcessIoPriority, ref ioPrio, sizeof(int));
        Log.Write($"Worker priority: background-mode={bg}, idle-priority={idle}, EcoQoS={ecoOk}, process I/O VeryLow NTSTATUS=0x{nt:X8}");
    }

    private void WorkerMain()
    {
        ApplyLowPriority();
        Thread.Sleep(3000); // let library discovery + sampler warm up
        if (_state.ActiveRun == RunKind.None)
            RunGuarded("Startup ACF scan", () => ScanSteamManifests("Startup"));
        else
            Log.Write("Startup ACF scan skipped (a pass is already in progress; the pass snapshot and end-of-pass full scan will cover updates).");
        while (!_stop)
        {
            try
            {
                if (_manualRequested)
                {
                    _manualRequested = false;
                    StartRun(RunKind.Manual, null);
                }
                else if (_state.ActiveRun == RunKind.None)
                {
                    List<string>? triggered = null;
                    lock (_lock)
                    {
                        if (_state.PendingTriggerKeys.Count > 0)
                        {
                            triggered = _state.PendingTriggerKeys.ToList();
                            _state.PendingTriggerKeys.Clear();
                        }
                    }
                    if (triggered != null) StartRun(RunKind.Triggered, triggered);
                    else if (ScheduleDue()) StartRun(RunKind.Scheduled, null);
                }

                if (_state.ActiveRun != RunKind.None) ExecuteRun();
                else
                {
                    SetPhase(Phase.Idle, "");
                    _wake.WaitOne(TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception ex)
            {
                Log.Write("Worker error: " + ex);
                _wake.WaitOne(TimeSpan.FromSeconds(30));
            }
        }
    }

    private void StartRun(RunKind kind, List<string>? keys)
    {
        var snapshot = CaptureSteamSnapshot(Settings);
        lock (_lock)
        {
            _state.ActiveRun = kind;
            _state.RunStartedUtc = DateTime.UtcNow;
            _state.CurrentUnitKey = null;
            _state.LastCompletedRelPath = null;
            _state.CurrentUnitIncomplete = false;
            _state.TriggeredUnitKeys = keys ?? new List<string>();
            _state.RunBytesSaved = 0;
            _state.RunFilesProcessed = 0;
            _state.RunFilesCompressed = 0;
            _state.PassStartSnapshot = snapshot;
            _state.HasPassStartSnapshot = true;
            _state.IgnoreGameLogForPass = false;
            if (kind == RunKind.Scheduled) _state.LastScheduledRunUtc = DateTime.UtcNow;
            _state.Save();
        }
        Log.Write($"{kind} run started" + (keys != null ? $" ({keys.Count} queued game(s))" : "") + ".");
        Log.Write($"Pass snapshot: {snapshot.Count} game(s).");
    }

    private bool ScheduleDue()
    {
        AppSettings s = Settings;
        DateTime now = DateTime.Now;
        DateTime? last = _state.LastScheduledRunUtc?.ToLocalTime();
        switch (s.ScheduleMode)
        {
            case ScheduleMode.Daily:
                TimeSpan tod = s.DailyTimeOfDay();
                // Most recent scheduled occurrence <= now (today's, or yesterday's if today's time hasn't come).
                // A time missed while the app was off starts one pass on next launch, never more than one.
                DateTime occurrence = now.Date + tod;
                if (occurrence > now) occurrence = occurrence.AddDays(-1);
                return last == null || last < occurrence;
            case ScheduleMode.Interval:
                return last == null || now - last.Value >= TimeSpan.FromHours(Math.Max(1, s.IntervalHours));
            default:
                return false;
        }
    }

    /// <summary>Caller holds _lock (reads _settings directly).</summary>
    private string NextScheduleText()
    {
        var s = _settings;
        DateTime? last = _state.LastScheduledRunUtc?.ToLocalTime();
        switch (s.ScheduleMode)
        {
            case ScheduleMode.Daily:
                DateTime at = DateTime.Now.Date + s.DailyTimeOfDay();
                if (last != null && last >= at) at = at.AddDays(1);
                return at.ToString("ddd HH:mm");
            case ScheduleMode.Interval:
                return last == null ? "now" : last.Value.AddHours(Math.Max(1, s.IntervalHours)).ToString("ddd HH:mm");
            default:
                return "manual only";
        }
    }

    private enum UnitResult { Completed, Incomplete, Aborted }

    private bool ShouldAbort => _stop || _manualRequested;

    private void ExecuteRun()
    {
        if (!_state.HasPassStartSnapshot)
            RestartPassWithoutSnapshot();

        StopWatchers();

        var settings = Settings;
        var libs = LibraryDiscovery.Discover(settings);
        var units = LibraryDiscovery.BuildUnits(libs);
        _monitor.SetGameRoots(libs.Select(l => l.WalkRoot));
        foreach (var l in libs.Where(l => l.Supported)) _monitor.WatchDrive(l.VolumeRoot);
        lock (_lock)
        {
            foreach (var lib in libs) lib.CompletedCount = units.Count(u => u.Library == lib && IsUnitLogged(u));
            _libraries = libs;
        }
        LibrariesChanged?.Invoke();

        RunKind kind = _state.ActiveRun;
        units = units.Where(u => u.Acf == null || u.Acf.StateFlags == SteamGameState.FullyInstalled).ToList(); // only fully installed games
        if (kind == RunKind.Triggered)
        {
            var set = new HashSet<string>(_state.TriggeredUnitKeys, StringComparer.OrdinalIgnoreCase);
            units = units.Where(u => set.Contains(u.Key)).ToList();
        }

        int start = 0;
        string? resumeRel = null;
        if (_state.CurrentUnitKey != null)
        {
            int idx = units.FindIndex(u => u.Key.Equals(_state.CurrentUnitKey, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { start = idx; resumeRel = _state.LastCompletedRelPath; }
            else Log.Write($"Resume unit '{_state.CurrentUnitKey}' no longer exists; starting from the beginning.");
        }

        for (int i = start; i < units.Count; i++)
        {
            if (ShouldAbort) return;
            var u = units[i];
            bool resuming = i == start && resumeRel != null;

            // Triggered runs walk every queued game, even if the buildid in the game log already matches
            // (a verify/repair can rewrite files uncompressed without changing buildid).
            if (kind == RunKind.Scheduled && !_state.IgnoreGameLogForPass && !resuming && IsUnitCompleteVerified(u))
                continue;

            lock (_lock)
            {
                _state.CurrentUnitKey = u.Key;
                if (!resuming) { _state.LastCompletedRelPath = null; _state.CurrentUnitIncomplete = false; }
                _state.Save();
                _curLib = u.Library.Path;
                _curGame = u.DisplayName;
                _curFile = "";
                _curVolume = u.Library.VolumeRoot;
            }
            _lastStateSave = DateTime.UtcNow;
            Log.Write($"Walking {u.DisplayName} ({u.Root})" + (resuming ? $" - resuming after {resumeRel}" : ""));

            var result = WalkUnit(u, resuming ? resumeRel : null);
            if (result == UnitResult.Aborted)
            {
                lock (_lock) _state.Save();
                return;
            }
            if (result == UnitResult.Completed) MarkUnitComplete(u);
            else Log.Write($"{u.DisplayName}: some files were in use; will retry next pass.");
        }

        ScanAfterPass();

        lock (_lock)
        {
            Log.Write($"{kind} run finished: {_state.RunFilesProcessed} files checked, {_state.RunFilesCompressed} compressed, {Fmt.Bytes(_state.RunBytesSaved)} saved.");
            _state.ActiveRun = RunKind.None;
            _state.CurrentUnitKey = null;
            _state.LastCompletedRelPath = null;
            _state.TriggeredUnitKeys.Clear();
            _state.PassStartSnapshot.Clear();
            _state.HasPassStartSnapshot = false;
            _state.IgnoreGameLogForPass = false;
            _state.LastRunCompletedUtc = DateTime.UtcNow;
            _state.Save();
            _curLib = _curGame = _curFile = _curVolume = "";
        }
        // Refresh completion counts for the UI; this also turns live watching back on.
        RefreshLibraries();
        // Re-check ACFs once watchers are live, so a game that changed between the end-of-pass scan and
        // the watchers coming back on is not missed. Logs only when something is queued.
        RunGuarded("Post-pass ACF scan", () => ScanSteamManifests("Post-pass", quietIfUnchanged: true));
    }

    /// <summary>
    /// A resumed pass from an older build (or any state without a start snapshot) is fully reset once:
    /// skip list and this-pass counters/progress are cleared, a fresh snapshot is taken, and every
    /// game is walked again. The game log and all-time totals are kept; the game log is not used
    /// to skip games on this pass. Already-compressed files are still skipped quickly at the file level.
    /// The saved snapshot prevents this reset from running again after a later restart.
    /// </summary>
    private void RestartPassWithoutSnapshot()
    {
        Log.Write("Resuming a pass with no saved start snapshot; resetting this pass from scratch (skip list cleared, this-pass progress cleared, game log kept but not used to skip games) and taking a fresh snapshot.");
        _skip.Clear();
        var snapshot = CaptureSteamSnapshot(Settings);
        lock (_lock)
        {
            _state.CurrentUnitKey = null;
            _state.LastCompletedRelPath = null;
            _state.CurrentUnitIncomplete = false;
            _state.RunBytesSaved = 0;
            _state.RunFilesProcessed = 0;
            _state.RunFilesCompressed = 0;
            _state.RunStartedUtc = DateTime.UtcNow;
            _state.PassStartSnapshot = snapshot;
            _state.HasPassStartSnapshot = true;
            _state.IgnoreGameLogForPass = true;
            _state.Save();
        }
        Log.Write($"Pass snapshot: {snapshot.Count} game(s).");
    }

    // ------------------------------------------------------------------ completion tracking

    private bool IsUnitLogged(WorkUnit u)
    {
        if (u.Acf != null)
            return _gameLog.Steam.TryGetValue(u.Key, out var e) && e.BuildId == u.Acf.BuildId;
        return _gameLog.Folders.ContainsKey(u.Key);
    }

    private bool IsUnitCompleteVerified(WorkUnit u)
    {
        if (u.Acf != null)
        {
            if (u.Acf.StateFlags != SteamGameState.FullyInstalled) return true; // not fully installed / updating -> skip entirely
            lock (_lock) return IsUnitLogged(u);
        }
        FolderLogEntry? e;
        lock (_lock) _gameLog.Folders.TryGetValue(u.Key, out e);
        if (e == null) return false;
        var (mt, count) = Signature(u);
        return mt == e.LatestMtimeTicks && count == e.FileCount;
    }

    private void MarkUnitComplete(WorkUnit u)
    {
        if (u.Acf != null)
        {
            var now = AcfInfo.Read(u.Acf.ManifestPath);
            if (now == null || now.StateFlags != SteamGameState.FullyInstalled || now.BuildId != u.Acf.BuildId)
            {
                Log.Write($"{u.DisplayName}: manifest changed during walk; not marking complete.");
                return;
            }
            lock (_lock)
            {
                if (!IsUnitLogged(u)) BumpCompleted(u);
                _gameLog.Steam[u.Key] = new GameLogEntry { Name = u.DisplayName, BuildId = now.BuildId, Library = u.Library.Path, CompletedUtc = DateTime.UtcNow };
                _gameLog.Save();
            }
        }
        else
        {
            var (mt, count) = Signature(u);
            lock (_lock)
            {
                if (!IsUnitLogged(u)) BumpCompleted(u);
                _gameLog.Folders[u.Key] = new FolderLogEntry { LatestMtimeTicks = mt, FileCount = count, CompletedUtc = DateTime.UtcNow };
                _gameLog.Save();
            }
        }
        Log.Write($"{u.DisplayName}: complete.");
        LibrariesChanged?.Invoke(); // live refresh of the per-library and total counts
    }

    /// <summary>Caller holds _lock. Increments the completed count on the live library object shown in the UI.</summary>
    private void BumpCompleted(WorkUnit u)
    {
        var lib = _libraries.FirstOrDefault(l => ReferenceEquals(l, u.Library))
                  ?? _libraries.FirstOrDefault(l => l.Kind == u.Library.Kind && l.Path.Equals(u.Library.Path, StringComparison.OrdinalIgnoreCase));
        if (lib != null && lib.CompletedCount < lib.UnitCount) lib.CompletedCount++;
    }

    /// <summary>Quick signature: latest mtime (files and dirs) + file count.</summary>
    private static (long LatestMtimeTicks, long FileCount) Signature(WorkUnit u)
    {
        long latest = 0, count = 0;
        var stack = new Stack<string>();
        stack.Push(u.Root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            foreach (var e in FileOps.List(dir, out _))
            {
                if (e.IsDirectory)
                {
                    if (!u.Recursive || e.IsReparse) continue;
                    if (e.MtimeTicks > latest) latest = e.MtimeTicks;
                    stack.Push(Path.Combine(dir, e.Name));
                }
                else
                {
                    count++;
                    if (e.MtimeTicks > latest) latest = e.MtimeTicks;
                }
            }
        }
        return (latest, count);
    }

}
