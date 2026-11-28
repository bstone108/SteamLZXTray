using System.Text;

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
