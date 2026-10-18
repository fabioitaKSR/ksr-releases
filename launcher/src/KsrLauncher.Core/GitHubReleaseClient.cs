using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsrLauncher.Core;

public sealed record ResolvedRelease(ReleaseManifest Manifest, string Tag, string AssetsBaseUrl);

public sealed class GitHubReleaseClient
{
    private readonly HttpClient _httpClient;

    private readonly ReleaseSignatures _signatures;
    private readonly string? _internalSignedCatalog;
    public GitHubReleaseClient(HttpClient? httpClient = null, ReleaseSignatures? signatures = null, string? internalSignedCatalog = null)
    {
        _internalSignedCatalog = internalSignedCatalog;
        _signatures = signatures ?? new ReleaseSignatures();
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

        if (!string.IsNullOrWhiteSpace(_internalSignedCatalog))
            return await ResolveInternalCatalogAsync(_internalSignedCatalog, channel, cancellationToken);

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
        var signatureAsset = release.Assets.SingleOrDefault(item => item.Name == "ksr-release.json.sig")
            ?? throw new InvalidDataException("The release does not contain a signed component manifest.");
        var payload = await ReleaseSignatures.DownloadBoundedAsync(_httpClient, manifestAsset.BrowserDownloadUrl, 2 * 1024 * 1024, cancellationToken);
        var signature = await ReleaseSignatures.DownloadBoundedAsync(_httpClient, signatureAsset.BrowserDownloadUrl, 16 * 1024, cancellationToken);
        _signatures.Verify(payload, signature);
        await using var manifestStream = new MemoryStream(payload, false);
        var manifest = await ManifestService.LoadAsync(manifestStream, cancellationToken);
        if (!string.Equals(manifest.Channel, channel, StringComparison.OrdinalIgnoreCase) && channel == "stable")
            throw new InvalidDataException($"Il manifest della release {release.TagName} dichiara il canale '{manifest.Channel}', atteso stable.");

        var manifestUri = new Uri(manifestAsset.BrowserDownloadUrl, UriKind.Absolute);
        var assetsBase = new Uri(manifestUri, ".").AbsoluteUri.TrimEnd('/');
        return new ResolvedRelease(manifest, release.TagName, assetsBase);
    }

    private async Task<ResolvedRelease> ResolveInternalCatalogAsync(string path, string channel, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("The internal signed catalog must be a local absolute path.");
        var payload = await ReadLocalBoundedAsync(path, 2 * 1024 * 1024, cancellationToken);
        var signature = await ReadLocalBoundedAsync(path + ".sig", 16 * 1024, cancellationToken);
        _signatures.Verify(payload, signature);
        await using var stream = new MemoryStream(payload, false);
        var manifest = await ManifestService.LoadAsync(stream, cancellationToken);
        if (!string.Equals(manifest.Channel, channel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The internal signed catalog has a different release channel.");
        if (manifest.Components.Any(component => string.IsNullOrWhiteSpace(component.AssetUrl)))
            throw new InvalidDataException("Every internal catalog package must have a signed absolute HTTPS assetUrl.");
        // Asset URLs have already been validated by ManifestService and authenticated by the signature.
        return new ResolvedRelease(manifest, "internal-" + manifest.Version, "https://github.com/fabioitaKSR/ksr-releases/releases/download/");
    }

    private static async Task<byte[]> ReadLocalBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(path);
        if (source.Length > maximumBytes) throw new InvalidDataException("Internal release metadata is too large.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Internal release metadata is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
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
