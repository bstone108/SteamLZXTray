using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SteamLZXTray;

/// <summary>Autostart (HKCU Run, fixed value name) and the single canonical Start Menu shortcut. Both idempotent.</summary>
internal static class Integration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "SteamLZXTray";
    public const string ShortcutBaseName = "Steam LZX Tray";

    /// <summary>Run value and shortcut always point at the canonical install path.</summary>
    public static string ExePath => Installer.InstallPath;
    private static string RunCommand => $"\"{ExePath}\" --tray";

    public static bool IsAutoStartEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return k?.GetValue(RunValueName) is string s && s.Length > 0;
    }

    public static void SetAutoStart(bool enable)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enable) k.SetValue(RunValueName, RunCommand, RegistryValueKind.String); // overwrite in place, never a second value
        else if (k.GetValue(RunValueName) != null) k.DeleteValue(RunValueName, false);
        Log.Write(enable ? $"Autostart enabled: {RunCommand}" : "Autostart disabled.");
    }

    /// <summary>On launch: if autostart is on but points at an old exe path, rewrite the same value.</summary>
    public static void RepairAutoStart()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k?.GetValue(RunValueName) is string cur && !cur.Equals(RunCommand, StringComparison.OrdinalIgnoreCase))
            {
                k.SetValue(RunValueName, RunCommand, RegistryValueKind.String);
                Log.Write($"Autostart entry updated to {RunCommand}");
            }
        }
        catch (Exception ex) { Log.Write("Autostart repair failed: " + ex.Message); }
    }

    public static string ProgramsDir => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public static string ShortcutPath => Path.Combine(ProgramsDir, ShortcutBaseName + ".lnk");

    public static bool IsShortcutInstalled() => File.Exists(ShortcutPath);

    /// <summary>Create/overwrite the one canonical Start Menu shortcut, remove strays. Never creates a desktop shortcut.</summary>
    public static void InstallShortcut()
    {
        RemoveStrayShortcuts();
        Type t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        object shell = Activator.CreateInstance(t)!;
        try
        {
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath })!;
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { ExePath });
            lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "" });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(ExePath) ?? "" });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { ExePath + ",0" });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Steam LZX Tray - background WOF LZX compression" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            Marshal.FinalReleaseComObject(lnk);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        Log.Write($"Start Menu shortcut written: {ShortcutPath}");
    }

    public static void UninstallShortcut()
    {
        RemoveStrayShortcuts();
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch (Exception ex) { Log.Write("Shortcut delete failed: " + ex.Message); }
        Log.Write("Start Menu shortcut removed.");
    }

    /// <summary>Delete any 'Steam LZX Tray*.lnk' in Start Menu Programs other than the canonical one.</summary>
    public static void RemoveStrayShortcuts()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(ProgramsDir, ShortcutBaseName + "*.lnk", SearchOption.TopDirectoryOnly))
            {
                if (f.Equals(ShortcutPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); Log.Write($"Removed stray shortcut {f}"); } catch (Exception ex) { Log.Write($"Could not remove {f}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Write("Stray shortcut scan failed: " + ex.Message); }
    }

    /// <summary>Make the Start Menu match the setting: one canonical shortcut (overwritten in place) or none. Strays always removed.</summary>
    public static void ApplyShortcutSetting(bool wanted)
    {
        try { if (wanted) InstallShortcut(); else UninstallShortcut(); }
        catch (Exception ex) { Log.Write("Shortcut update failed: " + ex.Message); }
    }
}
