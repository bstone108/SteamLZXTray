using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SteamLZXTray;

/// <summary>
/// Background auto-update from GitHub Releases (latest, non-draft, non-prerelease).
/// Downloads SteamLZXTray.exe + SHA256SUMS.txt, verifies, then launches the download with --update;
/// the normal installer path stops this instance (state saved), replaces the exe and relaunches.
/// </summary>
internal sealed class Updater : IDisposable
{
    public const string LatestUrl = "https://api.github.com/repos/bstone108/SteamLZXTray/releases/latest";
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly Func<AppSettings> _settings;
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly object _lock = new();
    private string _lastResult = "not checked yet";

    public static string UpdatesDir => Path.Combine(AppPaths.Dir, "updates");

    /// <summary>Raised (thread pool) right before the downloaded installer is launched.</summary>
    public event Action<string>? InstallStarting;
    /// <summary>Raised when a newer version is found but auto-install is off.</summary>
    public event Action<string>? UpdateAvailable;

    public Updater(Func<AppSettings> settings)
    {
        _settings = settings;
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(manual: false, forceInstall: false), null, FirstDelay, Interval);
    }

    public string LastResult { get { lock (_lock) return _lastResult; } }
    private void SetResult(string s) { lock (_lock) _lastResult = $"{DateTime.Now:g}: {s}"; Log.Write("Update check: " + s); }
    /// <summary>Transient progress shown live in Status (not logged).</summary>
    private void SetProgress(string s) { lock (_lock) _lastResult = s; }

    /// <summary>Running version as 4 numeric parts (test/dev builds that don't parse count as 0.0.0.0).</summary>
    public static Version RunningVersion => ParseVersion(AppInfo.Version) ?? new Version(0, 0, 0, 0);

    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) return null; // prerelease-style strings are never treated as releases
        var parts = s.Split('.');
        if (parts.Length != 4) return null;
        var n = new int[4];
        for (int i = 0; i < 4; i++) if (!int.TryParse(parts[i], out n[i]) || n[i] < 0) return null;
        return new Version(n[0], n[1], n[2], n[3]);
    }

    /// <summary>
    /// Check GitHub. Returns the newer tag if one exists. Installs when auto-install is on or forceInstall.
    /// </summary>
    public async Task<string?> CheckAsync(bool manual, bool forceInstall)
    {
        if (!await _busy.WaitAsync(0)) return null;
        try
        {
            SetProgress("checking GitHub...");
            using var http = NewClient();
            using var resp = await http.GetAsync(LatestUrl);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { SetResult("no published release yet"); return null; }
            if (!resp.IsSuccessStatusCode) { SetResult($"GitHub API returned {(int)resp.StatusCode} {resp.ReasonPhrase}"); return null; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if ((root.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) ||
                (root.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True))
            { SetResult("latest release is a draft/prerelease; ignored"); return null; }

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var remote = ParseVersion(tag);
            var local = RunningVersion;
            if (remote == null) { SetResult($"latest tag '{tag}' is not a 4-part version; ignored"); return null; }
            if (remote <= local) { SetResult($"up to date ({AppInfo.Version}; latest {tag})"); CleanupUpdates(); return null; }

            string? exeUrl = null, sumsUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                    string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    if (name.Equals("SteamLZXTray.exe", StringComparison.OrdinalIgnoreCase)) exeUrl = url;
                    else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sumsUrl = url;
                }
            if (exeUrl == null || sumsUrl == null) { SetResult($"{tag} is newer but its assets are incomplete; skipped"); return null; }

            if (!(_settings().AutoInstallUpdates || forceInstall))
            {
                SetResult($"update available: {tag} (automatic install is off)");
                UpdateAvailable?.Invoke(tag);
                return tag;
            }

            await DownloadVerifyAndLaunch(http, tag, exeUrl, sumsUrl);
            return tag;
        }
        catch (Exception ex)
        {
            SetResult("failed: " + ex.Message);
            Log.Write("Update check exception: " + ex);
            return null;
        }
        finally { _busy.Release(); }
    }

    private async Task DownloadVerifyAndLaunch(HttpClient http, string tag, string exeUrl, string sumsUrl)
    {
        string dir = Path.Combine(UpdatesDir, tag.TrimStart('v', 'V'));
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "SteamLZXTray.exe");
        string sums = Path.Combine(dir, "SHA256SUMS.txt");
        Log.Write($"Downloading update {tag} to {dir}");
        SetProgress($"downloading {tag}...");
        await Download(http, sumsUrl, sums);
        await Download(http, exeUrl, exe);

        string? expected = null;
        foreach (var line in File.ReadAllLines(sums))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim().Equals("SteamLZXTray.exe", StringComparison.OrdinalIgnoreCase))
                expected = parts[0].Trim().ToLowerInvariant();
        }
        SetProgress($"verifying {tag}...");
        string actual;
        using (var fs = File.OpenRead(exe)) actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        if (expected == null || expected.Length != 64 || actual != expected)
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) { Log.Write("Could not delete bad download: " + ex.Message); }
            SetResult($"{tag} download FAILED verification (expected {expected ?? "none"}, got {actual}); deleted, not installed");
            return;
        }
        SetResult($"{tag} downloaded and verified (SHA256 {actual}); installing");
        InstallStarting?.Invoke(tag);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir };
        psi.ArgumentList.Add("--update");
        Process.Start(psi);
        // The launched installer signals this instance to save state and exit.
    }

    private static async Task Download(HttpClient http, string url, string path)
    {
        string tmp = path + ".part";
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            await src.CopyToAsync(dst);
            await dst.FlushAsync();
        }
        File.Move(tmp, path, true);
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SteamLZXTray", Sanitize(AppInfo.Version)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    private static string Sanitize(string v)
    {
        var s = new string(v.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-').ToArray());
        return s.Length > 0 ? s : "dev";
    }

    /// <summary>Remove old downloads (best effort; the installer that ran from there has exited by now).</summary>
    public static void CleanupUpdates()
    {
        try
        {
            if (!Directory.Exists(UpdatesDir)) return;
            foreach (var d in Directory.GetDirectories(UpdatesDir))
            {
                var v = ParseVersion(Path.GetFileName(d));
                if (v == null || v <= RunningVersion)
                    try { Directory.Delete(d, true); } catch { /* still in use; next time */ }
            }
        }
        catch { }
    }

    public void Dispose() { _timer.Dispose(); _busy.Dispose(); }
}
