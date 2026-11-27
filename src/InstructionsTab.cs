using System.Drawing;

namespace SteamLZXTray;

/// <summary>Read-only "Instructions" tab: short plain-language help for every setting and button.</summary>
internal static class InstructionsTab
{
    private static readonly (string Heading, string[] Lines)[] Sections =
    {
        ("What this app does", new[]
        {
            "It compresses your Steam games (and any custom folders you add) with Windows' built-in LZX compression, the same as \"compact /exe:lzx\".",
            "Games still run normally. They just take less disk space.",
            "It only works when the PC is idle enough, and it pauses itself whenever you need the PC.",
        }),
        ("How passes work", new[]
        {
            "A pass walks your libraries file by file and compresses anything that isn't compressed yet.",
            "A pass starts from the schedule, from a detected Steam update (if that box is ticked), or when you press Run Now.",
            "Once started, a pass keeps going until it's finished, even across restarts, reboots and power loss. It picks up where it left off.",
            "When a pass finishes, the app goes idle and waits for the next schedule time, Steam update or Run Now.",
            "Scheduled and update passes skip games already fully compressed at their current Steam build. A game Steam has updated gets compressed again.",
            "Files that are in use are skipped for now and retried later. Files that don't shrink go on the skip list and aren't tried again unless they change.",
            "Between every file it waits if: CPU is busy, the drive is busy, a game is in the foreground, or you paused it. After a wait, things must stay quiet for about 5 seconds before it continues.",
        }),
        ("Tray icon", new[]
        {
            "Spinning blue disc with a green arrow: compressing right now.",
            "Amber hourglass: a pass is running but waiting for the PC to be idle. Hover over the icon (or open the window) to see why.",
            "Grey with pause bars: paused by you.",
            "Still blue disc: idle, nothing to do until the next pass.",
            "Left-click the icon to open this window. Right-click for Open, Run Now, Pause/Resume and Exit.",
            "Closing this window only hides it to the tray. Use Exit in the tray menu to quit.",
        }),
        ("Settings (click \"Save settings\" to apply)", new[]
        {
            "Max CPU load (other processes): the app waits while other programs use more CPU than this percentage. Its own CPU use isn't counted. Default 30%.",
            "Max drive activity (other processes): the app waits while other programs read or write more than this many MB/s on the drive being compressed. Its own disk use isn't counted. Default 50 MB/s.",
            "Minimum file size: files smaller than this (in KB) are skipped. Default 4 KB.",
            "Schedule - Daily at: start a pass once a day at this time. If the PC was off at that time, one pass starts next time the app runs.",
            "Schedule - Every N hours: start a pass when this many hours have passed since the last scheduled pass.",
            "Schedule - Manual only: no scheduled passes. Passes start only from Run Now or a Steam update.",
            "Watch for Steam updates: when Steam finishes installing or updating a game, that game is queued and compressed at the next idle moment. Untick to ignore Steam updates.",
            "Treat any fullscreen app in the foreground as a game: when ticked, any fullscreen window (a video player, for example) pauses work, not just games from your libraries.",
            "Excluded drives: drive letters to never touch, separated by commas (for example X:, Y:). Libraries on those drives show as Skipped. Empty means no drives are excluded.",
            "Automatically install updates: checks GitHub about 2 minutes after startup and then every 6 hours. If a newer release exists, it downloads it, checks its SHA256 and installs it silently. Untick to only be told an update is available.",
            "Start with Windows: starts the app in the tray when you sign in. This applies right away, no Save needed.",
            "Start Menu shortcut: keeps one \"Steam LZX Tray\" shortcut in the Start Menu. This applies right away, no Save needed.",
            "Custom folders (non-Steam): extra folders to compress, such as other game launchers' libraries. Add opens a folder picker. Remove deletes the selected folder from the list. Both save immediately.",
            "Custom folders are tracked per top-level subfolder. Unchanged subfolders are skipped on scheduled passes.",
        }),
        ("Buttons", new[]
        {
            "Run Now: starts a full pass right away over everything, ignoring the record of finished games. Already compressed files and skip-listed files are still passed over quickly. It also un-pauses the app.",
            "Pause / Resume: stops work after the current file, or continues it. A running pass stays saved while paused.",
            "Rescan libraries: re-reads your Steam libraries and custom folders and refreshes the list on the Status tab.",
            "Clear skip list: forgets the files that didn't compress, so they're tried again on the next pass.",
            "Reset game log: forgets which games and folders are finished, so the next scheduled pass checks everything again.",
            "Check for updates now: checks GitHub immediately. With automatic install on, a newer version installs straight away. With it off, you're asked first.",
            "Save settings: saves and applies the settings on the Settings tab.",
            "Uninstall...: removes the Start with Windows entry, the Start Menu shortcut and the program folder, then closes the app. It asks whether to keep your data (settings, logs, game log, skip list). Compressed files stay compressed and keep working.",
        }),
        ("Good to know", new[]
        {
            "Steam updates and \"Verify integrity of game files\" write files back uncompressed. The next pass compresses them again.",
            "File dates aren't changed by compression, so backup tools that look at modified dates won't see changes.",
            "Your data (settings, progress, logs) is kept in %LOCALAPPDATA%\\SteamLZXTray. The program is in %LOCALAPPDATA%\\Programs\\SteamLZXTray.",
        }),
    };

    public static TabPage Build()
    {
        var page = new TabPage("Instructions") { Padding = new Padding(6) };
        var box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            WordWrap = true,
            TabStop = false,
        };
        var baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        // Fonts live as long as the window (RichTextBox converts them to RTF formatting).
        var bodyFont = new Font(baseFont.FontFamily, 9.5f, FontStyle.Regular);
        var headFont = new Font(baseFont.FontFamily, 11f, FontStyle.Bold);

        foreach (var (heading, lines) in Sections)
        {
            box.SelectionFont = headFont;
            box.AppendText(heading + "\n");
            box.SelectionFont = bodyFont;
            box.SelectionBullet = true;
            box.SelectionIndent = 8;
            foreach (var line in lines) box.AppendText(line + "\n");
            box.SelectionBullet = false;
            box.SelectionIndent = 0;
            box.AppendText("\n");
        }
        box.SelectionStart = 0;
        box.ScrollToCaret();
        page.Controls.Add(box);
        return page;
    }
}
