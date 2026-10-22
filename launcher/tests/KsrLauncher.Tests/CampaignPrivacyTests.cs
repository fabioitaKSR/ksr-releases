using System.Net;
using System.Text;
using System.Text.Json;
using KsrLauncher.Core;

internal static class CampaignPrivacyTests
{
    public static async Task LegacyDataListUsesServerAuthorization()
    {
        foreach (var allowed in new[] { true, false })
        {
            using var http = new HttpClient(new LegacyHandler(allowed));
            var campaigns = await new KsrPlatformClient(http).GetDataCampaignsAsync("https://example.test", "personal-session");
            if (campaigns.Any(c => c.CampaignCode == "test1") != allowed)
                throw new Exception("Legacy test1 visibility ignored server authorization.");
            if (campaigns.Any(c => c.DataChoiceSupported || c.EndDataChoice != null))
                throw new Exception("Old server was treated as confirming a saved choice.");
        }
    }

    private sealed class LegacyHandler(bool allowed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter != "personal-session") throw new Exception("Missing personal session.");
            var path=request.RequestUri!.AbsolutePath;
            var json=path switch
            {
                "/api/v1/me/data-campaigns" => "{}",
                "/api/v1/campaigns" => """{"campaigns":[{"campaignCode":"KSR-PERSONAL","name":"My race"}]}""",
                "/api/v1/launcher/betas" => allowed
                    ? """{"modules":[{"id":"ksr-full","label":"KSR Full Mode","description":"Full Mode","order":10,"version":"","workflow":"full-mode"}]}"""
                    : """{"modules":[]}""",
                _ => throw new Exception("Unexpected data endpoint.")
            };
            return Task.FromResult(new HttpResponseMessage(path == "/api/v1/me/data-campaigns" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                { Content=new StringContent(json,Encoding.UTF8,"application/json") });
        }
    }

    public static async Task TransportAndServerConfirmation()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            if (request.Headers.Authorization?.Parameter != "access-private") throw new Exception("Missing personal session.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (body.RootElement.GetProperty("privacyNoticeVersion").GetString() != KsrPlatformClient.CampaignPrivacyNoticeVersion)
                throw new Exception("Missing notice version.");
            var choice = body.RootElement.GetProperty("endDataChoice").GetString();
            if (request.RequestUri!.AbsolutePath != "/api/v1/campaigns/KSR-TEST/" + (request.Method == HttpMethod.Post ? "join" : "data-preference"))
                throw new Exception("Wrong membership endpoint.");
            return JsonSerializer.Serialize(new { ok = true, campaign = new { campaignCode = "KSR-TEST", name = "Race", status = "active", endDataChoice = choice } });
        }));
        var client = new KsrPlatformClient(http);
        if ((await client.JoinCampaignWithDataChoiceAsync("https://example.test", "access-private", "KSR-TEST", "delete")).EndDataChoice != "delete")
            throw new Exception("Join preference lost.");
        if ((await client.SetCampaignDataChoiceAsync("https://example.test", "access-private", "KSR-TEST", "retain")).EndDataChoice != "retain")
            throw new Exception("Changed preference lost.");
        try { await client.JoinCampaignAsync("https://example.test", "access-private", "KSR-TEST"); throw new Exception("Missing choice accepted."); }
        catch (ArgumentException) { }
        if (calls != 2) throw new Exception("Invalid choice reached server.");

        using var wrong = new HttpClient(new Handler(_ => Task.FromResult("""{"ok":true,"campaign":{"campaignCode":"KSR-OTHER","name":"Other","endDataChoice":"retain"}}""")));
        try { await new KsrPlatformClient(wrong).SetCampaignDataChoiceAsync("https://example.test", "access-private", "KSR-TEST", "retain"); throw new Exception("Wrong membership accepted."); }
        catch (InvalidDataException) { }
        if (KsrPlatformClient.BuildPrivacyNoticeUri("https://example.test").AbsoluteUri != "https://example.test/privacy")
            throw new Exception("Privacy link uses the wrong server.");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await response(request), Encoding.UTF8, "application/json") };
    }
}
