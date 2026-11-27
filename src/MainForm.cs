using System.Drawing;

namespace SteamLZXTray;

internal sealed class MainForm : Form
{
    private readonly Engine _engine;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 700 };
    private long _logVersion = -1;
    private bool _loadingUi;

    // status
    private readonly Label _lblState = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold) };
    private readonly Label _lblWait = new() { AutoSize = true, ForeColor = Color.DarkGoldenrod };
    private readonly Label _lblCurrent = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label _lblFile = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label _lblStats = new() { AutoSize = true };
    private readonly Label _lblMetrics = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Button _btnRun = new() { Text = "Run Now", AutoSize = true };
    private readonly Button _btnPause = new() { Text = "Pause", AutoSize = true };
    private readonly Button _btnClearSkip = new() { Text = "Clear skip list", AutoSize = true };
    private readonly Button _btnResetLog = new() { Text = "Reset game log", AutoSize = true };
    private readonly Button _btnRescan = new() { Text = "Rescan libraries", AutoSize = true };
    private readonly ListView _libs = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, GridLines = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 8.5f) };

    // settings
    private readonly NumericUpDown _numCpu = new() { Minimum = 1, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _numDrive = new() { Minimum = 1, Maximum = 5000, Width = 70 };
    private readonly NumericUpDown _numMinKb = new() { Minimum = 0, Maximum = 1048576, Width = 90 };
    private readonly RadioButton _rbDaily = new() { Text = "Daily at", AutoSize = true };
    private readonly DateTimePicker _dtDaily = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
    private readonly RadioButton _rbInterval = new() { Text = "Every", AutoSize = true };
    private readonly NumericUpDown _numHours = new() { Minimum = 1, Maximum = 720, Width = 60 };
    private readonly RadioButton _rbManual = new() { Text = "Manual only", AutoSize = true };
    private readonly CheckBox _chkWatch = new() { Text = "Watch for Steam updates (compress games after Steam finishes updating them)", AutoSize = true };
    private readonly CheckBox _chkFullscreen = new() { Text = "Treat any fullscreen app in the foreground as a game", AutoSize = true };
    private readonly CheckBox _chkAutostart = new() { Text = "Start with Windows (applies immediately)", AutoSize = true };
    private readonly CheckBox _chkShortcut = new() { Text = "Start Menu shortcut (applies immediately)", AutoSize = true };
    private readonly TextBox _txtExcluded = new() { Width = 120 };
    private readonly ListBox _lstCustom = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button _btnAddFolder = new() { Text = "Add...", AutoSize = true };
    private readonly Button _btnRemoveFolder = new() { Text = "Remove", AutoSize = true };
    private readonly Button _btnSave = new() { Text = "Save settings", AutoSize = true };
    private readonly Button _btnUninstall = new() { Text = "Uninstall...", AutoSize = true };

    private readonly CheckBox _chkAutoUpdate = new() { Text = "Automatically install updates (checks GitHub releases at startup + every 6 h)", AutoSize = true };
    private readonly Button _btnCheckUpdate = new() { Text = "Check for updates now", AutoSize = true };
    private readonly Label _lblVersion = new() { AutoSize = true, ForeColor = Color.DimGray };

    public Updater? Updater { get; set; }

    /// <summary>Set by the tray context: saves state and exits the app.</summary>
    public Action? ExitRequested { get; set; }

    public MainForm(Engine engine, Icon icon)
    {
        _engine = engine;
        Text = $"Steam LZX Tray {AppInfo.Version}";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 700);
        MinimumSize = new Size(760, 520);
        ShowInTaskbar = true;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildStatusTab());
        tabs.TabPages.Add(BuildSettingsTab());
        var logTab = new TabPage("Log") { Padding = new Padding(6) };
        logTab.Controls.Add(_log);
        tabs.TabPages.Add(logTab);
        tabs.TabPages.Add(InstructionsTab.Build());
        Controls.Add(tabs);

        _btnRun.Click += (_, _) => _engine.RunNow();
        _btnPause.Click += (_, _) => _engine.SetPaused(!_engine.Paused);
        _btnClearSkip.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Clear the skip list? Files that did not compress will be tried again.", Text, MessageBoxButtons.OKCancel) == DialogResult.OK)
                _engine.ClearSkipList();
        };
        _btnResetLog.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Reset the game/folder log? Scheduled runs will re-walk every game.", Text, MessageBoxButtons.OKCancel) == DialogResult.OK)
                _engine.ResetGameLog();
        };
        _btnRescan.Click += (_, _) => _engine.Rescan();
        _btnSave.Click += (_, _) => SaveSettings();
        _btnUninstall.Click += (_, _) => Uninstall();
        _btnCheckUpdate.Click += async (_, _) =>
        {
            if (Updater == null) return;
            _btnCheckUpdate.Enabled = false;
            try
            {
                var tag = await Updater.CheckAsync(manual: true, forceInstall: false);
                if (tag != null && !_engine.Settings.AutoInstallUpdates &&
                    MessageBox.Show(this, $"Version {tag} is available. Install it now?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await Updater.CheckAsync(manual: true, forceInstall: true);
            }
            finally { _btnCheckUpdate.Enabled = true; RefreshStatus(); }
        };
        _btnAddFolder.Click += (_, _) => AddFolder();
        _btnRemoveFolder.Click += (_, _) =>
        {
            if (_lstCustom.SelectedItem is string s) { _lstCustom.Items.Remove(s); SaveSettings(); }
        };
        _chkAutostart.CheckedChanged += (_, _) =>
        {
            if (_loadingUi) return;
            try { Integration.SetAutoStart(_chkAutostart.Checked); }
            catch (Exception ex) { MessageBox.Show(this, "Could not change autostart: " + ex.Message, Text); }
            ReflectIntegration();
        };
        _chkShortcut.CheckedChanged += (_, _) =>
        {
            if (_loadingUi) return;
            try
            {
                var cur = _engine.Settings;
                cur.StartMenuShortcut = _chkShortcut.Checked;
                cur.Save();
                if (_chkShortcut.Checked) Integration.InstallShortcut(); else Integration.UninstallShortcut();
            }
            catch (Exception ex) { Log.Write("Shortcut change failed: " + ex); MessageBox.Show(this, "Could not change Start Menu shortcut: " + ex.Message, Text); }
            ReflectIntegration();
        };

        _engine.LibrariesChanged += () => { if (IsHandleCreated) BeginInvoke(new Action(RefreshLibraries)); };
        _timer.Tick += (_, _) => RefreshStatus();
        VisibleChanged += (_, _) =>
        {
            if (Visible) { LoadSettingsToUi(); RefreshLibraries(); RefreshStatus(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    private TabPage BuildStatusTab()
    {
        var page = new TabPage("Status") { Padding = new Padding(8) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(_lblState);
        t.Controls.Add(_lblWait);
        t.Controls.Add(_lblCurrent);
        t.Controls.Add(_lblFile);
        t.Controls.Add(_lblStats);
        t.Controls.Add(_lblMetrics);
        t.Controls.Add(_lblVersion);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 6) };
        buttons.Controls.AddRange(new Control[] { _btnRun, _btnPause, _btnRescan, _btnClearSkip, _btnResetLog });
        t.Controls.Add(buttons);
        _libs.Columns.Add("Type", 70);
        _libs.Columns.Add("Path", 330);
        _libs.Columns.Add("FS", 60);
        _libs.Columns.Add("Status", 230);
        _libs.Columns.Add("Games / folders", 110);
        _libs.Columns.Add("Compressed", 90);
        t.Controls.Add(_libs);
        page.Controls.Add(t);
        return page;
    }

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("Settings") { Padding = new Padding(10), AutoScroll = true };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = false };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) });
            t.Controls.Add(c);
        }
        FlowLayoutPanel Flow(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            foreach (var c in cs) { c.Anchor = AnchorStyles.Left; f.Controls.Add(c); }
            return f;
        }
        Label Note(string s) => new() { Text = s, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 6, 0, 0) };

        Row("Max CPU load (other processes):", Flow(_numCpu, Note("%")));
        Row("Max drive activity (other processes):", Flow(_numDrive, Note("MB/s on the drive being compressed (own I/O excluded)")));
        Row("Minimum file size:", Flow(_numMinKb, Note("KB (smaller files are skipped)")));
        Row("Schedule:", Flow(_rbDaily, _dtDaily, new Label { Width = 16 }, _rbInterval, _numHours, Note("hours"), new Label { Width = 16 }, _rbManual));
        Row("", _chkWatch);
        Row("", _chkFullscreen);
        Row("Excluded drives:", Flow(_txtExcluded, Note("comma separated drive letters, e.g. X:, Y:  (never touched; empty = none)")));
        Row("", Flow(_chkAutoUpdate, new Label { Width = 16 }, _btnCheckUpdate));
        Row("", _chkAutostart);
        Row("", _chkShortcut);

        var customPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 160, Margin = new Padding(0) };
        customPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        customPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var cb = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0) };
        cb.Controls.Add(_btnAddFolder); cb.Controls.Add(_btnRemoveFolder);
        customPanel.Controls.Add(_lstCustom, 0, 0);
        customPanel.Controls.Add(cb, 1, 0);
        Row("Custom folders (non-Steam):", customPanel);
        Row("", Flow(_btnSave, new Label { Width = 40 }, _btnUninstall));
        page.Controls.Add(t);
        return page;
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose a folder to compress (e.g. another game client's library)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string p = dlg.SelectedPath;
        var probe = new AppSettings { ExcludedDrives = ParseDrives(_txtExcluded.Text) };
        if (probe.IsExcluded(p))
        {
            MessageBox.Show(this, $"{p} is on an excluded drive and will not be compressed.", Text);
            return;
        }
        if (!_lstCustom.Items.Cast<string>().Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase)))
        {
            _lstCustom.Items.Add(p);
            SaveSettings();
        }
    }

    private static List<string> ParseDrives(string s) =>
        s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
         .Select(x => x.Trim().TrimEnd('\\'))
         .Select(x => x.Length == 1 ? x + ":" : x)
         .Select(x => x.ToUpperInvariant()).Distinct().ToList();

    private void LoadSettingsToUi()
    {
        _loadingUi = true;
        try
        {
            var s = _engine.Settings;
            _numCpu.Value = Math.Clamp(s.CpuThresholdPercent, 1, 100);
            _numDrive.Value = Math.Clamp(s.DriveThresholdMBps, 1, 5000);
            _numMinKb.Value = Math.Clamp(s.MinFileSizeKB, 0, 1048576);
            _rbDaily.Checked = s.ScheduleMode == ScheduleMode.Daily;
            _rbInterval.Checked = s.ScheduleMode == ScheduleMode.Interval;
            _rbManual.Checked = s.ScheduleMode == ScheduleMode.ManualOnly;
            if (!TimeSpan.TryParse(s.DailyTime, out var tod)) tod = TimeSpan.FromHours(3);
            _dtDaily.Value = DateTime.Today + tod;
            _numHours.Value = Math.Clamp(s.IntervalHours, 1, 720);
            _chkWatch.Checked = s.WatchSteamUpdates;
            _chkFullscreen.Checked = s.TreatFullscreenAsGame;
            _chkAutoUpdate.Checked = s.AutoInstallUpdates;
            _txtExcluded.Text = string.Join(", ", s.ExcludedDrives);
            _lstCustom.Items.Clear();
            foreach (var f in s.CustomFolders) _lstCustom.Items.Add(f);
        }
        finally { _loadingUi = false; }
        ReflectIntegration();
    }

    /// <summary>Checkboxes always mirror the real registry / shortcut state.</summary>
    private void ReflectIntegration()
    {
        _loadingUi = true;
        try
        {
            _chkAutostart.Checked = Integration.IsAutoStartEnabled();
            _chkShortcut.Checked = Integration.IsShortcutInstalled();
        }
        catch { }
        finally { _loadingUi = false; }
    }

    private void SaveSettings()
    {
        var s = new AppSettings
        {
            CpuThresholdPercent = (int)_numCpu.Value,
            DriveThresholdMBps = (int)_numDrive.Value,
            MinFileSizeKB = (int)_numMinKb.Value,
            ScheduleMode = _rbInterval.Checked ? ScheduleMode.Interval : _rbManual.Checked ? ScheduleMode.ManualOnly : ScheduleMode.Daily,
            DailyTime = _dtDaily.Value.ToString("HH:mm"),
            IntervalHours = (int)_numHours.Value,
            WatchSteamUpdates = _chkWatch.Checked,
            TreatFullscreenAsGame = _chkFullscreen.Checked,
            StartMenuShortcut = _chkShortcut.Checked,
            AutoInstallUpdates = _chkAutoUpdate.Checked,
            ExcludedDrives = ParseDrives(_txtExcluded.Text),
            CustomFolders = _lstCustom.Items.Cast<string>().ToList(),
        };
        try { _engine.ApplySettings(s); }
        catch (Exception ex) { MessageBox.Show(this, "Could not save settings: " + ex.Message, Text); }
    }

    private void RefreshLibraries()
    {
        _libs.BeginUpdate();
        _libs.Items.Clear();
        foreach (var l in _engine.Libraries)
        {
            var it = new ListViewItem(new[]
            {
                l.Kind == LibraryKind.Steam ? "Steam" : "Custom",
                l.Path,
                l.FileSystem,
                l.Supported ? $"OK ({l.UnitCount} units, {l.CompletedCount} done)" : "Skipped: " + l.Status,
                l.Supported ? l.UnitCount.ToString() : "-",
                l.Supported ? $"{l.CompletedCount}/{l.UnitCount}" : "-",
            });
            if (!l.Supported) it.ForeColor = Color.Gray;
            _libs.Items.Add(it);
        }
        _libs.EndUpdate();
    }

    private void RefreshStatus()
    {
        var st = _engine.GetStatus();
        _lblState.Text = st.Phase switch
        {
            Phase.Working => $"Working ({st.Run} run)",
            Phase.Waiting => $"Waiting for idle ({st.Run} run)",
            Phase.Paused => "Paused",
            _ => "Idle",
        };
        _lblWait.Text = st.Phase is Phase.Waiting or Phase.Paused && st.WaitReason.Length > 0 ? "Reason: " + st.WaitReason : "";
        _lblCurrent.Text = st.Game.Length > 0 ? $"Library: {st.Library}    Game/folder: {st.Game}" : $"Next scheduled run: {st.NextSchedule}" +
            (st.LastRunCompletedUtc is DateTime d ? $"    Last run finished: {d.ToLocalTime():g}" : "") +
            (st.PendingTriggers > 0 ? $"    Queued Steam updates: {st.PendingTriggers}" : "");
        _lblFile.Text = st.File.Length > 0 ? "File: " + st.File : "";
        var (done, total) = _engine.CompletedTotals();
        _lblStats.Text = $"Games/folders fully compressed: {done:N0} / {total:N0}      This run: {st.RunFilesProcessed:N0} files checked, {st.RunFilesCompressed:N0} compressed, {Fmt.Bytes(st.RunBytesSaved)} saved      " +
                         $"All time: {st.TotalFilesCompressed:N0} files, {Fmt.Bytes(st.TotalBytesSaved)} saved      Skip list: {st.SkipListCount:N0}";
        _lblMetrics.Text = $"CPU (other processes): {st.CpuOther:0}%" + (st.DiskOtherMBps >= 0 ? $"    Drive (other processes): {st.DiskOtherMBps:0.0} MB/s" : "")
            + (st.DiskTotalMBps >= 0 ? $"    Drive throughput (all): {st.DiskTotalMBps:0.0} MB/s" : "");
        _btnPause.Text = _engine.Paused ? "Resume" : "Pause";
        _lblVersion.Text = $"Version {AppInfo.Version}    Last update check: {Updater?.LastResult ?? "-"}";

        if (Log.Version != _logVersion)
        {
            _logVersion = Log.Version;
            var lines = Log.Snapshot();
            _log.Text = string.Join(Environment.NewLine, lines);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }

    private void Uninstall()
    {
        if (MessageBox.Show(this, $"Uninstall Steam LZX Tray?\n\nThis removes the Start with Windows entry, the Start Menu shortcut and\n{Installer.InstallDir}\n\nCompressed files stay compressed (they work normally).",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var keep = MessageBox.Show(this, $"Keep your data (settings, game log, skip list, logs) in\n{AppPaths.Dir} ?\n\nYes = keep, No = delete it too.",
            Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (keep == DialogResult.Cancel) return;
        try
        {
            Installer.ScheduleUninstall(keepData: keep == DialogResult.Yes);
        }
        catch (Exception ex)
        {
            Log.Write("Uninstall failed: " + ex);
            MessageBox.Show(this, "Uninstall failed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        ExitRequested?.Invoke();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // close = hide to tray
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
