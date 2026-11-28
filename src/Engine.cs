using System.Runtime.InteropServices;

namespace SteamLZXTray;

internal enum Phase { Idle, Working, Waiting, Paused }

internal sealed class EngineStatus
{
    public Phase Phase;
    public string WaitReason = "";
    public RunKind Run;
    public string Library = "", Game = "", File = "";
    public long RunFilesProcessed, RunFilesCompressed, RunBytesSaved, TotalBytesSaved, TotalFilesCompressed;
    public int SkipListCount, PendingTriggers;
    public double CpuOther, DiskOtherMBps = -1, DiskTotalMBps = -1;
    public DateTime? LastRunCompletedUtc, LastScheduledRunUtc;
    public string NextSchedule = "";
}

internal sealed class Engine : IDisposable
{
    private readonly object _lock = new();
    private AppSettings _settings;
    private readonly RunState _state;
    private readonly GameLog _gameLog;
    private readonly SkipList _skip = new();
    private readonly IdleMonitor _monitor = new();
    private readonly Thread _worker;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop, _paused, _manualRequested;
    private List<LibraryInfo> _libraries = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _debounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _scanLock = new();
    private readonly System.Threading.Timer _debounceTimer;
    private DateTime _lastStateSave = DateTime.MinValue;

    // live status (guarded by _lock)
    private Phase _phase = Phase.Idle;
    private string _waitReason = "", _curLib = "", _curGame = "", _curFile = "";

    public event Action? LibrariesChanged;

    public Engine(AppSettings settings)
    {
        _settings = settings;
        _state = RunState.Load(Log.Write);
        _gameLog = GameLog.Load(Log.Write);
        _skip.Load(Log.Write);
        _monitor.TreatFullscreenAsGame = settings.TreatFullscreenAsGame;
        _debounceTimer = new System.Threading.Timer(_ => ProcessDebouncedManifests(), null, Timeout.Infinite, Timeout.Infinite);
        if (_state.ActiveRun != RunKind.None)
        {
            if (!_state.HasPassStartSnapshot)
                Log.Write($"Pass in progress ({_state.ActiveRun}) found in state with no start snapshot; it will be reset from scratch with a fresh snapshot" +
                          (_state.PendingTriggerKeys.Count > 0 ? $"; {_state.PendingTriggerKeys.Count} Steam update(s) queued" : "") + ".");
            else
                Log.Write($"Pass in progress ({_state.ActiveRun}) found in state; resuming immediately at '{_state.CurrentUnitKey}' after '{_state.LastCompletedRelPath}'" +
                          (_state.PendingTriggerKeys.Count > 0 ? $"; {_state.PendingTriggerKeys.Count} Steam update(s) queued" : "") +
                          $"; reusing start snapshot of {_state.PassStartSnapshot.Count} game(s).");
        }
        _worker = new Thread(WorkerMain) { IsBackground = true, Name = "LZXWorker" };
    }

    public void Start()
    {
        ThreadPool.QueueUserWorkItem(_ => RefreshLibraries());
        _worker.Start();
    }

    public AppSettings Settings { get { lock (_lock) return _settings; } }
    public bool Paused => _paused;

    public void ApplySettings(AppSettings s)
    {
        bool watchOn;
        lock (_lock)
        {
            bool was = _settings.WatchSteamUpdates;
            _settings = s;
            watchOn = s.WatchSteamUpdates && !was && _state.ActiveRun == RunKind.None;
        }
        _monitor.TreatFullscreenAsGame = s.TreatFullscreenAsGame;
        s.Save();
        Log.Write("Settings saved.");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            RefreshLibraries();
            if (watchOn) ScanSteamManifests("Watch enabled");
        });
        _wake.Set();
    }

    public void RunNow() { _manualRequested = true; _paused = false; Log.Write("Run Now requested (full walk, game log ignored)."); _wake.Set(); }
    public void SetPaused(bool p) { _paused = p; Log.Write(p ? "Paused by user." : "Resumed by user."); _wake.Set(); }
    public void ClearSkipList() { _skip.Clear(); Log.Write("Skip list cleared."); }
    public void ResetGameLog()
    {
        lock (_lock) { _gameLog.Steam.Clear(); _gameLog.Folders.Clear(); _gameLog.Save(); }
        Log.Write("Game/folder log reset.");
        ThreadPool.QueueUserWorkItem(_ => RefreshLibraries());
    }
    public void Rescan() => ThreadPool.QueueUserWorkItem(_ => RefreshLibraries());

    public List<LibraryInfo> Libraries { get { lock (_lock) return _libraries.ToList(); } }

    public EngineStatus GetStatus()
    {
        var st = new EngineStatus();
        lock (_lock)
        {
            st.Phase = _paused && _phase != Phase.Idle ? Phase.Paused : _phase;
            if (_paused && _phase == Phase.Idle) st.Phase = Phase.Paused;
            st.WaitReason = _waitReason; st.Library = _curLib; st.Game = _curGame; st.File = _curFile;
            st.Run = _state.ActiveRun;
            st.RunFilesProcessed = _state.RunFilesProcessed; st.RunFilesCompressed = _state.RunFilesCompressed;
            st.RunBytesSaved = _state.RunBytesSaved; st.TotalBytesSaved = _state.TotalBytesSaved;
            st.TotalFilesCompressed = _state.TotalFilesCompressed;
            st.PendingTriggers = _state.PendingTriggerKeys.Count;
            st.LastRunCompletedUtc = _state.LastRunCompletedUtc; st.LastScheduledRunUtc = _state.LastScheduledRunUtc;
            st.NextSchedule = NextScheduleText();
        }
        st.SkipListCount = _skip.Count;
        st.CpuOther = _monitor.OtherCpuSmoothed;
        string vol = "";
        lock (_lock) vol = _curVolume;
        if (vol.Length > 0)
        {
            var o = _monitor.OtherDiskBytesPerSec(vol);
            st.DiskOtherMBps = o < 0 ? -1 : o / 1048576.0;
            var t = _monitor.TotalDiskBytesPerSec(vol);
            st.DiskTotalMBps = t < 0 ? -1 : t / 1048576.0;
        }
        return st;
    }
    private string _curVolume = "";

    // ------------------------------------------------------------------ libraries / watchers

    private void RefreshLibraries()
    {
        try
        {
            AppSettings s = Settings;
            var libs = LibraryDiscovery.Discover(s);
            var units = LibraryDiscovery.BuildUnits(libs);
            lock (_lock)
                foreach (var lib in libs)
                    lib.CompletedCount = units.Count(u => u.Library == lib && IsUnitLogged(u));
            foreach (var lib in libs)
                Log.Write($"Library [{lib.Kind}] {lib.Path}: {lib.Status}" + (lib.Supported ? $" ({lib.UnitCount} units, {lib.CompletedCount} done)" : ""));
            _monitor.SetGameRoots(libs.Select(l => l.WalkRoot));
            foreach (var l in libs.Where(l => l.Supported)) _monitor.WatchDrive(l.VolumeRoot);
            lock (_lock) _libraries = libs;
            SetupWatchers(libs, s.WatchSteamUpdates);
            LibrariesChanged?.Invoke();
        }
        catch (Exception ex) { Log.Write("Library refresh failed: " + ex); }
    }

    private void SetupWatchers(List<LibraryInfo> libs, bool enabled)
    {
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            if (!enabled) return;
            // Live watching stays off during a pass; a full ACF scan runs at the end before watchers come back on.
            if (_state.ActiveRun != RunKind.None) return;
            foreach (var lib in libs.Where(l => l.Kind == LibraryKind.Steam && l.Supported && Directory.Exists(l.SteamAppsDir)))
            {
                try
                {
                    var w = new FileSystemWatcher(lib.SteamAppsDir, "appmanifest_*.acf")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        IncludeSubdirectories = false,
                        InternalBufferSize = 64 * 1024,
                    };
                    w.Changed += (_, e) => OnManifest(e.FullPath);
                    w.Created += (_, e) => OnManifest(e.FullPath);
                    w.Renamed += (_, e) => OnManifest(e.FullPath);
                    w.Error += (_, e) => OnWatcherError(e.GetException());
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex) { Log.Write($"Cannot watch {lib.SteamAppsDir}: {ex.Message}"); }
            }
            Log.Write($"Watching {_watchers.Count} steamapps folder(s) for Steam updates.");
        }
    }

    private void OnManifest(string path)
    {
        if (!path.EndsWith(".acf", StringComparison.OrdinalIgnoreCase)) return;
        lock (_debounce) _debounce.Add(path);
        _debounceTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
    }

    private void OnWatcherError(Exception ex)
    {
        Log.Write("Manifest watcher error: " + ex.Message);
        if (!Settings.WatchSteamUpdates) return;
        if (_state.ActiveRun != RunKind.None)
        {
            Log.Write("Watcher error during a pass; the end-of-pass full ACF scan will catch any missed updates.");
            return;
        }
        ThreadPool.QueueUserWorkItem(_ => ScanSteamManifests("Watcher error"));
    }

    private void StopWatchers()
    {
        lock (_watchers)
        {
            if (_watchers.Count == 0) return;
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            Log.Write("Steam update watchers paused until this pass ends.");
        }
    }

    private void ProcessDebouncedManifests()
    {
        string[] paths;
        lock (_debounce) { paths = _debounce.ToArray(); _debounce.Clear(); }
        bool queued = false;
        foreach (var p in paths)
        {
            var acf = AcfInfo.Read(p);
            if (acf == null) continue;
            string libPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(p)!, "..")).TrimEnd('\\');
            string key = LibraryDiscovery.SteamKey(libPath, acf.AppId);
            var now = PassGameSnapshot.FromAcf(acf);
            bool add = false;
            lock (_lock)
            {
                // Leftover events after a pass starts are left for the end-of-pass full scan.
                if (_state.ActiveRun != RunKind.None) continue;
                bool knownSame = _state.HasLastKnownSteamState
                    && _state.LastKnownSteamState.TryGetValue(key, out var prev) && prev.SameAs(now);
                if (_state.HasLastKnownSteamState)
                    _state.LastKnownSteamState[key] = now;
                if (acf.StateFlags != 4) { _state.Save(); continue; } // still updating; a later write will trigger again
                if (knownSame) { _state.Save(); continue; }
                // No last-known baseline yet: fall back to the game-log buildid (legacy watcher behavior).
                if (!_state.HasLastKnownSteamState
                    && _gameLog.Steam.TryGetValue(key, out var e) && e.BuildId == acf.BuildId)
                { _state.Save(); continue; }
                if (_state.PendingTriggerKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) { _state.Save(); continue; }
                _state.PendingTriggerKeys.Add(key);
                _state.Save();
                add = true;
            }
            if (add)
            {
                Log.Write($"Steam update detected: {acf.Name} (build {acf.BuildId}) queued.");
                queued = true;
            }
        }
        if (queued) _wake.Set();
    }

    // ------------------------------------------------------------------ worker

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
            ScanSteamManifests("Startup");
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
                    List<string>? trig = null;
                    lock (_lock)
                        if (_state.PendingTriggerKeys.Count > 0) { trig = _state.PendingTriggerKeys.ToList(); _state.PendingTriggerKeys.Clear(); }
                    if (trig != null) StartRun(RunKind.Triggered, trig);
                    else if (ScheduleDue()) StartRun(RunKind.Scheduled, null);
                }

                if (_state.ActiveRun != RunKind.None) ExecuteRun();
                else { SetPhase(Phase.Idle, ""); _wake.WaitOne(TimeSpan.FromSeconds(30)); }
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
            _state.RunBytesSaved = 0; _state.RunFilesProcessed = 0; _state.RunFilesCompressed = 0;
            _state.PassStartSnapshot = snapshot;
            _state.HasPassStartSnapshot = true;
            _state.IgnoreGameLogForPass = false;
            if (kind == RunKind.Scheduled) _state.LastScheduledRunUtc = DateTime.UtcNow;
            _state.Save();
        }
        Log.Write($"{kind} run started" + (keys != null ? $" ({keys.Count} queued game(s))" : "") + ".");
        Log.Write($"Pass snapshot: {snapshot.Count} game(s).");
    }

    /// <summary>Fingerprint every Steam game in supported libraries (including ones not fully installed).</summary>
    private static Dictionary<string, PassGameSnapshot> CaptureSteamSnapshot(AppSettings settings)
    {
        var snap = new Dictionary<string, PassGameSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, acf) in LibraryDiscovery.ReadSupportedSteamAcfs(LibraryDiscovery.Discover(settings)))
            snap[key] = PassGameSnapshot.FromAcf(acf);
        return snap;
    }

    private bool ScheduleDue()
    {
        AppSettings s = Settings;
        DateTime now = DateTime.Now;
        DateTime? last = _state.LastScheduledRunUtc?.ToLocalTime();
        switch (s.ScheduleMode)
        {
            case ScheduleMode.Daily:
                if (!TimeSpan.TryParse(s.DailyTime, out var tod)) tod = TimeSpan.FromHours(3);
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

    private string NextScheduleText()
    {
        var s = _settings;
        DateTime? last = _state.LastScheduledRunUtc?.ToLocalTime();
        switch (s.ScheduleMode)
        {
            case ScheduleMode.Daily:
                if (!TimeSpan.TryParse(s.DailyTime, out var tod)) tod = TimeSpan.FromHours(3);
                DateTime at = DateTime.Now.Date + tod;
                if (last != null && last >= at) at = at.AddDays(1);
                return at.ToString("ddd HH:mm");
            case ScheduleMode.Interval:
                return last == null ? "now" : last.Value.AddHours(Math.Max(1, s.IntervalHours)).ToString("ddd HH:mm");
            default: return "manual only";
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
        units = units.Where(u => u.Acf == null || u.Acf.StateFlags == 4).ToList(); // only fully installed games
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
                _curLib = u.Library.Path; _curGame = u.DisplayName; _curFile = ""; _curVolume = u.Library.VolumeRoot;
            }
            _lastStateSave = DateTime.UtcNow;
            Log.Write($"Walking {u.DisplayName} ({u.Root})" + (resuming ? $" - resuming after {resumeRel}" : ""));

            var r = WalkUnit(u, resuming ? resumeRel : null);
            if (r == UnitResult.Aborted) { lock (_lock) _state.Save(); return; }
            if (r == UnitResult.Completed) MarkUnitComplete(u);
            else Log.Write($"{u.DisplayName}: some files were in use; will retry next pass.");
        }

        QueueSnapshotChanges();

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
            _curLib = _curGame = _curFile = ""; _curVolume = "";
        }
        // Refresh completion counts for the UI.
        RefreshLibraries();
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

    /// <summary>
    /// After a finished pass, full scan of every ACF before live watching is turned back on.
    /// Compares against the pass-start snapshot and last known state, and queues any fully
    /// installed game that changed or is new. Still-updating games (StateFlags != 4) are
    /// left for the live watcher to pick up when they finish.
    /// </summary>
    private void QueueSnapshotChanges()
    {
        var current = CaptureSteamSnapshot(Settings);
        Dictionary<string, PassGameSnapshot>? start = null;
        Dictionary<string, PassGameSnapshot>? known = null;
        bool knownSeeded;
        lock (_lock)
        {
            if (_state.HasPassStartSnapshot)
                start = new Dictionary<string, PassGameSnapshot>(_state.PassStartSnapshot, StringComparer.OrdinalIgnoreCase);
            knownSeeded = _state.HasLastKnownSteamState;
            if (knownSeeded)
                known = new Dictionary<string, PassGameSnapshot>(_state.LastKnownSteamState, StringComparer.OrdinalIgnoreCase);
        }

        if (!Settings.WatchSteamUpdates)
        {
            Log.Write("End-of-pass ACF scan skipped (Watch for Steam updates is off).");
            RememberSteamState(current);
            return;
        }

        var queued = QueueChangedGames(current, start, known, knownSeeded, "End-of-pass scan");
        RememberSteamState(current);
        if (queued.Count > 0)
            Log.Write($"End-of-pass ACF scan: {queued.Count} game(s) queued for another pass: {string.Join(", ", queued)}.");
        else
            Log.Write("End-of-pass ACF scan: no games changed or were added during the pass.");
    }

    /// <summary>
    /// Full ACF scan against last known state (startup, watcher buffer overflow, or watching turned on).
    /// Skipped when a pass is in progress — the end-of-pass scan covers that.
    /// First run with no baseline records current state without queueing every game.
    /// </summary>
    private void ScanSteamManifests(string reason)
    {
        lock (_scanLock)
        {
            if (!Settings.WatchSteamUpdates)
            {
                Log.Write($"{reason} ACF scan skipped (Watch for Steam updates is off).");
                return;
            }
            if (_state.ActiveRun != RunKind.None)
            {
                Log.Write($"{reason} ACF scan skipped (a pass is in progress; the end-of-pass full scan will cover updates).");
                return;
            }

            var current = CaptureSteamSnapshot(Settings);
            bool seeded;
            Dictionary<string, PassGameSnapshot> known;
            lock (_lock)
            {
                if (_state.ActiveRun != RunKind.None)
                {
                    Log.Write($"{reason} ACF scan skipped (a pass is in progress; the end-of-pass full scan will cover updates).");
                    return;
                }
                seeded = _state.HasLastKnownSteamState;
                known = new Dictionary<string, PassGameSnapshot>(_state.LastKnownSteamState, StringComparer.OrdinalIgnoreCase);
            }

            if (!seeded)
            {
                RememberSteamState(current);
                Log.Write($"{reason} ACF scan: recorded {current.Count} game(s) as last known state (no prior baseline).");
                return;
            }

            var queued = QueueChangedGames(current, passSnapshot: null, known, lastKnownSeeded: true, reason + " scan");
            RememberSteamState(current);
            if (queued.Count > 0)
            {
                Log.Write($"{reason} ACF scan: {queued.Count} game(s) queued: {string.Join(", ", queued)}.");
                _wake.Set();
            }
            else
                Log.Write($"{reason} ACF scan: {current.Count} game(s) checked, none changed.");
        }
    }

    private List<string> QueueChangedGames(
        Dictionary<string, PassGameSnapshot> current,
        Dictionary<string, PassGameSnapshot>? passSnapshot,
        Dictionary<string, PassGameSnapshot>? lastKnown,
        bool lastKnownSeeded,
        string logPrefix)
    {
        var queued = new List<string>();
        foreach (var (key, now) in current)
        {
            if (now.StateFlags != 4) continue; // still updating; the watcher will queue when it finishes
            string? why = null;
            if (passSnapshot != null)
            {
                if (!passSnapshot.TryGetValue(key, out var snap)) why = "installed during the pass";
                else if (!snap.SameAs(now)) why = "changed during the pass";
            }
            if (why == null && lastKnownSeeded && lastKnown != null)
            {
                if (!lastKnown.TryGetValue(key, out var prev)) why = "new";
                else if (!prev.SameAs(now)) why = "changed since last known state";
            }
            if (why == null) continue;
            lock (_lock)
            {
                if (_state.PendingTriggerKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                _state.PendingTriggerKeys.Add(key);
            }
            string label = string.IsNullOrEmpty(now.Name) ? key : now.Name;
            Log.Write($"{logPrefix}: {label} (build {now.BuildId}) {why}; queued.");
            queued.Add(label);
        }
        if (queued.Count > 0)
            lock (_lock) _state.Save();
        return queued;
    }

    private void RememberSteamState(Dictionary<string, PassGameSnapshot> current)
    {
        lock (_lock)
        {
            _state.LastKnownSteamState = new Dictionary<string, PassGameSnapshot>(current, StringComparer.OrdinalIgnoreCase);
            _state.HasLastKnownSteamState = true;
            _state.Save();
        }
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
            if (u.Acf.StateFlags != 4) return true; // not fully installed / updating -> skip entirely
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
            if (now == null || now.StateFlags != 4 || now.BuildId != u.Acf.BuildId)
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

    /// <summary>Completed / total units across supported libraries (from gamelog.json at startup, live afterwards).</summary>
    public (int Done, int Total) CompletedTotals()
    {
        lock (_lock) return (_libraries.Where(l => l.Supported).Sum(l => l.CompletedCount), _libraries.Where(l => l.Supported).Sum(l => l.UnitCount));
    }

    /// <summary>Quick signature: latest mtime (files and dirs) + file count.</summary>
    private static (long, long) Signature(WorkUnit u)
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

    // ------------------------------------------------------------------ walking

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
            lock (_lock) { _state.CurrentUnitIncomplete = false; }
        }
        return _state.CurrentUnitIncomplete ? UnitResult.Incomplete : UnitResult.Completed;
    }

    private void WalkDir(WalkCtx ctx, string full, string rel, string[]? resume, int depth)
    {
        var entries = FileOps.List(full, out int err);
        if (err != 0) { Log.Write($"Cannot list {full} (Win32 {err})"); return; }
        foreach (var e in entries)
        {
            if (ctx.Aborted || ShouldAbort) { ctx.Aborted = true; return; }
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
                if (!ProcessFile(ctx, childFull, childRel, e)) { ctx.Aborted = true; return; }
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
                lock (_lock) { _state.CurrentUnitIncomplete = true; }
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
            _state.RunFilesCompressed++; _state.TotalFilesCompressed++;
            _state.RunBytesSaved += saved; _state.TotalBytesSaved += saved;
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
            else if (_monitor.LockedOrScreensaver) { SetPhase(Phase.Working, ""); return true; }
            else if (_monitor.GameInForeground) reason = _monitor.GameReason;
            else if (_monitor.OtherCpuSmoothed > s.CpuThresholdPercent) reason = $"CPU busy ({_monitor.OtherCpuSmoothed:0}%)";
            else
            {
                double d = _monitor.OtherDiskBytesPerSec(u.Library.VolumeRoot);
                if (d >= 0 && d > s.DriveThresholdMBps * 1048576.0) reason = $"Drive busy ({d / 1048576.0:0.0} MB/s other I/O)";
            }

            if (reason.Length == 0)
            {
                // After a wait, require ~5 s of continuous good conditions before resuming.
                if (!waited || ++goodStreak >= 5) { SetPhase(Phase.Working, ""); return true; }
            }
            else
            {
                static string Cat(string r) { int i = r.IndexOf(" (", StringComparison.Ordinal); return i > 0 ? r[..i] : r; }
                if (!waited || Cat(reason) != Cat(CurrentWaitReason)) Log.Write("Waiting: " + reason);
                if (!waited) lock (_lock) _state.Save(); // keep position durable while we sit
                waited = true;
                goodStreak = 0;
                SetPhase(_paused ? Phase.Paused : Phase.Waiting, reason);
            }
            _wake.WaitOne(1000);
        }
    }

    private string CurrentWaitReason { get { lock (_lock) return _waitReason; } }

    private void SetPhase(Phase p, string reason)
    {
        lock (_lock) { _phase = p; _waitReason = reason; }
    }

    /// <summary>Flush run state now (Windows shutdown / logoff).</summary>
    public void SaveNow() { lock (_lock) { try { _state.Save(); } catch { } } }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        // The worker stops at the next file boundary. A large file may still be compressing inside the kernel call,
        // which can't be interrupted; Windows finishes it before the process can end.
        if (_worker.IsAlive)
        {
            string cur; lock (_lock) { cur = _curFile ?? ""; }
            Log.Write($"Exit: stopping after the current file{(cur.Length > 0 ? " (" + cur + ")" : "")}...");
            if (!_worker.Join(5000))
                Log.Write("Exit: still finishing the current file; the process ends when Windows completes it.");
        }
        lock (_lock) { try { _state.Save(); } catch { } }
        lock (_watchers) { foreach (var w in _watchers) w.Dispose(); _watchers.Clear(); }
        _debounceTimer.Dispose();
        _monitor.Dispose();
    }
}

internal static class Fmt
{
    public static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
