using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastTaskMgr.Core.Updates;

internal sealed class UpdateService : IDisposable
{
    private const string LatestReleaseApi = "https://api.github.com/repos/rockenrooster/FastTaskMgr/releases/latest";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new();
    private readonly Lock _lock = new();
    private Task<UpdateCheckResult>? _checkTask;
    private bool _isChecking;
    private bool _isDownloading;
    private double _downloadProgress;
    private string? _downloadedFile;

    public UpdateService()
    {
        Version rawVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        CurrentVersion = Normalize(rawVersion);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"FastTaskMgr/{CurrentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public event EventHandler? StatusChanged;

    public Version CurrentVersion { get; }
    public UpdateCheckResult? LastResult { get; private set; }

    public bool IsChecking
    {
        get
        {
            lock (_lock)
            {
                return _isChecking;
            }
        }
    }

    public bool IsDownloading
    {
        get
        {
            lock (_lock)
            {
                return _isDownloading;
            }
        }
    }

    public double DownloadProgress
    {
        get
        {
            lock (_lock)
            {
                return _downloadProgress;
            }
        }
    }

    public string? DownloadedFile
    {
        get
        {
            lock (_lock)
            {
                return _downloadedFile;
            }
        }
    }

    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_checkTask is { IsCompleted: false })
            {
                return _checkTask;
            }

            _checkTask = CheckCoreAsync(cancellationToken);
            return _checkTask;
        }
    }

    public async Task<string> DownloadLatestAsync(CancellationToken cancellationToken = default)
    {
        UpdateCheckResult result = LastResult ?? throw new InvalidOperationException("Check for updates before downloading.");
        if (!result.CanDownload || result.DownloadUrl is null)
        {
            throw new InvalidOperationException("No downloadable update is available.");
        }

        SetDownloading(true, 0, null);
        string updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastTaskMgr",
            "Updates");
        Directory.CreateDirectory(updateDir);

        string version = CleanFileName(result.LatestVersionText);
        string assetName = result.AssetName ?? "FastTaskMgr.exe";
        string? extension = Path.GetExtension(assetName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".exe";
        }

        string targetPath = Path.Combine(updateDir, $"FastTaskMgr-{version}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{extension}");
        string tempPath = targetPath + ".download";

        try
        {
            using HttpResponseMessage response = await _http.GetAsync(result.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream output = File.Create(tempPath);

            byte[] buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (total is > 0)
                {
                    SetDownloadProgress(received / (double)total.Value * 100);
                }
            }

            File.Move(tempPath, targetPath, overwrite: true);
            SetDownloading(false, 100, targetPath);
            return targetPath;
        }
        catch
        {
            TryDelete(tempPath);
            SetDownloading(false, 0, null);
            throw;
        }
    }

    public void InstallDownloadedUpdate(string appPath)
    {
        string sourcePath = DownloadedFile ?? throw new InvalidOperationException("Download the update before installing.");
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Downloaded update file was not found.", sourcePath);
        }

        string script = $"""
        $ErrorActionPreference = 'Stop'
        Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue
        Copy-Item -LiteralPath '{EscapePowerShell(sourcePath)}' -Destination '{EscapePowerShell(appPath)}' -Force
        Start-Process -FilePath '{EscapePowerShell(appPath)}'
        """;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(startInfo);
    }

    public void Dispose() => _http.Dispose();

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        SetChecking(true);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(LatestReleaseApi, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return StoreResult(UpdateCheckResult.NotFound(CurrentVersion));
            }

            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            GitHubRelease? release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return StoreResult(UpdateCheckResult.Error(CurrentVersion, "GitHub returned an empty release response."));
            }

            string versionText = release.TagName.Trim();
            if (!TryParseVersion(versionText, out Version? latestVersion))
            {
                return StoreResult(UpdateCheckResult.Error(CurrentVersion, $"Latest release tag {versionText} is not a version."));
            }

            GitHubAsset? asset = SelectAsset(release.Assets);
            Version normalizedLatest = Normalize(latestVersion!);
            bool updateAvailable = normalizedLatest.CompareTo(CurrentVersion) > 0;
            string message = updateAvailable
                ? asset is null ? "Update found, but no FastTaskMgr.exe release asset was found." : "Update available."
                : "FastTaskMgr is up to date.";

            return StoreResult(new UpdateCheckResult(
                CurrentVersion,
                normalizedLatest,
                versionText,
                updateAvailable,
                updateAvailable && asset is not null,
                release.HtmlUrl,
                asset?.Name,
                asset is null ? null : new Uri(asset.BrowserDownloadUrl),
                asset?.Size ?? 0,
                message));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StoreResult(UpdateCheckResult.Error(CurrentVersion, $"Update check failed: {ex.Message}"));
        }
        finally
        {
            SetChecking(false);
        }
    }

    private UpdateCheckResult StoreResult(UpdateCheckResult result)
    {
        LastResult = result;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void SetChecking(bool value)
    {
        lock (_lock)
        {
            _isChecking = value;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetDownloading(bool value, double progress, string? downloadedFile)
    {
        lock (_lock)
        {
            _isDownloading = value;
            _downloadProgress = progress;
            _downloadedFile = downloadedFile;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetDownloadProgress(double progress)
    {
        lock (_lock)
        {
            if (Math.Abs(_downloadProgress - progress) < 1)
            {
                return;
            }

            _downloadProgress = progress;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static GitHubAsset? SelectAsset(IReadOnlyList<GitHubAsset>? assets)
    {
        if (assets is null || assets.Count == 0)
        {
            return null;
        }

        return assets.FirstOrDefault(asset => asset.Name.Equals("FastTaskMgr.exe", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && asset.Name.Contains("FastTaskMgr", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseVersion(string tag, out Version? version)
    {
        string text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        if (Version.TryParse(text, out Version? parsed))
        {
            version = parsed;
            return true;
        }

        version = null;
        return false;
    }

    private static Version Normalize(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static string CleanFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "update" : cleaned;
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version? LatestVersion,
    string LatestVersionText,
    bool IsUpdateAvailable,
    bool CanDownload,
    string? ReleaseUrl,
    string? AssetName,
    Uri? DownloadUrl,
    long AssetSizeBytes,
    string Message)
{
    public static UpdateCheckResult NotFound(Version currentVersion) => new(
        currentVersion,
        null,
        "No release",
        false,
        false,
        null,
        null,
        null,
        0,
        "No GitHub release was found.");

    public static UpdateCheckResult Error(Version currentVersion, string message) => new(
        currentVersion,
        null,
        "Unknown",
        false,
        false,
        null,
        null,
        null,
        0,
        message);
}

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

internal sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")] long Size);
