using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsrLauncher.Core;

public sealed record LauncherUpdateRelease(
    Version Version,
    string Tag,
    string AssetName,
    string AssetUrl,
    string Sha256);

public sealed class LauncherUpdateService
{
    private readonly HttpClient _httpClient;

    public LauncherUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KSR-Launcher", "0.1"));
        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<LauncherUpdateRelease?> CheckAsync(
        string repository,
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new ArgumentException("The GitHub repository must use owner/name format.");
        var uri = $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/releases?per_page=20";
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubLauncherRelease>>(
            stream, ManifestService.JsonOptions, cancellationToken) ?? [];

        foreach (var release in releases.Where(item => !item.Draft && !item.Prerelease)
                     .Select(item => (Release: item, Version: ParseVersion(item.TagName)))
                     .Where(item => item.Version is not null && item.Version > currentVersion)
                     .OrderByDescending(item => item.Version))
        {
            var versionText = $"{release.Version!.Major}.{release.Version.Minor}.{Math.Max(0, release.Version.Build)}";
            var expectedName = $"KSR-Launcher-v{versionText}-win-x64.exe";
            var executable = release.Release.Assets.FirstOrDefault(asset =>
                asset.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
            var checksums = release.Release.Assets.FirstOrDefault(asset =>
                asset.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
            if (executable is null || checksums is null) continue;

            var checksumText = await _httpClient.GetStringAsync(checksums.BrowserDownloadUrl, cancellationToken);
            var sha256 = ParseChecksum(checksumText, executable.Name);
            if (sha256 is null) continue;
            return new LauncherUpdateRelease(
                release.Version, release.Release.TagName, executable.Name, executable.BrowserDownloadUrl, sha256);
        }
        return null;
    }

    public async Task<string> DownloadAsync(
        LauncherUpdateRelease release,
        string destinationDirectory,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (Path.GetFileName(release.AssetName) != release.AssetName ||
            !release.AssetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The launcher update asset name is unsafe.");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, release.AssetName);
        var partial = destination + ".part";
        try
        {
            using var response = await _httpClient.GetAsync(
                release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                    progress?.Report(total);
                }
            }
            var actual = await PackageService.ComputeSha256Async(partial, cancellationToken);
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded launcher update failed SHA-256 verification.");
            File.Move(partial, destination, true);
            return destination;
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }

    private static Version? ParseVersion(string tag) =>
        Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var version) ? version : null;

    private static string? ParseChecksum(string content, string assetName)
    {
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[^1].TrimStart('*').Equals(assetName, StringComparison.OrdinalIgnoreCase)) continue;
            var hash = parts[0];
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit)) return hash.ToLowerInvariant();
        }
        return null;
    }

    private sealed class GitHubLauncherRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public List<GitHubLauncherAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubLauncherAsset
    {
        public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }
}
