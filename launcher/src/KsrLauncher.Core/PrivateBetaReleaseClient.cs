using System.Net.Http.Headers;

namespace KsrLauncher.Core;

public sealed class PrivateBetaReleaseClient : IDisposable
{
    public HttpClient Http { get; }
    private readonly string _baseUrl;
    private readonly ReleaseSignatures _signatures;

    public PrivateBetaReleaseClient(string server, string moduleId, string accessToken, ReleaseSignatures? signatures = null,
        HttpMessageHandler? handler = null)
    {
        BetaModulesClient.ValidateId(moduleId);
        var origin = new Uri(server.TrimEnd('/') + "/");
        if (origin.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(origin.UserInfo))
            throw new InvalidDataException("Private beta downloads require HTTPS.");
        _baseUrl = new Uri(origin, $"/api/v1/launcher/betas/{moduleId}/files/").AbsoluteUri;
        _signatures = signatures ?? new ReleaseSignatures();
        Http = new HttpClient(new SameModuleHandler(_baseUrl, accessToken,
            handler ?? new HttpClientHandler { AllowAutoRedirect = false })) { Timeout = TimeSpan.FromMinutes(15) };
    }

    public async Task<ResolvedRelease> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await ReleaseSignatures.DownloadBoundedAsync(Http, _baseUrl + "ksr-release.json", 2 * 1024 * 1024, cancellationToken);
        var signature = await ReleaseSignatures.DownloadBoundedAsync(Http, _baseUrl + "ksr-release.json.sig", 16384, cancellationToken);
        _signatures.Verify(payload, signature);
        await using var stream = new MemoryStream(payload, false);
        var manifest = await ManifestService.LoadAsync(stream, cancellationToken);
        if (manifest.Components.Any(c => c.TargetKind != "ksp"))
            throw new InvalidDataException("A beta package must target a KSP installation.");
        // Preserve all signed hashes, sizes and destinations. Transport is mapped
        // to the same authenticated module; signed upstream URLs are never used.
        foreach (var component in manifest.Components)
            component.AssetUrl = _baseUrl + Uri.EscapeDataString(component.Asset);
        return new ResolvedRelease(manifest, "private-" + manifest.Version, _baseUrl.TrimEnd('/'));
    }

    public void Dispose() => Http.Dispose();

    private sealed class SameModuleHandler(string baseUrl, string token, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token) || request.RequestUri is not Uri uri ||
                !uri.AbsoluteUri.StartsWith(baseUrl, StringComparison.Ordinal) ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                Uri.UnescapeDataString(uri.AbsolutePath).Contains("..", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Private beta transport refused the request.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
