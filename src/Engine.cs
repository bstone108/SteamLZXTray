namespace SteamLZXTray;

/// <summary>
/// Background compression engine. One worker thread runs passes (scheduled, Steam-update-triggered or Run Now),
/// gated per file by <see cref="IdleMonitor"/>. All mutable run state lives in <see cref="RunState"/> and is
/// guarded by <c>_lock</c>.
/// </summary>
/// <remarks>
/// The class is split across files:
/// Engine.cs (state, public API, status), Engine.Steam.cs (library discovery, ACF watchers and scans),
/// Engine.Run.cs (worker loop, schedule, pass lifecycle, completion tracking) and Engine.Walk.cs (file walking,
/// compression and idle waits).
/// </remarks>
internal sealed partial class Engine : IDisposable
{
    private readonly object _lock = new();
    private readonly RunState _state;
    private readonly GameLog _gameLog;
    private readonly SkipList _skip = new();
    private readonly IdleMonitor _monitor = new();
    private readonly Thread _worker;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop, _paused, _manualRequested;
    private AppSettings _settings;
    private List<LibraryInfo> _libraries = new();

    // Live status shown in the UI (guarded by _lock).
    private Phase _phase = Phase.Idle;
    private string _waitReason = "", _curLib = "", _curGame = "", _curFile = "", _curVolume = "";

    public event Action? LibrariesChanged;

    public Engine(AppSettings settings)
    {
        _settings = settings;
        _state = RunState.Load(Log.Write);
        _gameLog = GameLog.Load(Log.Write);
        _skip.Load(Log.Write);
        _monitor.TreatFullscreenAsGame = settings.TreatFullscreenAsGame;
        _debounceTimer = new System.Threading.Timer(_ => RunGuarded("Steam manifest check", ProcessDebouncedManifests),
            null, Timeout.Infinite, Timeout.Infinite);
        LogResumeState();
        _worker = new Thread(WorkerMain) { IsBackground = true, Name = "LZXWorker" };
    }

    private void LogResumeState()
    {
        if (_state.ActiveRun == RunKind.None) return;
        string queued = _state.PendingTriggerKeys.Count > 0 ? $"; {_state.PendingTriggerKeys.Count} Steam update(s) queued" : "";
        if (!_state.HasPassStartSnapshot)
            Log.Write($"Pass in progress ({_state.ActiveRun}) found in state with no start snapshot; it will be reset from scratch with a fresh snapshot{queued}.");
        else
            Log.Write($"Pass in progress ({_state.ActiveRun}) found in state; resuming immediately at '{_state.CurrentUnitKey}' after '{_state.LastCompletedRelPath}'{queued}" +
                      $"; reusing start snapshot of {_state.PassStartSnapshot.Count} game(s).");
    }

    public void Start()
    {
        ThreadPool.QueueUserWorkItem(_ => RefreshLibraries());
        _worker.Start();
    }

    // ------------------------------------------------------------------ public API

    public AppSettings Settings { get { lock (_lock) return _settings; } }
    public bool Paused => _paused;
    public List<LibraryInfo> Libraries { get { lock (_lock) return _libraries.ToList(); } }

    public void ApplySettings(AppSettings s)
    {
        bool watchTurnedOn;
        lock (_lock)
        {
            watchTurnedOn = s.WatchSteamUpdates && !_settings.WatchSteamUpdates && _state.ActiveRun == RunKind.None;
            _settings = s;
        }
        _monitor.TreatFullscreenAsGame = s.TreatFullscreenAsGame;
        s.Save();
        Log.Write("Settings saved.");
        ThreadPool.QueueUserWorkItem(_ =>
        {
            RefreshLibraries();
            if (watchTurnedOn) RunGuarded("Watch enabled ACF scan", () => ScanSteamManifests("Watch enabled"));
        });
        _wake.Set();
    }

    public void RunNow()
    {
        _manualRequested = true;
        _paused = false;
        Log.Write("Run Now requested (full walk, game log ignored).");
        _wake.Set();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Log.Write(paused ? "Paused by user." : "Resumed by user.");
        _wake.Set();
    }

    public void ClearSkipList()
    {
        _skip.Clear();
        Log.Write("Skip list cleared.");
    }

    public void ResetGameLog()
    {
        lock (_lock)
        {
            _gameLog.Steam.Clear();
            _gameLog.Folders.Clear();
            _gameLog.Save();
        }
        Log.Write("Game/folder log reset.");
        ThreadPool.QueueUserWorkItem(_ => RefreshLibraries());
    }

    public void Rescan() => ThreadPool.QueueUserWorkItem(_ => RefreshLibraries());

    public EngineStatus GetStatus()
    {
        var st = new EngineStatus();
        string volume;
        lock (_lock)
        {
            st.Phase = _paused ? Phase.Paused : _phase;
            st.WaitReason = _waitReason;
            st.Library = _curLib;
            st.Game = _curGame;
            st.File = _curFile;
            st.Run = _state.ActiveRun;
            st.RunFilesProcessed = _state.RunFilesProcessed;
            st.RunFilesCompressed = _state.RunFilesCompressed;
            st.RunBytesSaved = _state.RunBytesSaved;
            st.TotalBytesSaved = _state.TotalBytesSaved;
            st.TotalFilesCompressed = _state.TotalFilesCompressed;
            st.PendingTriggers = _state.PendingTriggerKeys.Count;
            st.LastRunCompletedUtc = _state.LastRunCompletedUtc;
            st.NextSchedule = NextScheduleText();
            volume = _curVolume;
        }
        st.SkipListCount = _skip.Count;
        st.CpuOther = _monitor.OtherCpuSmoothed;
        if (volume.Length > 0)
        {
            double other = _monitor.OtherDiskBytesPerSec(volume);
            st.DiskOtherMBps = other < 0 ? -1 : other / 1048576.0;
            double total = _monitor.TotalDiskBytesPerSec(volume);
            st.DiskTotalMBps = total < 0 ? -1 : total / 1048576.0;
        }
        return st;
    }

    /// <summary>Completed / total units across supported libraries (from gamelog.json at startup, live afterwards).</summary>
    public (int Done, int Total) CompletedTotals()
    {
        lock (_lock)
        {
            var supported = _libraries.Where(l => l.Supported).ToList();
            return (supported.Sum(l => l.CompletedCount), supported.Sum(l => l.UnitCount));
        }
    }

    /// <summary>Flush run state now (Windows shutdown / logoff).</summary>
    public void SaveNow()
    {
        lock (_lock)
        {
            try { _state.Save(); } catch { /* best effort during shutdown */ }
        }
    }

    // ------------------------------------------------------------------ helpers

    private void SetPhase(Phase phase, string reason)
    {
        lock (_lock) { _phase = phase; _waitReason = reason; }
    }

    private string CurrentWaitReason { get { lock (_lock) return _waitReason; } }

    /// <summary>Runs work on a thread-pool or timer thread; an exception is logged instead of ending the process.</summary>
    private static void RunGuarded(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Write($"{what} failed: {ex}"); }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        // The worker stops at the next file boundary. A large file may still be compressing inside the kernel call,
        // which can't be interrupted; Windows finishes it before the process can end.
        if (_worker.IsAlive)
        {
            string current;
            lock (_lock) current = _curFile;
            Log.Write($"Exit: stopping after the current file{(current.Length > 0 ? " (" + current + ")" : "")}...");
            if (!_worker.Join(5000))
                Log.Write("Exit: still finishing the current file; the process ends when Windows completes it.");
        }
        SaveNow();
        DisposeWatchers();
        _debounceTimer.Dispose();
        _monitor.Dispose();
    }
}
