using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsrLauncher.Core;

internal static class GitHubAssetDownloads
{
    public static async Task<HttpResponseMessage> OpenAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        var route = PublicRoute(url);
        // Private beta transports and authenticated clients retain their existing scope.
        if (route is null || http.DefaultRequestHeaders.Authorization is not null)
            return await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        HttpResponseMessage? direct = null;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try { direct = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token); }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested) { }
        }
        if (direct is not null)
        {
            if (direct.StatusCode is not (HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
                return direct;
            direct.Dispose();
        }
        cancellationToken.ThrowIfCancellationRequested();

        // One alternative attempt only, using the official public API. The selected
        // asset must still belong to the exact original URL; callers retain all
        // signature, signed-URL, size, SHA and archive-budget validation.
        using var lookup = Request(route.LookupUrl, "application/vnd.github+json");
        using var metadata = await SendApiAsync(http, lookup, cancellationToken);
        metadata.EnsureSuccessStatusCode();
        var bytes = await ReadMetadataAsync(metadata, cancellationToken);
        Release? release;
        try { release = JsonSerializer.Deserialize<Release>(bytes); }
        catch (JsonException e) { throw new InvalidDataException("Invalid GitHub release response.", e); }
        if (release is null || release.Assets is null || release.Draft || release.Tag != route.Tag)
            throw new InvalidDataException("GitHub returned a different release.");
        var matches = release.Assets.Where(asset => asset is not null && asset.Name == route.Name && asset.BrowserUrl == url && asset.Id > 0).Take(2).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The GitHub API asset does not match the authorized download URL.");
        using var download = Request(route.AssetUrl(matches[0].Id), "application/octet-stream");
        return await SendApiAsync(http, download, cancellationToken);
    }

    private static HttpRequestMessage Request(string url, string mediaType)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KSR-Launcher", "0.7.33"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static async Task<HttpResponseMessage> SendApiAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.RequestMessage?.RequestUri is Uri final && final.Scheme != Uri.UriSchemeHttps)
        { response.Dispose(); throw new InvalidDataException("GitHub downloads require HTTPS."); }
        return response;
    }

    private static async Task<byte[]> ReadMetadataAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int maximum = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("GitHub release metadata is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("GitHub release metadata is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static Route? PublicRoute(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length != 6 || parts[2] != "releases" || parts[3] != "download" ||
            parts.Any(part => part is "." or ".." || part.Any(c => c is '/' or '\\' || char.IsControl(c)))) return null;
        return new Route(parts[0], parts[1], parts[4], parts[5]);
    }

    private sealed record Route(string Owner, string Repository, string Tag, string Name)
    {
        private string Prefix => $"https://api.github.com/repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repository)}/releases/";
        public string LookupUrl => Prefix + "tags/" + Uri.EscapeDataString(Tag);
        public string AssetUrl(long id) => Prefix + "assets/" + id;
    }
    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("assets")] public Asset[] Assets { get; set; } = [];
    }
    private sealed class Asset
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserUrl { get; set; } = "";
    }
}
