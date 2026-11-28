using Microsoft.Win32;

namespace SteamLZXTray;

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

        SystemEvents.SessionEnding += OnSessionEnding;
        _anim.Tick += (_, _) => Animate();
        _anim.Start();
        _engine.Start();

        _updater.InstallStarting += _ => _engine.SaveNow(); // SaveNow never throws
        _updater.UpdateAvailable += tag =>
        {
            try { _form.BeginInvoke(new Action(() => _tray.ShowBalloonTip(6000, "Steam LZX Tray", $"Update {tag} is available. Open Settings to install it.", ToolTipIcon.Info))); } catch { }
        };

        if (postInstall != null)
        {
            bool updated = postInstall.Equals("update", StringComparison.OrdinalIgnoreCase);
            string msg = updated ? $"Updated to {AppInfo.Version}." : $"Installed {AppInfo.Version}. Click the tray icon for status and settings.";
            Log.Write("Post-install: " + msg);
            _tray.ShowBalloonTip(6000, "Steam LZX Tray", msg, ToolTipIcon.Info);
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => _engine.SaveNow();

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
        if (!ReferenceEquals(next, _currentIcon))
        {
            _tray.Icon = next;
            _currentIcon = next;
        }

        string tip = st.Phase switch
        {
            Phase.Working => $"LZX: compressing {st.Game}",
            Phase.Waiting => $"LZX: waiting - {st.WaitReason}",
            Phase.Paused => "LZX: paused",
            _ => $"LZX: idle, saved {Fmt.Bytes(st.TotalBytesSaved)}",
        };
        if (tip.Length > 127) tip = tip.Substring(0, 124) + "...";
        if (tip != _lastTip)
        {
            _tray.Text = tip;
            _lastTip = tip;
        }
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
            SystemEvents.SessionEnding -= OnSessionEnding;
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
