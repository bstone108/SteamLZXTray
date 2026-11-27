using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

    // No-internet handling: instead of reporting a failure, wait until the network has been up for 2 minutes, then retry.
    private static readonly TimeSpan NetworkStableFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(15);
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _netChanged = new(false);
    private volatile bool _netAvailable = true;
    private int _waitingForNetwork; // 1 while the wait loop runs

    public Updater(Func<AppSettings> settings)
    {
        _settings = settings;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(manual: false, forceInstall: false), null, FirstDelay, Interval);
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        _netAvailable = e.IsAvailable;
        try { _netChanged.Set(); } catch { }
    }

    /// <summary>True when the exception means GitHub couldn't be reached at all (offline, DNS, connect failure, timeout).</summary>
    private static bool IsNoNetwork(Exception ex)
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) return true;
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is HttpRequestException hre && hre.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
                return true;
            if (e is SocketException se && se.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                    or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostUnreachable or SocketError.HostDown
                    or SocketError.TimedOut or SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.ConnectionAborted)
                return true;
            if (e is TaskCanceledException or TimeoutException) return true; // HttpClient timeout
        }
        return false;
    }

    private void StartWaitingForNetwork()
    {
        SetProgress("waiting for network");
        if (Interlocked.Exchange(ref _waitingForNetwork, 1) == 1) return;
        Log.Write("Update check: no network; waiting until it has been up for 2 minutes.");
        _ = Task.Run(WaitForNetworkThenCheck);
    }

    private async Task WaitForNetworkThenCheck()
    {
        var token = _cts.Token;
        try
        {
            DateTime? upSince = null;
            while (!token.IsCancellationRequested)
            {
                bool up = _netAvailable && NetworkInterface.GetIsNetworkAvailable() && await ProbeGitHub(token);
                if (!up) upSince = null;
                else
                {
                    upSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - upSince.Value >= NetworkStableFor) break;
                }
                // Sleep until the next probe; a network change wakes us early (going down resets the 2-minute window).
                await Task.Run(() => WaitHandle.WaitAny(new[] { _netChanged, token.WaitHandle }, ProbeEvery), CancellationToken.None);
            }
        }
        catch (Exception ex) { Log.Write("Update check: network wait error: " + ex.Message); }
        finally { Interlocked.Exchange(ref _waitingForNetwork, 0); }

        if (token.IsCancellationRequested) return;
        Log.Write("Update check: network has been up for 2 minutes; retrying.");
        await CheckAsync(manual: false, forceInstall: false);
    }

    /// <summary>Any HTTP response from api.github.com means the network path works.</summary>
    private static async Task<bool> ProbeGitHub(CancellationToken token)
    {
        try
        {
            using var http = NewClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var req = new HttpRequestMessage(HttpMethod.Head, "https://api.github.com/");
            using var resp = await http.SendAsync(req, token);
            return true;
        }
        catch { return false; }
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
        catch (Exception ex) when (IsNoNetwork(ex))
        {
            StartWaitingForNetwork(); // not a failure: retry once the network has been up for 2 minutes
            return null;
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

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        try { _cts.Cancel(); } catch { }
        _timer.Dispose();
        _busy.Dispose();
    }
}
