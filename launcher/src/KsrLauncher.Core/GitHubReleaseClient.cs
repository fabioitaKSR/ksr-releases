using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsrLauncher.Core;

public sealed record ResolvedRelease(ReleaseManifest Manifest, string Tag, string AssetsBaseUrl);

public sealed class GitHubReleaseClient
{
    private readonly HttpClient _httpClient;

    public GitHubReleaseClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KSR-Launcher", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<ResolvedRelease> ResolveAsync(string repository, string channel = "stable", CancellationToken cancellationToken = default)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new ArgumentException("Il repository deve avere formato proprietario/nome, per esempio fabioitaKSR/ksr-releases.");
        if (channel is not ("stable" or "beta")) throw new ArgumentException("Il canale deve essere stable o beta.");

        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/releases?per_page=30";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(responseStream, ManifestService.JsonOptions, cancellationToken) ?? [];
        static bool HasManifest(GitHubRelease item) =>
            item.Assets.Any(asset => string.Equals(asset.Name, "ksr-release.json", StringComparison.OrdinalIgnoreCase));
        var release = channel == "stable"
            ? releases.FirstOrDefault(item => !item.Draft && !item.Prerelease && HasManifest(item))
            : releases.FirstOrDefault(item => !item.Draft && item.Prerelease && HasManifest(item))
              ?? releases.FirstOrDefault(item => !item.Draft && HasManifest(item));
        if (release is null) throw new InvalidOperationException($"Nessuna release {channel} con ksr-release.json pubblicata in {repository}.");

        var manifestAsset = release.Assets.First(item => string.Equals(item.Name, "ksr-release.json", StringComparison.OrdinalIgnoreCase));
        using var manifestResponse = await _httpClient.GetAsync(manifestAsset.BrowserDownloadUrl, cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();
        await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await ManifestService.LoadAsync(manifestStream, cancellationToken);
        if (!string.Equals(manifest.Channel, channel, StringComparison.OrdinalIgnoreCase) && channel == "stable")
            throw new InvalidDataException($"Il manifest della release {release.TagName} dichiara il canale '{manifest.Channel}', atteso stable.");

        var manifestUri = new Uri(manifestAsset.BrowserDownloadUrl, UriKind.Absolute);
        var assetsBase = new Uri(manifestUri, ".").AbsoluteUri.TrimEnd('/');
        return new ResolvedRelease(manifest, release.TagName, assetsBase);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }
}
