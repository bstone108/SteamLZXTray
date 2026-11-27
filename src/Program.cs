using System.Reflection;

namespace SteamLZXTray;

internal static class AppInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
}

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

/// <summary>Tray-only application context. The window is created hidden and shown on icon click.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly IconSet _icons = new();
    private readonly Engine _engine;
    private readonly Updater _updater;
    private readonly MainForm _form;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 80 };
    private readonly ToolStripMenuItem _miPause;
    private readonly RegisteredWaitHandle _showWait;
    private readonly RegisteredWaitHandle _exitWait;
    private bool _exiting;
    private int _frame;
    private Icon? _currentIcon;
    private string _lastTip = "";

    public TrayContext(AppSettings settings, EventWaitHandle showEvent, EventWaitHandle exitEvent, string? postInstall)
    {
        _engine = new Engine(settings);
        _updater = new Updater(() => _engine.Settings);
        _form = new MainForm(_engine, _icons.Idle) { ExitRequested = ExitApp, Updater = _updater };
        _ = _form.Handle; // create handle (hidden) so BeginInvoke works

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Run Now", null, (_, _) => _engine.RunNow());
        _miPause = new ToolStripMenuItem("Pause", null, (_, _) => _engine.SetPaused(!_engine.Paused));
        menu.Items.Add(_miPause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        menu.Opening += (_, _) => _miPause.Text = _engine.Paused ? "Resume" : "Pause";

        _tray = new NotifyIcon { Icon = _icons.Idle, Text = "Steam LZX Tray", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            try { _form.BeginInvoke(new Action(ShowWindow)); } catch { }
        }, null, Timeout.Infinite, false);

        // Installer/updater asks us to save state and exit so it can replace the exe.
        _exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) =>
        {
            Log.Write("Exit requested by installer/updater.");
            try { _form.BeginInvoke(new Action(ExitApp)); } catch { }
        }, null, Timeout.Infinite, true);

        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => _engine.SaveNow();
        _anim.Tick += (_, _) => Animate();
        _anim.Start();
        _engine.Start();

        _updater.InstallStarting += tag => { try { _engine.SaveNow(); } catch { } };
        _updater.UpdateAvailable += tag =>
        {
            try { _form.BeginInvoke(new Action(() => _tray.ShowBalloonTip(6000, "Steam LZX Tray", $"Update {tag} is available. Open Settings to install it.", ToolTipIcon.Info))); } catch { }
        };

        if (postInstall != null)
        {
            bool upd = postInstall.Equals("update", StringComparison.OrdinalIgnoreCase);
            string msg = upd ? $"Updated to {AppInfo.Version}." : $"Installed {AppInfo.Version}. Click the tray icon for status and settings.";
            Log.Write("Post-install: " + msg);
            _tray.ShowBalloonTip(6000, "Steam LZX Tray", msg, ToolTipIcon.Info);
        }
    }

    private void ShowWindow()
    {
        if (!_form.Visible) _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    private void Animate()
    {
        var st = _engine.GetStatus();
        Icon next = st.Phase switch
        {
            Phase.Working => _icons.Spin[_frame = (_frame + 1) % _icons.Spin.Length],
            Phase.Waiting => _icons.Waiting,
            Phase.Paused => _icons.Paused,
            _ => _icons.Idle,
        };
        if (!ReferenceEquals(next, _currentIcon)) { _tray.Icon = next; _currentIcon = next; }

        string tip = st.Phase switch
        {
            Phase.Working => $"LZX: compressing {st.Game}",
            Phase.Waiting => $"LZX: waiting - {st.WaitReason}",
            Phase.Paused => "LZX: paused",
            _ => $"LZX: idle, saved {Fmt.Bytes(st.TotalBytesSaved)}",
        };
        if (tip.Length > 127) tip = tip.Substring(0, 124) + "...";
        if (tip != _lastTip) { _tray.Text = tip; _lastTip = tip; }
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _anim.Stop();
        _tray.Visible = false;
        _engine.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait.Unregister(null);
            _exitWait.Unregister(null);
            _updater.Dispose();
            _anim.Dispose();
            _tray.Dispose();
            _form.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }
}
