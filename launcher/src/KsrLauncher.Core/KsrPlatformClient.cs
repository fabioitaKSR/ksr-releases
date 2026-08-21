using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;

namespace KsrLauncher.Core;

public sealed record KsrUser(long Id, string Username);

public sealed record KsrHealth(string Service, string? Version, bool Legacy);

public sealed record KsrLoginSession(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    KsrUser User);

public sealed record KsrCampaign(
    string CampaignCode,
    string Name,
    string Status,
    string Role,
    string? NationId,
    string? MasterSaveSha256,
    long? MasterSaveSize);

public sealed class KsrPlatformClient(HttpClient? httpClient = null)
{
    public const string ProductionServerUrl = "https://play.kerbalspacerace.net";

    private readonly HttpClient _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public async Task RegisterAsync(
        string serverUrl,
        string username,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length < 3)
            throw new ArgumentException("Username must contain at least 3 characters.");
        if (!MailAddress.TryCreate(email.Trim(), out _))
            throw new ArgumentException("Enter a valid email address.");
        if (password.Length < 8)
            throw new ArgumentException("Password must contain at least 8 characters.");

        await PostForSuccessAsync(
            new Uri(baseUri, "/api/v1/auth/register"),
            new { username = username.Trim(), email = email.Trim(), password },
            "KSR account creation failed",
            cancellationToken);
    }

    public async Task RequestPasswordResetAsync(
        string serverUrl,
        string email,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        if (!MailAddress.TryCreate(email.Trim(), out _)) throw new ArgumentException("Enter a valid email address.");
        await PostForSuccessAsync(
            new Uri(baseUri, "/api/v1/auth/forgot-password"),
            new { email = email.Trim() },
            "Password reset request failed",
            cancellationToken);
    }

    public async Task ResetPasswordAsync(
        string serverUrl,
        string resetToken,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        if (string.IsNullOrWhiteSpace(resetToken)) throw new ArgumentException("Enter the password reset token.");
        if (newPassword.Length < 8) throw new ArgumentException("Password must contain at least 8 characters.");
        await PostForSuccessAsync(
            new Uri(baseUri, "/api/v1/auth/reset-password"),
            new { resetToken = resetToken.Trim(), password = newPassword },
            "Password reset failed",
            cancellationToken);
    }

    public async Task<KsrHealth> GetHealthAsync(
        string serverUrl,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        using var response = await _httpClient.GetAsync(new Uri(baseUri, "/api/v1/health"), cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        var root = Unwrap(document.RootElement);
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The KSR server health check did not report a ready service.");
        return new KsrHealth(
            RequiredString(root, "service"),
            OptionalString(root, "version"),
            root.TryGetProperty("legacy", out var legacy) && legacy.ValueKind == JsonValueKind.True);
    }

    public async Task<KsrLoginSession> LoginAsync(
        string serverUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new ArgumentException("Username and password are required.");

        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(baseUri, "/api/v1/auth/login"),
            new { username = username.Trim(), password },
            ManifestService.JsonOptions,
            cancellationToken);
        return await ReadLoginSessionAsync(response, cancellationToken);
    }

    public async Task<KsrLoginSession> RefreshAsync(
        string serverUrl,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("A refresh token is required.");

        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(baseUri, "/api/v1/auth/refresh"),
            new { refreshToken },
            ManifestService.JsonOptions,
            cancellationToken);
        return await ReadLoginSessionAsync(response, cancellationToken);
    }

    private static async Task<KsrLoginSession> ReadLoginSessionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = await ReadResponseAsync(response, cancellationToken);
        var root = Unwrap(document.RootElement);
        var userElement = RequiredObject(root, "user");
        return new KsrLoginSession(
            RequiredString(root, "accessToken"),
            RequiredString(root, "refreshToken"),
            OptionalInt32(root, "expiresIn") ?? 1800,
            new KsrUser(RequiredInt64(userElement, "id"), RequiredString(userElement, "username")));
    }

    public async Task<IReadOnlyList<KsrCampaign>> GetCampaignsAsync(
        string serverUrl,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        using var request = AuthorizedRequest(HttpMethod.Get, new Uri(baseUri, "/api/v1/campaigns"), accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        var root = document.RootElement;
        var campaigns = root.ValueKind == JsonValueKind.Array
            ? root
            : RequiredArray(Unwrap(root), "campaigns");
        return campaigns.EnumerateArray().Select(ReadCampaign).ToList();
    }

    private static KsrCampaign ReadCampaign(JsonElement item) => new(
        RequiredString(item, "campaignCode"),
        RequiredString(item, "name"),
        OptionalString(item, "status") ?? "unknown",
        OptionalString(item, "role") ?? "player",
        OptionalString(item, "nationId"),
        OptionalString(item, "masterSaveSha256"),
        OptionalInt64(item, "masterSaveSize"));

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, Uri uri, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("Sign in before calling the KSR API.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task PostForSuccessAsync(
        Uri uri,
        object payload,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(uri, payload, ManifestService.JsonOptions, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var error = TryReadError(content);
        var message = error.Message ?? $"{failureMessage} ({(int)response.StatusCode}).";
        throw new KsrApiException((int)response.StatusCode, error.Code, message);
    }

    private static Uri ValidateServerUri(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException("Configure a valid KSR server URL.");
        var localHttp = uri.Scheme == Uri.UriSchemeHttp &&
            (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        if (uri.Scheme != Uri.UriSchemeHttps && !localHttp)
            throw new ArgumentException("The KSR server must use HTTPS. Plain HTTP is allowed only on localhost for development.");
        return uri;
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = TryReadError(content);
            var message = error.Message ?? $"KSR server request failed ({(int)response.StatusCode}).";
            throw new KsrApiException((int)response.StatusCode, error.Code, message);
        }
        try { return JsonDocument.Parse(content); }
        catch (JsonException exception) { throw new InvalidDataException("The KSR server returned invalid JSON.", exception); }
    }

    private static (string? Code, string? Message) TryReadError(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var error)) return (null, null);
            if (error.ValueKind == JsonValueKind.String) return (null, error.GetString());
            if (error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
                var message = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
                return (code, message);
            }
        }
        catch (JsonException) { }
        return (null, null);
    }

    private static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : root;

    private static JsonElement RequiredObject(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"KSR response is missing '{name}'.");

    private static JsonElement RequiredArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidDataException($"KSR response is missing '{name}'.");

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new InvalidDataException($"KSR response is missing '{name}'.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long RequiredInt64(JsonElement element, string name) =>
        OptionalInt64(element, name) ?? throw new InvalidDataException($"KSR response is missing '{name}'.");

    private static long? OptionalInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static int? OptionalInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}

public sealed class KsrApiException(int statusCode, string? code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}
