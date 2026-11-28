namespace SteamLZXTray;

/// <summary>Library discovery, Steam manifest (ACF) watchers and the full ACF scans that back them up.</summary>
internal sealed partial class Engine
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _debounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _debounceTimer;
    /// <summary>Serializes everything that reads and rewrites the last-known ACF state (watcher batches and full scans).</summary>
    private readonly object _scanLock = new();

    private bool PassActive { get { lock (_lock) return _state.ActiveRun != RunKind.None; } }

    // ------------------------------------------------------------------ libraries

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

    // ------------------------------------------------------------------ live watchers

    private void SetupWatchers(List<LibraryInfo> libs, bool enabled)
    {
        lock (_watchers)
        {
            DisposeWatchers();
            if (!enabled) return;
            // Live watching stays off during a pass; a full ACF scan runs at the end before watchers come back on.
            if (PassActive) return;
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

    private void StopWatchers()
    {
        lock (_watchers)
        {
            if (_watchers.Count == 0) return;
            DisposeWatchers();
            Log.Write("Steam update watchers paused until this pass ends.");
        }
    }

    private void DisposeWatchers()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
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
        if (PassActive)
        {
            Log.Write("Watcher error during a pass; the end-of-pass full ACF scan will catch any missed updates.");
            return;
        }
        ThreadPool.QueueUserWorkItem(_ => RunGuarded("Watcher error ACF scan", () => ScanSteamManifests("Watcher error")));
    }

    /// <summary>Handles a debounced batch of watcher hits (timer thread).</summary>
    private void ProcessDebouncedManifests()
    {
        string[] paths;
        lock (_debounce) { paths = _debounce.ToArray(); _debounce.Clear(); }
        bool queued = false;
        lock (_scanLock)
        {
            foreach (var path in paths)
            {
                var acf = AcfInfo.Read(path);
                if (acf == null) continue;
                string libPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..")).TrimEnd('\\');
                string key = LibraryDiscovery.SteamKey(libPath, acf.AppId);
                if (TryQueueWatcherHit(key, acf))
                {
                    Log.Write($"Steam update detected: {acf.Name} (build {acf.BuildId}) queued.");
                    queued = true;
                }
            }
        }
        if (queued) _wake.Set();
    }

    /// <summary>Records the game's new ACF state and queues it if it is fully installed and changed. Returns true if queued.</summary>
    private bool TryQueueWatcherHit(string key, AcfInfo acf)
    {
        var now = SteamGameState.FromAcf(acf);
        lock (_lock)
        {
            // Leftover events after a pass starts are left for the end-of-pass full scan.
            if (_state.ActiveRun != RunKind.None) return false;

            bool seeded = _state.HasLastKnownSteamState;
            bool knownSame = seeded && _state.LastKnownSteamState.TryGetValue(key, out var prev) && prev.SameAs(now);
            if (seeded) _state.LastKnownSteamState[key] = now;

            bool queue = acf.StateFlags == SteamGameState.FullyInstalled // still updating: a later write will trigger again
                         && !knownSame
                         // No last-known baseline yet: fall back to the game-log buildid (legacy watcher behavior).
                         && !(!seeded && _gameLog.Steam.TryGetValue(key, out var logged) && logged.BuildId == acf.BuildId)
                         && !_state.PendingTriggerKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
            if (queue) _state.PendingTriggerKeys.Add(key);
            _state.Save();
            return queue;
        }
    }

    // ------------------------------------------------------------------ full ACF scans

    /// <summary>Fingerprint every Steam game in supported libraries (including ones not fully installed).</summary>
    private static Dictionary<string, SteamGameState> CaptureSteamSnapshot(AppSettings settings)
    {
        var snapshot = new Dictionary<string, SteamGameState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, acf) in LibraryDiscovery.ReadSupportedSteamAcfs(LibraryDiscovery.Discover(settings)))
            snapshot[key] = SteamGameState.FromAcf(acf);
        return snapshot;
    }

    /// <summary>
    /// After a finished pass, full scan of every ACF before live watching is turned back on.
    /// Compares against the pass-start snapshot and last known state, and queues any fully
    /// installed game that changed or is new. Still-updating games (StateFlags != 4) are
    /// left for the live watcher to pick up when they finish.
    /// </summary>
    private void ScanAfterPass()
    {
        lock (_scanLock)
        {
            var current = CaptureSteamSnapshot(Settings);
            Dictionary<string, SteamGameState>? start = null;
            Dictionary<string, SteamGameState>? known = null;
            lock (_lock)
            {
                if (_state.HasPassStartSnapshot)
                    start = new Dictionary<string, SteamGameState>(_state.PassStartSnapshot, StringComparer.OrdinalIgnoreCase);
                if (_state.HasLastKnownSteamState)
                    known = new Dictionary<string, SteamGameState>(_state.LastKnownSteamState, StringComparer.OrdinalIgnoreCase);
            }

            if (!Settings.WatchSteamUpdates)
            {
                Log.Write("End-of-pass ACF scan skipped (Watch for Steam updates is off).");
                RememberSteamState(current);
                return;
            }

            var queued = QueueChangedGames(current, start, known, "End-of-pass scan");
            RememberSteamState(current);
            if (queued.Count > 0)
                Log.Write($"End-of-pass ACF scan: {queued.Count} game(s) queued for another pass: {string.Join(", ", queued)}.");
            else
                Log.Write("End-of-pass ACF scan: no games changed or were added during the pass.");
        }
    }

    /// <summary>
    /// Full ACF scan against last known state (startup, watcher buffer overflow, watching turned on, or right after
    /// watchers come back on at the end of a pass). Skipped when a pass is in progress; the end-of-pass scan covers that.
    /// The first run with no baseline records current state without queueing every game.
    /// </summary>
    /// <param name="quietIfUnchanged">Skip the "none changed" log line (used for the post-pass re-check).</param>
    private void ScanSteamManifests(string reason, bool quietIfUnchanged = false)
    {
        lock (_scanLock)
        {
            if (!Settings.WatchSteamUpdates)
            {
                if (!quietIfUnchanged) Log.Write($"{reason} ACF scan skipped (Watch for Steam updates is off).");
                return;
            }
            if (PassActive)
            {
                Log.Write($"{reason} ACF scan skipped (a pass is in progress; the end-of-pass full scan will cover updates).");
                return;
            }

            var current = CaptureSteamSnapshot(Settings);
            Dictionary<string, SteamGameState>? known;
            lock (_lock)
            {
                if (_state.ActiveRun != RunKind.None)
                {
                    Log.Write($"{reason} ACF scan skipped (a pass is in progress; the end-of-pass full scan will cover updates).");
                    return;
                }
                known = _state.HasLastKnownSteamState
                    ? new Dictionary<string, SteamGameState>(_state.LastKnownSteamState, StringComparer.OrdinalIgnoreCase)
                    : null;
            }

            if (known == null)
            {
                RememberSteamState(current);
                Log.Write($"{reason} ACF scan: recorded {current.Count} game(s) as last known state (no prior baseline).");
                return;
            }

            var queued = QueueChangedGames(current, passSnapshot: null, known, reason + " scan");
            RememberSteamState(current);
            if (queued.Count > 0)
            {
                Log.Write($"{reason} ACF scan: {queued.Count} game(s) queued: {string.Join(", ", queued)}.");
                _wake.Set();
            }
            else if (!quietIfUnchanged)
                Log.Write($"{reason} ACF scan: {current.Count} game(s) checked, none changed.");
        }
    }

    /// <summary>
    /// Queues every fully installed game whose state differs from the pass-start snapshot (when given) or from the last
    /// known state (when given). Returns the display names queued, for the log.
    /// </summary>
    private List<string> QueueChangedGames(
        Dictionary<string, SteamGameState> current,
        Dictionary<string, SteamGameState>? passSnapshot,
        Dictionary<string, SteamGameState>? lastKnown,
        string logPrefix)
    {
        var queued = new List<string>();
        foreach (var (key, now) in current)
        {
            if (now.StateFlags != SteamGameState.FullyInstalled) continue; // still updating; the watcher will queue when it finishes
            string? why = null;
            if (passSnapshot != null)
            {
                if (!passSnapshot.TryGetValue(key, out var snap)) why = "installed during the pass";
                else if (!snap.SameAs(now)) why = "changed during the pass";
            }
            if (why == null && lastKnown != null)
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

    private void RememberSteamState(Dictionary<string, SteamGameState> current)
    {
        lock (_lock)
        {
            _state.LastKnownSteamState = new Dictionary<string, SteamGameState>(current, StringComparer.OrdinalIgnoreCase);
            _state.HasLastKnownSteamState = true;
            _state.Save();
        }
    }
}
