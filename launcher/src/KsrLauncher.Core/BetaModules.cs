using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KsrLauncher.Core;

public sealed record KsrBetaModule(string Id, string Label, string Description, int Order, string Version, string Workflow);

public sealed partial class BetaModulesClient(HttpClient? httpClient = null)
{
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _http = httpClient ?? SharedHttp;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static void ValidateId(string id)
    {
        if (id is null || !IdPattern().IsMatch(id)) throw new InvalidDataException("Invalid beta module ID.");
    }

    public async Task<IReadOnlyList<KsrBetaModule>> GetAsync(string server, string accessToken, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(server.TrimEnd('/') + "/api/v1/launcher/betas");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidDataException("Beta access requires HTTPS.");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new UnauthorizedAccessException("Sign in before opening beta modules.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Beta access could not be verified.", null, response.StatusCode);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > 32 * 1024) throw new InvalidDataException("The beta catalog is too large.");
            output.Write(buffer, 0, count);
        }
        return Parse(output.ToArray());
    }

    public static IReadOnlyList<KsrBetaModule> Parse(byte[] payload)
    {
        if (payload.Length > 32 * 1024) throw new InvalidDataException("The beta catalog is too large.");
        using var doc = JsonDocument.Parse(payload);
        var modules = doc.RootElement.GetProperty("modules").Deserialize<List<KsrBetaModule>>(ManifestService.JsonOptions)
            ?? throw new InvalidDataException("Invalid beta catalog.");
        if (modules.Any(m => m is null)) throw new InvalidDataException("Invalid beta module.");
        if (modules.Count > 6 || modules.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != modules.Count)
            throw new InvalidDataException("Invalid beta catalog capacity or IDs.");
        foreach (var m in modules)
        {
            ValidateId(m.Id);
            if (string.IsNullOrWhiteSpace(m.Label) || m.Label.Length > 80 || m.Description is null || m.Description.Length > 500 ||
                m.Version is null || m.Version.Length > 40 || m.Workflow is not ("full-mode" or "installation") ||
                (m.Workflow == "full-mode") != (m.Id == "ksr-full"))
                throw new InvalidDataException("Invalid beta module.");
        }
        return modules.OrderBy(m => m.Order).ThenBy(m => m.Id, StringComparer.Ordinal).ToArray();
    }
}
