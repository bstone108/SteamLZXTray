using System.Diagnostics;

namespace SteamLZXTray;

/// <summary>
/// Self-install / self-update. Any launch from a path other than the canonical install path
/// never enters running mode: it stops the running instance, copies itself into place,
/// fixes the shortcut and Run value, starts the installed copy and exits.
/// </summary>
internal static class Installer
{
    public const string ExitEventName = @"Local\SteamLZXTray.Exit.v1";
    public const string PostInstallArg = "--post-install=";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SteamLZXTray");
    public static string InstallPath => Path.Combine(InstallDir, "SteamLZXTray.exe");

    public static string CurrentExe => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static bool IsRunningFromInstallPath => SamePath(CurrentExe, InstallPath);

    /// <summary>Returns process exit code. Never starts the engine or tray.</summary>
    public static int RunInstall(bool silent = false)
    {
        string src = CurrentExe;
        Log.Write($"Installer{(silent ? " (silent update)" : "")}: launched from {src}; install path {InstallPath}");
        try
        {
            bool isUpdate = File.Exists(InstallPath);

            // 1. Ask any running instance to save state and exit; kill only our installed copy if it hangs.
            StopRunningInstance();
            var foreign = FindInstances(installedOnly: false);
            if (foreign.Count > 0 && silent)
            {
                foreach (var p in foreign) { Log.Write($"Installer (silent update): copy still running from {SafePath(p)} (pid {p.Id}); continuing."); p.Dispose(); }
            }
            else if (foreign.Count > 0)
            {
                // e.g. an older test build started from the Desktop: it holds the single-instance mutex
                // and doesn't know the exit signal. We never kill a copy outside the install path.
                string where = string.Join("\n", foreign.Select(p => SafePath(p) ?? $"pid {p.Id}"));
                foreach (var p in foreign) p.Dispose();
                MessageBox.Show($"Another copy of Steam LZX Tray is running from:\n{where}\n\nRight-click its tray icon and choose Exit, then click OK to continue.",
                    "Steam LZX Tray setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                foreach (var p in FindInstances(installedOnly: false))
                {
                    Log.Write($"Installer: copy still running from {SafePath(p)} (pid {p.Id}); the installed copy may only open its window.");
                    p.Dispose();
                }
            }

            // 2. Copy self into place, retrying while the target is locked.
            Directory.CreateDirectory(InstallDir);
            CopyWithRetry(src, InstallPath, TimeSpan.FromMinutes(3));
            Log.Write($"Installer: copied to {InstallPath} ({(isUpdate ? "update" : "new install")})");

            // 3. Canonical Start Menu shortcut (setting, on by default) + stray cleanup.
            var settings = AppSettings.Load(Log.Write);
            Integration.ApplyShortcutSetting(settings.StartMenuShortcut);

            // 4. Autostart: if on, rewrite the one fixed Run value to the install path.
            if (Integration.IsAutoStartEnabled()) Integration.SetAutoStart(true);

            // 5. Start the installed copy and exit.
            var psi = new ProcessStartInfo(InstallPath)
            {
                UseShellExecute = false,
                WorkingDirectory = InstallDir,
            };
            psi.ArgumentList.Add("--tray");
            psi.ArgumentList.Add(PostInstallArg + (isUpdate ? "update" : "install"));
            Process.Start(psi);
            Log.Write("Installer: started installed copy; exiting.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("Installer failed: " + ex);
            if (silent && File.Exists(InstallPath))
            {
                // Auto-update failed after we stopped the running copy: bring the existing install back.
                try
                {
                    var back = new ProcessStartInfo(InstallPath) { UseShellExecute = false, WorkingDirectory = InstallDir };
                    back.ArgumentList.Add("--tray");
                    Process.Start(back);
                    Log.Write("Installer: update failed; restarted the existing installed copy.");
                }
                catch (Exception ex2) { Log.Write("Installer: could not restart existing copy: " + ex2.Message); }
            }
            if (!silent) MessageBox.Show($"Steam LZX Tray could not install to\n{InstallPath}\n\n{ex.Message}\n\nSee the log in {AppPaths.Logs}.",
                "Steam LZX Tray setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void StopRunningInstance()
    {
        bool signalled = false;
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ExitEventName);
            ev.Set();
            signalled = true;
            Log.Write("Installer: asked running instance to exit.");
        }
        catch (WaitHandleCannotBeOpenedException) { /* nothing running (or a build without the exit event) */ }
        catch (Exception ex) { Log.Write("Installer: exit signal failed: " + ex.Message); }

        // Give it time to finish the file it's compressing (it stops at the next file boundary).
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var nextNote = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (CountInstances(installedOnly: false) == 0) return;
            if (!signalled && CountInstances(installedOnly: true) == 0) break; // only foreign copies; nothing to wait for
            if (signalled && DateTime.UtcNow >= nextNote)
            {
                Log.Write("Installer: waiting for old copy to finish current file...");
                nextNote = DateTime.UtcNow.AddSeconds(10);
            }
            Thread.Sleep(250);
        }

        foreach (var p in FindInstances(installedOnly: true))
        {
            try
            {
                Log.Write($"Installer: instance pid {p.Id} did not exit in time; killing it.");
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception ex) { Log.Write($"Installer: kill failed for pid {p.Id}: {ex.Message}"); }
            finally { p.Dispose(); }
        }

        // A process stuck in a kernel compression call can't end until Windows finishes that file, even after
        // a kill. Keep waiting (up to 3 minutes) so the exe unlocks before we copy and relaunch.
        var start = DateTime.UtcNow;
        var killDeadline = start.AddMinutes(3);
        nextNote = start;
        while (DateTime.UtcNow < killDeadline)
        {
            var left = FindInstances(installedOnly: true);
            if (left.Count == 0)
            {
                if (DateTime.UtcNow - start > TimeSpan.FromSeconds(1))
                    Log.Write($"Installer: old copy exited after {(DateTime.UtcNow - start).TotalSeconds:0} s more.");
                break;
            }
            if (DateTime.UtcNow >= nextNote)
            {
                Log.Write($"Installer: waiting for old copy (pid {string.Join(", ", left.Select(p => p.Id))}) to finish current file... ({(DateTime.UtcNow - start).TotalSeconds:0} s)");
                nextNote = DateTime.UtcNow.AddSeconds(10);
            }
            foreach (var p in left) p.Dispose();
            Thread.Sleep(500);
        }
        if (FindInstances(installedOnly: true) is { Count: > 0 } stuck)
        {
            foreach (var p in stuck) { Log.Write($"Installer: old copy pid {p.Id} still hasn't exited after 3 min; trying to install anyway."); p.Dispose(); }
        }

        foreach (var p in FindInstances(installedOnly: false))
        {
            Log.Write($"Installer: another copy is still running from {SafePath(p)} (pid {p.Id}); not touching it.");
            p.Dispose();
        }
    }

    /// <summary>Other SteamLZXTray processes; installedOnly = main module equals the install path.</summary>
    private static List<Process> FindInstances(bool installedOnly)
    {
        var result = new List<Process>();
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("SteamLZXTray"))
        {
            if (p.Id == me) { p.Dispose(); continue; }
            if (!installedOnly) { result.Add(p); continue; }
            string? path = SafePath(p);
            if (path != null && SamePath(path, InstallPath)) result.Add(p);
            else p.Dispose();
        }
        return result;
    }

    /// <summary>Like <see cref="FindInstances"/> but only counts, disposing every process handle.</summary>
    private static int CountInstances(bool installedOnly)
    {
        var found = FindInstances(installedOnly);
        foreach (var p in found) p.Dispose();
        return found.Count;
    }

    private static string? SafePath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    private static void CopyWithRetry(string src, string dst, TimeSpan timeout)
    {
        string tmp = dst + ".new";
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                File.Copy(src, tmp, true);
                File.Move(tmp, dst, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(500);
            }
        }
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        throw new IOException($"Install target stayed locked for {timeout.TotalSeconds:0} s: {last?.Message}", last);
    }

    /// <summary>
    /// Uninstall from the running (installed) instance: Run value, shortcut, then a deferred
    /// cmd delete of the install folder (and optionally data) after this process exits.
    /// </summary>
    public static void ScheduleUninstall(bool keepData)
    {
        Integration.SetAutoStart(false);
        Integration.UninstallShortcut();
        try { if (File.Exists(Integration.ShortcutPath)) File.Delete(Integration.ShortcutPath); } catch { }

        string q(string p) => "\"" + p + "\"";
        int pid = Environment.ProcessId;
        // Wait for this process to exit (poll tasklist), then remove folders.
        string cmd =
            "/d /c \"for /l %i in (1,1,30) do (tasklist /fi \"PID eq " + pid + "\" | find \"" + pid + "\" >nul && ping -n 2 127.0.0.1 >nul)" +
            " & rmdir /s /q " + q(InstallDir) +
            (keepData ? "" : " & rmdir /s /q " + q(AppPaths.Dir)) + "\"";
        Log.Write("Uninstall: scheduled deferred delete" + (keepData ? " (keeping data)." : " (removing data)."));
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath(),
        };
        Process.Start(psi);
    }
}
