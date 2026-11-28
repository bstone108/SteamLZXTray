using System.Text;
using Microsoft.Win32;

namespace SteamLZXTray;

/// <summary>Minimal Valve KeyValues (VDF/ACF) parser.</summary>
internal sealed class VdfNode
{
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<KeyValuePair<string, VdfNode>> Ordered { get; } = new();
    public string? Value { get; set; }

    public string? Str(string key) => Children.TryGetValue(key, out var n) ? n.Value : null;
    public VdfNode? Node(string key) => Children.TryGetValue(key, out var n) ? n : null;

    public static VdfNode Parse(string text)
    {
        int i = 0;
        var root = new VdfNode();
        ParseInto(root, text, ref i);
        return root;
    }

    private static void ParseInto(VdfNode parent, string t, ref int i)
    {
        while (true)
        {
            string? key = NextToken(t, ref i, out bool isOpen, out bool isClose);
            if (key == null && !isOpen && !isClose) return; // EOF
            if (isClose) return;
            if (isOpen || key == null) continue; // malformed; skip
            string? val = NextToken(t, ref i, out isOpen, out isClose);
            var node = new VdfNode();
            if (isOpen) ParseInto(node, t, ref i);
            else if (isClose) { node.Value = ""; Add(parent, key, node); return; }
            else node.Value = val ?? "";
            Add(parent, key, node);
        }
    }

    private static void Add(VdfNode parent, string key, VdfNode node)
    {
        parent.Children[key] = node;
        parent.Ordered.Add(new(key, node));
    }

    private static string? NextToken(string t, ref int i, out bool open, out bool close)
    {
        open = close = false;
        while (i < t.Length)
        {
            char c = t[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; continue; }
            if (c == '{') { i++; open = true; return null; }
            if (c == '}') { i++; close = true; return null; }
            if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < t.Length && t[i] != '"')
                {
                    if (t[i] == '\\' && i + 1 < t.Length)
                    {
                        char n = t[i + 1];
                        sb.Append(n switch { 'n' => '\n', 't' => '\t', _ => n });
                        i += 2;
                    }
                    else sb.Append(t[i++]);
                }
                i++; // closing quote
                return sb.ToString();
            }
            // unquoted token
            int s = i;
            while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != '{' && t[i] != '}' && t[i] != '"') i++;
            return t.Substring(s, i - s);
        }
        return null;
    }
}

internal sealed class AcfInfo
{
    public string AppId = "", Name = "", InstallDir = "", BuildId = "", LastUpdated = "", SizeOnDisk = "";
    public int StateFlags;
    public string ManifestPath = "";

    public static AcfInfo? Read(string path)
    {
        try
        {
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                text = sr.ReadToEnd();
            var root = VdfNode.Parse(text);
            var app = root.Node("AppState");
            if (app == null) return null;
            var a = new AcfInfo
            {
                AppId = app.Str("appid") ?? "",
                Name = app.Str("name") ?? "",
                InstallDir = app.Str("installdir") ?? "",
                BuildId = app.Str("buildid") ?? "",
                LastUpdated = app.Str("LastUpdated") ?? "",
                SizeOnDisk = app.Str("SizeOnDisk") ?? "",
                ManifestPath = path,
            };
            int.TryParse(app.Str("StateFlags"), out a.StateFlags);
            return string.IsNullOrEmpty(a.AppId) || string.IsNullOrEmpty(a.InstallDir) ? null : a;
        }
        catch { return null; }
    }
}

internal enum LibraryKind { Steam, Custom }

/// <summary>A unit of work: one Steam game folder or one top-level subfolder of a custom folder.</summary>
internal sealed class WorkUnit
{
    public string Key = "";          // stable key used for resume + logs
    public string DisplayName = "";
    public string Root = "";         // directory to walk
    public bool Recursive = true;    // false for the loose-files unit of a custom folder
    public LibraryInfo Library = null!;
    public AcfInfo? Acf;
}

internal sealed class LibraryInfo
{
    public LibraryKind Kind;
    public string Path = "";          // library root (Steam) or custom folder
    public string WalkRoot = "";      // steamapps\common or the custom folder itself
    public string SteamAppsDir = "";
    public string VolumeRoot = "";
    public string FileSystem = "";
    public bool Supported;
    public string Status = "";
    public int UnitCount;
    public int CompletedCount;
}

internal static class LibraryDiscovery
{
    public static string? FindSteamPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string p && Directory.Exists(p)) return System.IO.Path.GetFullPath(p);
        }
        catch { }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (k?.GetValue("InstallPath") is string p && Directory.Exists(p)) return System.IO.Path.GetFullPath(p);
        }
        catch { }
        return null;
    }

    public static List<LibraryInfo> Discover(AppSettings settings)
    {
        var result = new List<LibraryInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? steam = FindSteamPath();
        if (steam == null) Log.Write("Steam install not found in registry.");
        else
        {
            var paths = new List<string> { steam };
            string vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                {
                    var root = VdfNode.Parse(File.ReadAllText(vdf));
                    var lf = root.Node("libraryfolders") ?? root.Node("LibraryFolders");
                    if (lf != null)
                        foreach (var kv in lf.Ordered)
                        {
                            if (!int.TryParse(kv.Key, out _)) continue;
                            string? p = kv.Value.Value ?? kv.Value.Str("path"); // old format: "1" "D:\\Lib"
                            if (!string.IsNullOrWhiteSpace(p)) paths.Add(p);
                        }
                }
                else Log.Write($"libraryfolders.vdf not found at {vdf}");
            }
            catch (Exception ex) { Log.Write($"Failed to parse {vdf}: {ex.Message}"); }

            foreach (var raw in paths)
            {
                string p;
                try { p = System.IO.Path.GetFullPath(raw).TrimEnd('\\'); } catch { continue; }
                if (!seen.Add(p)) continue;
                var lib = new LibraryInfo
                {
                    Kind = LibraryKind.Steam,
                    Path = p,
                    SteamAppsDir = System.IO.Path.Combine(p, "steamapps"),
                    WalkRoot = System.IO.Path.Combine(p, "steamapps", "common"),
                };
                CheckVolume(lib, settings);
                result.Add(lib);
            }
        }

        foreach (var raw in settings.CustomFolders)
        {
            string p;
            try { p = System.IO.Path.GetFullPath(raw).TrimEnd('\\'); } catch { continue; }
            if (!seen.Add(p)) continue;
            var lib = new LibraryInfo { Kind = LibraryKind.Custom, Path = p, WalkRoot = p };
            CheckVolume(lib, settings);
            result.Add(lib);
        }
        return result;
    }

    private static void CheckVolume(LibraryInfo lib, AppSettings settings)
    {
        lib.Supported = false;
        if (!Directory.Exists(lib.WalkRoot)) { lib.Status = "Folder not found"; return; }
        if (settings.IsExcluded(lib.WalkRoot)) { lib.Status = "Excluded drive (settings)"; return; }
        string? vol = Native.VolumeRoot(lib.WalkRoot);
        if (vol == null) { lib.Status = "Cannot resolve volume"; return; }
        lib.VolumeRoot = vol;
        if (settings.IsExcluded(vol)) { lib.Status = "Excluded drive (settings)"; return; }
        var fsName = new StringBuilder(64);
        if (!Native.GetVolumeInformationW(vol, null, 0, out _, out _, out uint flags, fsName, fsName.Capacity))
        { lib.Status = "GetVolumeInformation failed"; return; }
        lib.FileSystem = fsName.ToString();
        if ((flags & Native.FILE_READ_ONLY_VOLUME) != 0) { lib.Status = "Read-only volume"; return; }
        if (!lib.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        { lib.Status = $"Unsupported FS ({lib.FileSystem}) - WOF needs NTFS"; return; }
        if ((flags & Native.FILE_FILE_COMPRESSION) == 0) { lib.Status = "Volume reports no compression support"; return; }
        uint dt = Native.GetDriveTypeW(vol);
        if (dt == 4) { lib.Status = "Network drive (not supported)"; return; }
        lib.Supported = true;
        lib.Status = "OK";
    }

    /// <summary>Build ordered work units for all supported libraries.</summary>
    public static List<WorkUnit> BuildUnits(List<LibraryInfo> libs)
    {
        var units = new List<WorkUnit>();
        foreach (var lib in libs)
        {
            if (!lib.Supported) { lib.UnitCount = 0; continue; }
            var libUnits = new List<WorkUnit>();
            if (lib.Kind == LibraryKind.Steam)
            {
                IEnumerable<string> manifests;
                try { manifests = Directory.EnumerateFiles(lib.SteamAppsDir, "appmanifest_*.acf").ToList(); }
                catch (Exception ex) { Log.Write($"Cannot list manifests in {lib.SteamAppsDir}: {ex.Message}"); manifests = Array.Empty<string>(); }
                var acfs = manifests.Select(AcfInfo.Read).Where(a => a != null).Select(a => a!)
                    .OrderBy(a => long.TryParse(a.AppId, out var n) ? n : long.MaxValue).ThenBy(a => a.AppId, StringComparer.Ordinal);
                foreach (var a in acfs)
                {
                    string dir = System.IO.Path.Combine(lib.WalkRoot, a.InstallDir);
                    if (!Directory.Exists(dir)) continue;
                    libUnits.Add(new WorkUnit
                    {
                        Key = SteamKey(lib.Path, a.AppId),
                        DisplayName = string.IsNullOrEmpty(a.Name) ? a.InstallDir : a.Name,
                        Root = dir,
                        Library = lib,
                        Acf = a,
                    });
                }
            }
            else
            {
                List<string> subs;
                try { subs = Directory.EnumerateDirectories(lib.WalkRoot).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(); }
                catch (Exception ex) { Log.Write($"Cannot list {lib.WalkRoot}: {ex.Message}"); subs = new(); }
                libUnits.Add(new WorkUnit { Key = lib.WalkRoot + @"\.", DisplayName = "(loose files)", Root = lib.WalkRoot, Recursive = false, Library = lib });
                foreach (var s in subs)
                {
                    try
                    {
                        var attrs = File.GetAttributes(s);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) continue; // don't follow junctions/symlinks
                    }
                    catch { continue; }
                    libUnits.Add(new WorkUnit { Key = s, DisplayName = System.IO.Path.GetFileName(s), Root = s, Library = lib });
                }
            }
            lib.UnitCount = libUnits.Count;
            units.AddRange(libUnits);
        }
        return units;
    }

    public static string SteamKey(string libPath, string appId) => libPath + "|" + appId;

    /// <summary>Every readable appmanifest in supported Steam libraries, including games not fully installed.</summary>
    public static List<(string Key, AcfInfo Acf)> ReadSupportedSteamAcfs(List<LibraryInfo> libs)
    {
        var list = new List<(string, AcfInfo)>();
        foreach (var lib in libs.Where(l => l.Kind == LibraryKind.Steam && l.Supported && Directory.Exists(l.SteamAppsDir)))
        {
            List<string> manifests;
            try { manifests = Directory.EnumerateFiles(lib.SteamAppsDir, "appmanifest_*.acf").ToList(); }
            catch (Exception ex) { Log.Write($"Cannot list manifests in {lib.SteamAppsDir}: {ex.Message}"); continue; }
            foreach (var path in manifests)
            {
                var a = AcfInfo.Read(path);
                if (a != null) list.Add((SteamKey(lib.Path, a.AppId), a));
            }
        }
        return list;
    }
}
