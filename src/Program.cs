namespace SteamLZXTray;

internal static class Program
{
    private const string MutexName = @"Local\SteamLZXTray.SingleInstance.v1";
    private const string ShowEventName = @"Local\SteamLZXTray.ShowWindow.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        try { AppPaths.Ensure(); } catch { /* logging falls back silently */ }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);
        ApplicationConfiguration.Initialize();

        // Anything not launched from the canonical install path is an installer/updater only.
        if (!Installer.IsRunningFromInstallPath)
            return Installer.RunInstall(silent: args.Any(a => a.Equals("--update", StringComparison.OrdinalIgnoreCase)));

        string? postInstall = args.FirstOrDefault(a => a.StartsWith(Installer.PostInstallArg, StringComparison.OrdinalIgnoreCase))
            ?.Substring(Installer.PostInstallArg.Length);

        // Single instance. After an install/update handoff the old instance may still be exiting,
        // so wait for the mutex instead of bailing out.
        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(postInstall != null ? 15000 : 0); }
        catch (AbandonedMutexException) { owned = true; } // previous instance was killed / lost power
        if (!owned)
        {
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            return 0;
        }

        try
        {
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.ExitEventName);
            Log.Write($"=== Steam LZX Tray {AppInfo.Version} starting from {Installer.CurrentExe} (args: {string.Join(' ', args)}) ===");

            var settings = AppSettings.Load(Log.Write);
            Integration.RepairAutoStart();
            Integration.ApplyShortcutSetting(settings.StartMenuShortcut);

            using var ctx = new TrayContext(settings, showEvent, exitEvent, postInstall);
            Application.Run(ctx);
            Log.Write("=== exiting ===");
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
        return 0;
    }
}
