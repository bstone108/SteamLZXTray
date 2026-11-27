using System.Drawing;

namespace SteamLZXTray;

internal sealed class MainForm : Form
{
    private readonly Engine _engine;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 700 };
    private long _logVersion = -1;
    private bool _loadingUi;

    // status (short labelled rows; values never force the window wider)
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100 };
    private readonly Label _lblState = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold), Margin = new Padding(3, 2, 3, 4) };
    private EllipsisLabel _valWait = null!, _valLibrary = null!, _valGame = null!, _valFile = null!, _valNext = null!, _valLast = null!,
        _valQueued = null!, _valGames = null!, _valRun = null!, _valAllTime = null!, _valSkip = null!, _valCpu = null!,
        _valDriveOther = null!, _valDriveTotal = null!, _valVersion = null!, _valUpdate = null!;
    private TableLayoutPanel _statusOuter = null!;
    private TabControl _tabs = null!;
    private TabPage _statusPage = null!;
    private int _lastLibRows = -1;
    private readonly Button _btnRun = new() { Text = "Run Now", AutoSize = true };
    private readonly Button _btnPause = new() { Text = "Pause", AutoSize = true };
    private readonly Button _btnClearSkip = new() { Text = "Clear skip list", AutoSize = true };
    private readonly Button _btnResetLog = new() { Text = "Reset game log", AutoSize = true };
    private readonly Button _btnRescan = new() { Text = "Rescan libraries", AutoSize = true };
    private readonly ListView _libs = new() { View = View.Details, FullRowSelect = true, GridLines = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Scrollable = true, ShowItemToolTips = true };
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

    public Updater? Updater { get; set; }

    /// <summary>Set by the tray context: saves state and exits the app.</summary>
    public Action? ExitRequested { get; set; }

    public MainForm(Engine engine, Icon icon)
    {
        _engine = engine;
        Text = $"Steam LZX Tray {AppInfo.Version}";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        // Logical (96-dpi) sizes converted for the monitor's DPI; the status tab later grows/shrinks the height to fit.
        Size = new Size(LogicalToDeviceUnits(720), LogicalToDeviceUnits(620));
        MinimumSize = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(400));
        ShowInTaskbar = true;

        var tabs = _tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(_statusPage = BuildStatusTab());
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
        _libs.Resize += (_, _) => FitLibraryColumns();
        _libs.HandleCreated += (_, _) => FitLibraryList();
        DpiChanged += (_, _) => BeginInvoke(new Action(() => { ApplyDpiSizes(); FitLibraryList(); }));
        ApplyDpiSizes();
        _timer.Tick += (_, _) => RefreshStatus();
        VisibleChanged += (_, _) =>
        {
            if (Visible) { LoadSettingsToUi(); RefreshLibraries(); RefreshStatus(); _lastLibRows = -1; FitLibraryList(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    private TabPage BuildStatusTab()
    {
        var page = new TabPage("Status") { Padding = new Padding(8), UseVisualStyleBackColor = true };

        // Two-column grid of short "Name: value" rows.
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        EllipsisLabel Row(string name, bool path = false, Color? color = null)
        {
            var n = new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 1, 8, 1) };
            var v = new EllipsisLabel(_tips, path);
            if (color is Color c) v.ForeColor = c;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(n);
            grid.Controls.Add(v);
            return v;
        }
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(_lblState);
        grid.SetColumnSpan(_lblState, 2);
        _valWait = Row("Waiting for:", color: Color.DarkGoldenrod);
        _valLibrary = Row("Library:", path: true);
        _valGame = Row("Game / folder:");
        _valFile = Row("File:", path: true);
        _valNext = Row("Next scheduled pass:");
        _valLast = Row("Last pass finished:");
        _valQueued = Row("Queued Steam updates:");
        _valGames = Row("Fully compressed:");
        _valRun = Row("This pass:");
        _valAllTime = Row("All time:");
        _valSkip = Row("Skip list:");
        _valCpu = Row("CPU (other apps):");
        _valDriveOther = Row("Drive (other apps):");
        _valDriveTotal = Row("Drive (total):");
        _valVersion = Row("Version:");
        _valUpdate = Row("Update check:");

        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 6), Margin = new Padding(0) };
        buttons.Controls.AddRange(new Control[] { _btnRun, _btnPause, _btnRescan, _btnClearSkip, _btnResetLog });

        var libsLabel = new Label { Text = "Libraries and folders", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 4, 3, 2) };

        _libs.Columns.Add("Type");
        _libs.Columns.Add("Path");
        _libs.Columns.Add("FS");
        _libs.Columns.Add("Status");
        _libs.Margin = new Padding(3, 0, 3, 3);

        // Outer layout: everything auto-sized from the top; a filler row takes leftover space.
        var outer = _statusOuter = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0) };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.Controls.Add(grid, 0, 0);
        outer.Controls.Add(buttons, 0, 1);
        outer.Controls.Add(libsLabel, 0, 2);
        outer.Controls.Add(_libs, 0, 3);
        outer.Controls.Add(new Panel { Margin = new Padding(0), Height = 0 }, 0, 4);
        page.Controls.Add(outer);
        return page;
    }

    /// <summary>Convert the few fixed pixel sizes from 96-dpi logical units for the current monitor DPI.</summary>
    private void ApplyDpiSizes()
    {
        int S(int v) => LogicalToDeviceUnits(v);
        _numCpu.Width = S(70); _numDrive.Width = S(70); _numMinKb.Width = S(90);
        _dtDaily.Width = S(80); _numHours.Width = S(60); _txtExcluded.Width = S(140);
        MinimumSize = new Size(S(520), S(400));
    }

    /// <summary>List height = header + rows (1..10) + border; scrollbar only beyond 10. Then fit the window height.</summary>
    private void FitLibraryList()
    {
        int rows = Math.Clamp(_libs.Items.Count, 1, 10);
        FitLibraryColumns(); // Path column fills the width, so there is no horizontal scrollbar to account for
        int border = _libs.IsHandleCreated ? Math.Max(0, _libs.Height - _libs.ClientSize.Height) : SystemInformation.Border3DSize.Height * 2;
        int h = ListViewMetrics.HeaderHeight(_libs) + rows * ListViewMetrics.RowHeight(_libs) + border + 1;
        if (_libs.Height != h) _libs.Height = h;
        FitLibraryColumns(); // vertical scrollbar (>10 rows) changes the client width
        if (rows != _lastLibRows) { _lastLibRows = rows; FitWindowHeight(); }
    }

    /// <summary>Type/FS/Status sized to content; Path takes the remaining width (no horizontal scrollbar).</summary>
    private void FitLibraryColumns()
    {
        if (_libs.Columns.Count < 4) return;
        int pad = LogicalToDeviceUnits(14);
        int W(string t) => TextRenderer.MeasureText(t, _libs.Font).Width + pad;
        var items = _libs.Items.Cast<ListViewItem>().ToList();
        int type = Math.Max(W("Custom"), W("Type"));
        int fs = Math.Max(W("NTFS"), items.Select(i => W(i.SubItems[2].Text)).DefaultIfEmpty(0).Max());
        int status = Math.Min(LogicalToDeviceUnits(260), Math.Max(W("Status"), items.Select(i => W(i.SubItems[3].Text)).DefaultIfEmpty(0).Max()));
        int path = Math.Max(LogicalToDeviceUnits(120), _libs.ClientSize.Width - type - fs - status - 1);
        _libs.BeginUpdate();
        _libs.Columns[0].Width = type; _libs.Columns[1].Width = path; _libs.Columns[2].Width = fs; _libs.Columns[3].Width = status;
        _libs.EndUpdate();
    }

    /// <summary>Make the window tall enough for the whole Status tab (within the screen's working area).</summary>
    private void FitWindowHeight()
    {
        if (!IsHandleCreated || WindowState != FormWindowState.Normal || _statusOuter == null) return;
        int content = _statusOuter.GetPreferredSize(new Size(_statusPage.ClientSize.Width - _statusPage.Padding.Horizontal, 0)).Height;
        int chrome = _tabs.Height - _statusPage.Height + _statusPage.Padding.Vertical;
        int wantClient = content + chrome;
        int extra = Height - ClientSize.Height;
        var work = Screen.FromControl(this).WorkingArea;
        int wantHeight = Math.Min(work.Height, Math.Max(MinimumSize.Height, wantClient + extra));
        if (Height != wantHeight) Height = wantHeight;
        if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
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
        Row("Schedule:", Flow(_rbDaily, _dtDaily, new Label { Width = LogicalToDeviceUnits(16) }, _rbInterval, _numHours, Note("hours"), new Label { Width = LogicalToDeviceUnits(16) }, _rbManual));
        Row("", _chkWatch);
        Row("", _chkFullscreen);
        Row("Excluded drives:", Flow(_txtExcluded, Note("comma separated drive letters, e.g. X:, Y:  (never touched; empty = none)")));
        Row("", Flow(_chkAutoUpdate, new Label { Width = LogicalToDeviceUnits(16) }, _btnCheckUpdate));
        Row("", _chkAutostart);
        Row("", _chkShortcut);

        var customPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = LogicalToDeviceUnits(160), Margin = new Padding(0) };
        customPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        customPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var cb = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0) };
        cb.Controls.Add(_btnAddFolder); cb.Controls.Add(_btnRemoveFolder);
        customPanel.Controls.Add(_lstCustom, 0, 0);
        customPanel.Controls.Add(cb, 1, 0);
        Row("Custom folders (non-Steam):", customPanel);
        Row("", Flow(_btnSave, new Label { Width = LogicalToDeviceUnits(40) }, _btnUninstall));
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
            }) { ToolTipText = l.Path + Environment.NewLine + (l.Supported ? $"{l.CompletedCount} of {l.UnitCount} done" : l.Status) };
            if (!l.Supported) it.ForeColor = SystemColors.GrayText;
            _libs.Items.Add(it);
        }
        _libs.EndUpdate();
        FitLibraryList();
    }

    private void RefreshStatus()
    {
        var st = _engine.GetStatus();
        _lblState.Text = st.Phase switch
        {
            Phase.Working => $"Working ({st.Run} pass)",
            Phase.Waiting => $"Waiting for idle ({st.Run} pass)",
            Phase.Paused => "Paused",
            _ => "Idle",
        };
        _valWait.Text = st.Phase is Phase.Waiting or Phase.Paused && st.WaitReason.Length > 0 ? st.WaitReason : "-";
        _valLibrary.Text = st.Library.Length > 0 ? st.Library : "-";
        _valGame.Text = st.Game.Length > 0 ? st.Game : "-";
        _valFile.Text = st.File.Length > 0 ? st.File : "-";
        _valNext.Text = st.NextSchedule;
        _valLast.Text = st.LastRunCompletedUtc is DateTime d ? d.ToLocalTime().ToString("g") : "never";
        _valQueued.Text = st.PendingTriggers.ToString("N0");
        var (done, total) = _engine.CompletedTotals();
        _valGames.Text = $"{done:N0} of {total:N0} games/folders";
        _valRun.Text = $"{st.RunFilesProcessed:N0} checked, {st.RunFilesCompressed:N0} compressed, {Fmt.Bytes(st.RunBytesSaved)} saved";
        _valAllTime.Text = $"{st.TotalFilesCompressed:N0} files, {Fmt.Bytes(st.TotalBytesSaved)} saved";
        _valSkip.Text = $"{st.SkipListCount:N0} files";
        _valCpu.Text = $"{st.CpuOther:0}%";
        _valDriveOther.Text = st.DiskOtherMBps >= 0 ? $"{st.DiskOtherMBps:0.0} MB/s" : "-";
        _valDriveTotal.Text = st.DiskTotalMBps >= 0 ? $"{st.DiskTotalMBps:0.0} MB/s" : "-";
        _valVersion.Text = AppInfo.Version;
        _valUpdate.Text = Updater?.LastResult ?? "-";
        _btnPause.Text = _engine.Paused ? "Resume" : "Pause";

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
