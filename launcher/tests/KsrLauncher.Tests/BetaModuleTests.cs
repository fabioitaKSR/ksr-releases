using System.Net;
using System.Text;
using System.Text.Json;
using KsrLauncher.Core;

internal static class BetaModuleTests
{
    public static async Task CatalogAndAccountIsolation()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            var token = request.Headers.Authorization?.Parameter;
            var modules = token == "account-a" ? new[] { Module("ksr-full", "full-mode"), Module("future-beta") } : [];
            return JsonSerializer.SerializeToUtf8Bytes(new { modules }, ManifestService.JsonOptions);
        }));
        var client = new BetaModulesClient(http);
        if ((await client.GetAsync("https://ksr.test", "account-a")).Count != 2 ||
            (await client.GetAsync("https://ksr.test", "account-b")).Count != 0)
            throw new Exception("Account catalogs leaked.");
        await RejectAsync(() => client.GetAsync("http://ksr.test", "account-a"));
        foreach (var id in new[] { "../beta", "Beta", "beta\n", "beta/b", "", new string('a', 65) })
            Reject(() => BetaModulesClient.ValidateId(id));
        var six = Enumerable.Range(0, 6).Select(i => Module("module-" + i)).ToList();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { modules = six }, ManifestService.JsonOptions);
        if (BetaModulesClient.Parse(payload).Count != 6) throw new Exception("Six beta modules are not supported.");
        six.Add(Module("seventh"));
        Reject(() => BetaModulesClient.Parse(JsonSerializer.SerializeToUtf8Bytes(new { modules = six }, ManifestService.JsonOptions)));
    }

    public static Task SettingsPreserveLegacyAndSeparateModules()
    {
        var root = Path.Combine(Path.GetTempPath(), "ksr-beta-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, "{\"TestKspRoot\":\"F:\\\\existing-full\",\"CustomCampaignField\":42}");
        var existing = LauncherSettingsStore.LoadTestKspRoot(settings);
        LauncherSettingsStore.SaveBetaKspRoot("future-beta", Path.Combine(root, "beta"), settings);
        if (LauncherSettingsStore.LoadBetaKspRoot("ksr-full", settings) != existing ||
            LauncherSettingsStore.LoadBetaKspRoot("future-beta", settings) != Path.Combine(root, "beta"))
            throw new Exception("Beta paths collided with Full Mode.");
        LauncherSettingsStore.SaveBetaKspRoot("future-beta", null, settings);
        if (LauncherSettingsStore.LoadTestKspRoot(settings) != existing || !File.ReadAllText(settings).Contains("CustomCampaignField"))
            throw new Exception("Beta settings removed existing configuration.");
        return Task.CompletedTask;
    }

    public static async Task PrivateTransportVerifiesAndDoesNotFollowPublicAssets()
    {
        using var key = new TestSignatures();
        var manifest = new ReleaseManifest { SchemaVersion = 1, Product = "KerbalSpaceRace", Version = "1.0.0", MinimumLauncherVersion = "0.1.0",
            Components = [new ComponentManifest { Id = "private-mod", Asset = "private.zip", AssetUrl = "https://public.test/private.zip",
                Sha256 = new string('a', 64), Size = 123, Source = "GameData/Beta", Target = "GameData/Beta" }] };
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestService.JsonOptions);
        var requests = 0;
        using var client = new PrivateBetaReleaseClient("https://ksr.test", "future-beta", "personal-access", key.Verifier,
            new Handler(request =>
            {
                requests++;
                if (request.RequestUri!.Host != "ksr.test" || request.Headers.Authorization?.Parameter != "personal-access")
                    throw new Exception("Private credentials or requests escaped the KSR server.");
                return request.RequestUri.AbsolutePath.EndsWith(".sig") ? key.Sign(payload) : payload;
            }));
        var release = await client.ResolveAsync();
        if (!release.Manifest.Components[0].AssetUrl.StartsWith("https://ksr.test/api/v1/launcher/betas/future-beta/files/", StringComparison.Ordinal))
            throw new Exception("Signed public URL was used instead of the private route.");
        await RejectAsync(() => client.Http.GetAsync("https://public.test/private.zip"));
        if (requests != 2) throw new Exception("Forbidden transport reached the network.");
        using var tampered = new PrivateBetaReleaseClient("https://ksr.test", "future-beta", "personal-access", key.Verifier,
            new Handler(request => request.RequestUri!.AbsolutePath.EndsWith(".sig") ? key.Sign(Encoding.UTF8.GetBytes("different")) : payload));
        await RejectAsync(() => tampered.ResolveAsync());
    }

    public static async Task CancellationStillRollsBackInstalledComponents()
    {
        var root = Path.Combine(Path.GetTempPath(), "ksr-beta-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "GameData"));
        File.WriteAllText(Path.Combine(root, "KSP_x64.exe"), "fixture");
        var manifest = new ReleaseManifest { SchemaVersion = 1, Product = "KerbalSpaceRace", Version = "1.0.0", MinimumLauncherVersion = "0.1.0" };
        foreach (var id in new[] { "first", "second" })
        {
            Directory.CreateDirectory(Path.Combine(root, "GameData", id));
            File.WriteAllText(Path.Combine(root, "GameData", id, "old.txt"), "preserve-me");
            var zip = Path.Combine(root, id + ".zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("payload/new.txt").Open())) writer.Write("new");
            manifest.Components.Add(new ComponentManifest { Id = id, Asset = id + ".zip", Source = "payload", Target = "GameData/" + id,
                Size = new FileInfo(zip).Length, Sha256 = await PackageService.ComputeSha256Async(zip) });
        }
        using var cancellation = new CancellationTokenSource();
        try
        {
            await new UpdateEngine().RunAsync(manifest, new LauncherLocations(root, Path.Combine(root, "state")), root,
                true, UpdatePolicy.InstallOrRepair, new ImmediateProgress(p => { if (p.Phase == "INSTALLING") cancellation.Cancel(); }), cancellation.Token);
            throw new Exception("Cancellation was ignored.");
        }
        catch (OperationCanceledException) { }
        foreach (var id in new[] { "first", "second" })
            if (File.ReadAllText(Path.Combine(root, "GameData", id, "old.txt")) != "preserve-me" ||
                File.Exists(Path.Combine(root, "GameData", id, "new.txt"))) throw new Exception("Cancelled beta update was not rolled back.");
    }

    private sealed class ImmediateProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }

    private static KsrBetaModule Module(string id, string workflow = "installation") => new(id, id, "Beta", 10, "", workflow);
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException) { return; }
        throw new Exception("Unsafe beta request was accepted.");
    }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException) { return; }
        throw new Exception("Unsafe beta request was accepted.");
    }
    private sealed class Handler(Func<HttpRequestMessage, byte[]> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response(request)), RequestMessage = request });
    }
}
