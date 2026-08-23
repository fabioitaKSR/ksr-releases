using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
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
    long? MasterSaveSize,
    int? BaselineSchemaVersion = null,
    string? BaselineSha256 = null);

public sealed record KsrGameTicket(string Token, string CampaignCode, int ExpiresIn, double? ExpiresAt);

public sealed class KsrPlatformClient(HttpClient? httpClient = null)
{
    public const string ProductionServerUrl = "https://play.kerbalspacerace.net";

    private readonly HttpClient _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    public static Uri BuildLeaderboardUri(string serverUrl, string campaignCode)
    {
        var baseUri = ValidateServerUri(serverUrl);
        if (string.IsNullOrWhiteSpace(campaignCode) ||
            !campaignCode.Trim().StartsWith("KSR-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a valid KSR campaign before opening its leaderboard.");
        return new Uri(baseUri,
            $"/sheet-preview?campaign_id={Uri.EscapeDataString(campaignCode.Trim())}");
    }

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

    public async Task<KsrGameTicket> GetGameTicketAsync(
        string serverUrl,
        string accessToken,
        string campaignCode,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        if (string.IsNullOrWhiteSpace(campaignCode)) throw new ArgumentException("A campaign code is required.");
        using var request = AuthorizedRequest(HttpMethod.Post, new Uri(baseUri, "/api/v1/auth/game-ticket"), accessToken);
        request.Content = JsonContent.Create(new { campaignCode = campaignCode.Trim() }, options: ManifestService.JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        var root = Unwrap(document.RootElement);
        return new KsrGameTicket(
            RequiredString(root, "gameTicket"),
            OptionalString(root, "campaignCode") ?? campaignCode.Trim(),
            OptionalInt32(root, "expiresIn") ?? 43200,
            root.TryGetProperty("expiresAt", out var expiresAt) && expiresAt.TryGetDouble(out var value) ? value : null);
    }

    public async Task<KsrCampaign> CreateCampaignAsync(
        string serverUrl,
        string accessToken,
        CampaignBaselinePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var baseUri = ValidateServerUri(serverUrl);
        if (!File.Exists(package.MasterSavePath))
            throw new FileNotFoundException("The campaign Master Save package was not found.", package.MasterSavePath);
        if (!File.Exists(package.ManifestPath))
            throw new FileNotFoundException("The campaign baseline was not found.", package.ManifestPath);
        if (string.IsNullOrWhiteSpace(package.Manifest.CampaignName))
            throw new InvalidDataException("The campaign baseline does not contain a campaign name.");

        var masterSaveSha256 = await PackageService.ComputeSha256Async(package.MasterSavePath, cancellationToken);
        if (!string.Equals(masterSaveSha256, package.Manifest.MasterSaveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Master Save changed after the campaign baseline was created. Build the baseline again.");
        var baselineSha256 = await PackageService.ComputeSha256Async(package.ManifestPath, cancellationToken);
        var idempotencyKey = BuildCampaignIdempotencyKey(package.Manifest.CampaignName, masterSaveSha256, baselineSha256);

        await using var masterSaveStream = new FileStream(
            package.MasterSavePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var baselineStream = new FileStream(
            package.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(package.Manifest.CampaignName, Encoding.UTF8), "name");
        var masterSaveContent = new StreamContent(masterSaveStream);
        masterSaveContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(masterSaveContent, "masterSave", "master-save.zip");
        var baselineContent = new StreamContent(baselineStream);
        baselineContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(baselineContent, "baseline", "baseline.json");

        using var request = AuthorizedRequest(HttpMethod.Post, new Uri(baseUri, "/api/v1/campaigns"), accessToken);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Content = form;
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        var root = Unwrap(document.RootElement);
        var campaignElementToRead = root.TryGetProperty("campaign", out var campaignElement) && campaignElement.ValueKind == JsonValueKind.Object
            ? campaignElement
            : root;
        var campaign = ReadCampaign(campaignElementToRead);
        if (!string.Equals(campaign.MasterSaveSha256, masterSaveSha256, StringComparison.OrdinalIgnoreCase) ||
            campaign.MasterSaveSize != package.Manifest.MasterSaveSize)
            throw new InvalidDataException("The KSR server returned Master Save metadata that does not match the uploaded package.");
        if (campaign.BaselineSchemaVersion != package.Manifest.SchemaVersion ||
            !string.Equals(campaign.BaselineSha256, baselineSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The KSR server returned baseline metadata that does not match the uploaded package.");
        return campaign;
    }

    public async Task<KsrCampaign> JoinCampaignAsync(
        string serverUrl,
        string accessToken,
        string campaignCode,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        var normalizedCode = campaignCode.Trim();
        if (normalizedCode.Length < 5 || normalizedCode.Length > 100 ||
            !normalizedCode.StartsWith("KSR-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a valid KSR Campaign ID.");
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            new Uri(baseUri, $"/api/v1/campaigns/{Uri.EscapeDataString(normalizedCode)}/join"),
            accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        var root = Unwrap(document.RootElement);
        var campaign = root.TryGetProperty("campaign", out var campaignElement) && campaignElement.ValueKind == JsonValueKind.Object
            ? campaignElement
            : root;
        return ReadCampaign(campaign);
    }

    public async Task<CampaignBaselinePackage> DownloadCampaignArtifactsAsync(
        string serverUrl,
        string accessToken,
        KsrCampaign campaign,
        string destinationDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var baseUri = ValidateServerUri(serverUrl);
        if (string.IsNullOrWhiteSpace(campaign.CampaignCode) ||
            !campaign.CampaignCode.StartsWith("KSR-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A valid KSR Campaign ID is required.");

        Directory.CreateDirectory(destinationDirectory);
        var masterSavePath = Path.Combine(destinationDirectory, "master-save.zip");
        var manifestPath = Path.Combine(destinationDirectory, "baseline.json");
        var masterPart = masterSavePath + ".part";
        var manifestPart = manifestPath + ".part";
        try
        {
            progress?.Report("Downloading the verified Master Save…");
            await DownloadVerifiedArtifactAsync(
                new Uri(baseUri, $"/api/v1/campaigns/{Uri.EscapeDataString(campaign.CampaignCode)}/master-save"),
                accessToken, masterPart, "X-KSR-Master-Save-SHA256", campaign.MasterSaveSha256,
                campaign.MasterSaveSize, cancellationToken);

            progress?.Report("Downloading the GameData baseline…");
            await DownloadVerifiedArtifactAsync(
                new Uri(baseUri, $"/api/v1/campaigns/{Uri.EscapeDataString(campaign.CampaignCode)}/baseline"),
                accessToken, manifestPart, "X-KSR-Baseline-SHA256", campaign.BaselineSha256,
                null, cancellationToken);

            var manifest = await CampaignBaselineBuilder.LoadAsync(manifestPart, cancellationToken);
            if (!string.Equals(manifest.MasterSaveSha256, campaign.MasterSaveSha256, StringComparison.OrdinalIgnoreCase) ||
                manifest.MasterSaveSize != campaign.MasterSaveSize)
                throw new InvalidDataException("The downloaded baseline does not match the campaign Master Save metadata.");
            if (campaign.BaselineSchemaVersion is not null && manifest.SchemaVersion != campaign.BaselineSchemaVersion)
                throw new InvalidDataException("The downloaded baseline schema does not match the campaign metadata.");
            File.Move(masterPart, masterSavePath, true);
            File.Move(manifestPart, manifestPath, true);
            progress?.Report("Master Save and GameData baseline verified.");
            return new CampaignBaselinePackage(destinationDirectory, manifestPath, masterSavePath, manifest);
        }
        catch
        {
            if (File.Exists(masterPart)) File.Delete(masterPart);
            if (File.Exists(manifestPart)) File.Delete(manifestPart);
            throw;
        }
    }

    private async Task DownloadVerifiedArtifactAsync(
        Uri uri,
        string accessToken,
        string destination,
        string hashHeader,
        string? expectedSha256,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Get, uri, accessToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using var error = await ReadResponseAsync(response, cancellationToken);
            throw new InvalidDataException("The KSR server rejected the campaign artifact download.");
        }
        var responseSha256 = response.Headers.TryGetValues(hashHeader, out var values)
            ? values.FirstOrDefault()?.Trim('"')
            : null;
        if (string.IsNullOrWhiteSpace(responseSha256))
            throw new InvalidDataException($"The server response is missing {hashHeader}.");
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(responseSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The server artifact hash does not match the campaign metadata.");
        if (expectedSize is not null && response.Content.Headers.ContentLength is long contentLength &&
            contentLength != expectedSize)
            throw new InvalidDataException("The server artifact size does not match the campaign metadata.");

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
            await source.CopyToAsync(output, cancellationToken);
        if (expectedSize is not null && new FileInfo(destination).Length != expectedSize)
            throw new InvalidDataException("The downloaded artifact is incomplete.");
        var actualSha256 = await PackageService.ComputeSha256Async(destination, cancellationToken);
        if (!string.Equals(actualSha256, responseSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded artifact failed SHA-256 verification.");
    }

    public static string BuildCampaignIdempotencyKey(string campaignName, string masterSaveSha256, string baselineSha256)
    {
        var material = $"ksr-campaign-v1\n{campaignName.Trim()}\n{masterSaveSha256.ToLowerInvariant()}\n{baselineSha256.ToLowerInvariant()}";
        return "ksr-v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public async Task CloseCampaignAsync(
        string serverUrl,
        string accessToken,
        string campaignCode,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        if (string.IsNullOrWhiteSpace(campaignCode)) throw new ArgumentException("A campaign code is required.");
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            new Uri(baseUri, $"/api/v1/campaigns/{Uri.EscapeDataString(campaignCode.Trim())}/close"),
            accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        var root = Unwrap(document.RootElement);
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new InvalidDataException("The KSR server did not close the campaign.");
    }

    public async Task DismissClosedCampaignAsync(
        string serverUrl,
        string accessToken,
        string campaignCode,
        CancellationToken cancellationToken = default)
    {
        var baseUri = ValidateServerUri(serverUrl);
        if (string.IsNullOrWhiteSpace(campaignCode)) throw new ArgumentException("A campaign code is required.");
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            new Uri(baseUri, $"/api/v1/campaigns/{Uri.EscapeDataString(campaignCode.Trim())}/dismiss"),
            accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        var root = Unwrap(document.RootElement);
        if (root.TryGetProperty("dismissed", out var dismissed) && dismissed.ValueKind == JsonValueKind.False)
            throw new InvalidDataException("The KSR server did not remove the closed campaign from your list.");
    }

    private static KsrCampaign ReadCampaign(JsonElement item) => new(
        RequiredString(item, "campaignCode"),
        RequiredString(item, "name"),
        OptionalString(item, "status") ?? "unknown",
        OptionalString(item, "role") ?? "player",
        OptionalString(item, "nationId"),
        OptionalString(item, "masterSaveSha256"),
        OptionalInt64(item, "masterSaveSize"),
        OptionalInt32(item, "baselineSchemaVersion"),
        OptionalString(item, "baselineSha256"));

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
