using System.Text.Json;
using SteamLZXTray;

// Minimal test runner (no NuGet): each check throws on failure; exit code = number of failures.
var tests = new (string Name, Action Body)[]
{
    ("RunState JSON property names are unchanged", RunStatePropertyNames),
    ("Pre-snapshot state.json loads as a legacy pass (one reset)", LegacyStateLoads),
    ("Current state.json keeps snapshot and last-known state (no reset)", CurrentStateLoads),
    ("RunState tolerates explicit nulls", RunStateNulls),
    ("RunState save/load round trip", RunStateRoundTrip),
    ("SteamGameState.SameAs ignores Name only", SameAsFields),
    ("AppSettings JSON property names are unchanged", SettingsPropertyNames),
    ("AppSettings tolerates nulls and parses DailyTime", SettingsNullsAndDailyTime),
    ("GameLog tolerates nulls and is case-insensitive", GameLogLoads),
    ("VDF parser reads appmanifest and libraryfolders", VdfParses),
    ("Fmt.Bytes", FmtBytes),
};

int failed = 0;
foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}: {ex.Message}"); }
}
Console.WriteLine(failed == 0 ? $"All {tests.Length} tests passed." : $"{failed} of {tests.Length} tests failed.");
return failed;

// ---------------------------------------------------------------------- helpers

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static string TempFile(string contents)
{
    string path = Path.Combine(Path.GetTempPath(), "lzxtest-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(path, contents);
    return path;
}

static T LoadJson<T>(string json, Func<T> fallback) where T : class
{
    string path = TempFile(json);
    try { return AtomicJson.Load(path, fallback); }
    finally { File.Delete(path); }
}

static HashSet<string> PropertyNames<T>(T value)
{
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, AtomicJson.Options));
    return doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
}

static void SameSet(HashSet<string> actual, string[] expected, string what)
{
    var missing = expected.Except(actual).ToList();
    var extra = actual.Except(expected).ToList();
    Check(missing.Count == 0 && extra.Count == 0,
        $"{what}: missing [{string.Join(", ", missing)}], unexpected [{string.Join(", ", extra)}]");
}

// ---------------------------------------------------------------------- tests

static void RunStatePropertyNames()
{
    SameSet(PropertyNames(new RunState()), new[]
    {
        "ActiveRun", "RunStartedUtc", "CurrentUnitKey", "LastCompletedRelPath", "CurrentUnitIncomplete",
        "TriggeredUnitKeys", "PendingTriggerKeys", "LastScheduledRunUtc", "LastRunCompletedUtc",
        "TotalBytesSaved", "TotalFilesCompressed", "RunBytesSaved", "RunFilesProcessed", "RunFilesCompressed",
        "HasPassStartSnapshot", "IgnoreGameLogForPass", "PassStartSnapshot", "HasLastKnownSteamState", "LastKnownSteamState",
    }, "state.json");
    SameSet(PropertyNames(new SteamGameState()), new[] { "Name", "BuildId", "LastUpdated", "SizeOnDisk", "StateFlags" }, "snapshot entry");
}

static void LegacyStateLoads()
{
    // Shape written by builds before v2026.11.28.01 (no snapshot or last-known fields).
    var s = RunState.Normalize(LoadJson("""
        {
          "ActiveRun": "Scheduled",
          "CurrentUnitKey": "D:\\SteamLibrary|570",
          "LastCompletedRelPath": "game\\bin\\a.dll",
          "TriggeredUnitKeys": [],
          "PendingTriggerKeys": ["D:\\SteamLibrary|730"],
          "TotalBytesSaved": 123456789,
          "TotalFilesCompressed": 42,
          "RunFilesProcessed": 7
        }
        """, () => new RunState()));
    Check(s.ActiveRun == RunKind.Scheduled, "ActiveRun");
    Check(!s.HasPassStartSnapshot, "legacy pass must have no snapshot (triggers the one-time reset)");
    Check(!s.IgnoreGameLogForPass, "IgnoreGameLogForPass defaults to false");
    Check(!s.HasLastKnownSteamState && s.LastKnownSteamState.Count == 0, "no last-known baseline");
    Check(s.TotalBytesSaved == 123456789 && s.TotalFilesCompressed == 42, "all-time totals kept");
    Check(s.PendingTriggerKeys.Count == 1, "pending keys kept");
}

static void CurrentStateLoads()
{
    var s = RunState.Normalize(LoadJson("""
        {
          "ActiveRun": "Triggered",
          "TriggeredUnitKeys": ["D:\\SteamLibrary|570"],
          "PendingTriggerKeys": [],
          "HasPassStartSnapshot": true,
          "IgnoreGameLogForPass": true,
          "PassStartSnapshot": { "D:\\SteamLibrary|570": { "Name": "Dota 2", "BuildId": "1", "LastUpdated": "100", "SizeOnDisk": "5", "StateFlags": 4 } },
          "HasLastKnownSteamState": true,
          "LastKnownSteamState": { "D:\\SteamLibrary|570": { "Name": "Dota 2", "BuildId": "1", "LastUpdated": "100", "SizeOnDisk": "5", "StateFlags": 4 } }
        }
        """, () => new RunState()));
    Check(s.HasPassStartSnapshot && s.IgnoreGameLogForPass && s.HasLastKnownSteamState, "flags");
    Check(s.PassStartSnapshot.ContainsKey(@"d:\steamlibrary|570"), "snapshot keys are case-insensitive after load");
    Check(s.LastKnownSteamState[@"D:\STEAMLIBRARY|570"].StateFlags == SteamGameState.FullyInstalled, "last-known entry");
}

static void RunStateNulls()
{
    var s = RunState.Normalize(LoadJson("""
        { "PassStartSnapshot": null, "LastKnownSteamState": null, "PendingTriggerKeys": null, "TriggeredUnitKeys": null }
        """, () => new RunState()));
    Check(s.PassStartSnapshot != null && s.LastKnownSteamState != null, "dictionaries");
    Check(s.PendingTriggerKeys != null && s.TriggeredUnitKeys != null, "lists");
}

static void RunStateRoundTrip()
{
    string path = Path.Combine(Path.GetTempPath(), "lzxtest-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        var s = new RunState { ActiveRun = RunKind.Manual, HasPassStartSnapshot = true, TotalBytesSaved = 9 };
        s.PassStartSnapshot["L|1"] = new SteamGameState { Name = "A", BuildId = "2", StateFlags = 4 };
        AtomicJson.Save(path, s);
        AtomicJson.Save(path, s); // second save exercises the replace-with-backup path
        Check(File.Exists(path + ".bak"), "backup written");
        var back = RunState.Normalize(AtomicJson.Load(path, () => new RunState()));
        Check(back.ActiveRun == RunKind.Manual && back.HasPassStartSnapshot && back.TotalBytesSaved == 9, "scalars");
        Check(back.PassStartSnapshot["l|1"].BuildId == "2", "snapshot entry");
        Check(File.ReadAllText(path).Contains("\"Manual\""), "enums stored as strings");
    }
    finally
    {
        foreach (var f in new[] { path, path + ".bak", path + ".tmp" }) File.Delete(f);
    }
}

static void SameAsFields()
{
    var a = new SteamGameState { Name = "X", BuildId = "1", LastUpdated = "2", SizeOnDisk = "3", StateFlags = 4 };
    SteamGameState With(Action<SteamGameState> change)
    {
        var c = new SteamGameState { Name = a.Name, BuildId = a.BuildId, LastUpdated = a.LastUpdated, SizeOnDisk = a.SizeOnDisk, StateFlags = a.StateFlags };
        change(c);
        return c;
    }
    Check(a.SameAs(With(c => c.Name = "Renamed")), "name is not part of the fingerprint");
    Check(!a.SameAs(With(c => c.BuildId = "9")), "buildid");
    Check(!a.SameAs(With(c => c.LastUpdated = "9")), "LastUpdated");
    Check(!a.SameAs(With(c => c.SizeOnDisk = "9")), "SizeOnDisk");
    Check(!a.SameAs(With(c => c.StateFlags = 1026)), "StateFlags");
}

static void SettingsPropertyNames()
{
    SameSet(PropertyNames(new AppSettings()), new[]
    {
        "CpuThresholdPercent", "DriveThresholdMBps", "ScheduleMode", "DailyTime", "IntervalHours", "WatchSteamUpdates",
        "MinFileSizeKB", "TreatFullscreenAsGame", "AutoInstallUpdates", "StartMenuShortcut", "CustomFolders", "ExcludedDrives",
    }, "settings.json");
}

static void SettingsNullsAndDailyTime()
{
    var s = LoadJson("""{ "DailyTime": "22:15", "CustomFolders": null, "ExcludedDrives": ["e:\\"], "ScheduleMode": "Interval" }""",
        () => new AppSettings());
    Check(s.DailyTimeOfDay() == new TimeSpan(22, 15, 0), "DailyTime parsed");
    Check(s.ScheduleMode == ScheduleMode.Interval, "enum by name");
    Check(s.IsExcluded(@"E:\Games") && !s.IsExcluded(@"D:\Games"), "IsExcluded");
    Check(new AppSettings { DailyTime = "garbage" }.DailyTimeOfDay() == TimeSpan.FromHours(3), "bad DailyTime falls back to 03:00");
    Check(s.WatchSteamUpdates && s.AutoInstallUpdates, "missing properties keep defaults");
}

static void GameLogLoads()
{
    var g = LoadJson("""{ "Steam": { "D:\\Lib|570": { "BuildId": "7" } }, "Folders": null }""", () => new GameLog());
    // Same normalization GameLog.Load applies.
    g.Steam = new Dictionary<string, GameLogEntry>(g.Steam ?? new(), StringComparer.OrdinalIgnoreCase);
    g.Folders = new Dictionary<string, FolderLogEntry>(g.Folders ?? new(), StringComparer.OrdinalIgnoreCase);
    Check(g.Steam[@"d:\lib|570"].BuildId == "7" && g.Folders.Count == 0, "entries");
}

static void VdfParses()
{
    string acfPath = TempFile("""
        "AppState"
        {
            "appid"        "570"
            "name"         "Dota 2"
            "StateFlags"   "4"
            "installdir"   "dota 2 beta"
            "LastUpdated"  "1700000000"
            "SizeOnDisk"   "123"
            "buildid"      "999"
            // comment
            "UserConfig" { "language" "english" }
        }
        """);
    try
    {
        var acf = AcfInfo.Read(acfPath);
        Check(acf != null, "parsed");
        Check(acf!.AppId == "570" && acf.Name == "Dota 2" && acf.InstallDir == "dota 2 beta", "strings");
        Check(acf.BuildId == "999" && acf.LastUpdated == "1700000000" && acf.SizeOnDisk == "123" && acf.StateFlags == 4, "fingerprint fields");
        var snap = SteamGameState.FromAcf(acf);
        Check(snap.Name == "Dota 2" && snap.BuildId == "999" && snap.StateFlags == 4, "FromAcf");
    }
    finally { File.Delete(acfPath); }

    var lf = VdfNode.Parse("""
        "libraryfolders"
        {
            "0" { "path" "C:\\Program Files (x86)\\Steam" }
            "1" { "path" "D:\\SteamLibrary" }
        }
        """).Node("libraryfolders")!;
    Check(lf.Ordered.Count == 2 && lf.Node("1")!.Str("path") == @"D:\SteamLibrary", "libraryfolders with escaped backslashes");
    Check(AcfInfo.Read("/nonexistent/appmanifest_1.acf") == null, "missing file -> null");
}

static void FmtBytes()
{
    Check(Fmt.Bytes(0) == "0 B", Fmt.Bytes(0));
    Check(Fmt.Bytes(1536) == $"{1.5:0.##} KB", Fmt.Bytes(1536));
    Check(Fmt.Bytes(5L * 1024 * 1024 * 1024) == "5 GB", Fmt.Bytes(5L * 1024 * 1024 * 1024));
}
