using System.Net;
using System.Text;
using System.Text.Json;
using KsrLauncher.Core;

internal static class AccountDeletionTests
{
    public static async Task PreviewAndExplicitConfirmation()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            if (request.Method != HttpMethod.Post || request.Headers.Authorization?.Parameter != "synthetic-session") throw new Exception("Missing authenticated POST.");
            if (request.RequestUri!.Query.Contains("password")) throw new Exception("Password in URL.");
            if (request.RequestUri.AbsolutePath.EndsWith("/preview"))
                return """{"userId":1,"username":"alice","campaignsToClose":["RACE-A"],"participationCount":2,"supportReportCount":1,"linkedRecordCount":3,"blockers":[],"canDelete":true,"previewToken":"synthetic-preview","expiresIn":600}""";
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (!body.RootElement.GetProperty("confirmed").GetBoolean() || body.RootElement.GetProperty("previewToken").GetString() != "synthetic-preview") throw new Exception("Missing confirmation binding.");
            if (body.RootElement.GetProperty("password").GetString() != "synthetic-password") throw new Exception("Missing reauthentication.");
            return """{"accountDeleted":true,"processingComplete":true,"confirmationEmailQueued":true}""";
        }));
        var client = new KsrPlatformClient(http);
        var preview = await client.PreviewAccountDeletionAsync("https://example.test", "synthetic-session");
        if (calls != 1 || preview.UserId != 1) throw new Exception("Preview made a deletion request.");
        try { await client.DeleteAccountAsync("https://example.test", "synthetic-session", preview, "bob", "synthetic-password"); throw new Exception("Wrong username accepted."); }
        catch (ArgumentException) { }
        if (calls != 1) throw new Exception("Invalid confirmation reached server.");
        var result = await client.DeleteAccountAsync("https://example.test", "synthetic-session", preview, "alice", "synthetic-password");
        if (!result.AccountDeleted || calls != 2) throw new Exception("Deletion was not confirmed.");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> handle) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await handle(request), Encoding.UTF8, "application/json") };
    }
}
