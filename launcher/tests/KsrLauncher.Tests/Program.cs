using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using KsrLauncher.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Manifest valido", ManifestValid),
    ("Manifest blocca path traversal", ManifestRejectsTraversal),
    ("ZIP blocca path traversal", ZipRejectsTraversal),
    ("Aggiornamento, preserve e rollback", UpdatePreserveRollback),
    ("SHA errato non modifica installazione", WrongHashDoesNotModify),
    ("Errore nel gruppo ripristina componenti precedenti", GroupFailureRollsBack),
    ("GitHub seleziona release stabile e manifest", GitHubSelectsStableRelease),
    ("Update ordinario ignora componente assente", ExistingOnlySkipsMissing),
    ("Installazione mancante richiede consenso esplicito", ExplicitInstallAddsMissing),
    ("Mod di terzi non viene toccata", ThirdPartyModIsUntouched),
    ("Support LOG package is safe and complete", SupportLogPackageIsSafe),
    ("Support SAVE package is safe and complete", SupportSavePackageIsSafe),
    ("Support report requires a useful description", SupportDescriptionIsRequired),
    ("Support upload uses authenticated HTTPS endpoint", SupportUploadIsAuthenticated),
    ("Platform login parses V1 session", PlatformLoginParsesSession),
    ("Platform campaigns use bearer session data", PlatformCampaignsUseBearerSession),
    ("Platform registration sends private email payload", PlatformRegistrationSendsEmail),
    ("Platform password recovery uses V1 endpoints", PlatformPasswordRecoveryUsesV1Endpoints),
    ("Platform health uses production V1 contract", PlatformHealthUsesV1Contract),
    ("Platform authentication preserves server error code", PlatformAuthenticationPreservesErrorCode)
};
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} test superati");
return failures == 0 ? 0 : 1;

static Task ManifestValid() { ManifestService.Validate(CreateManifest(new string('a', 64))); return Task.CompletedTask; }

static Task ManifestRejectsTraversal()
{
    var manifest = CreateManifest(new string('a', 64));
    manifest.Components[0].Target = "../fuori";
    Throws<InvalidDataException>(() => ManifestService.Validate(manifest));
    return Task.CompletedTask;
}

static Task ZipRejectsTraversal()
{
    using var scope = new TempScope();
    var zip = Path.Combine(scope.Root, "bad.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("../escape.txt");
    Throws<InvalidDataException>(() => PackageService.ExtractSafely(zip, Path.Combine(scope.Root, "out")));
    return Task.CompletedTask;
}

static async Task UpdatePreserveRollback()
{
    using var scope = new TempScope();
    var ksp = CreateKsp(scope.Root);
    var launcherData = Path.Combine(scope.Root, "LauncherData");
    var assets = Path.Combine(scope.Root, "assets");
    Directory.CreateDirectory(assets);
    var target = Path.Combine(ksp, "GameData", "TestMod");
    Directory.CreateDirectory(Path.Combine(target, "PluginData"));
    await File.WriteAllTextAsync(Path.Combine(target, "old.txt"), "old");
    await File.WriteAllTextAsync(Path.Combine(target, "PluginData", "local.cfg"), "local-value");
    var zip = Path.Combine(assets, "component.zip");
    CreateZip(zip, new Dictionary<string, string>
    {
        ["GameData/TestMod/new.txt"] = "new",
        ["GameData/TestMod/PluginData/local.cfg"] = "release-default"
    });
    var manifest = CreateManifest(await PackageService.ComputeSha256Async(zip));
    manifest.Preserve = ["GameData/TestMod/PluginData/**"];

    var result = await new UpdateEngine().RunAsync(manifest, new LauncherLocations(ksp, launcherData), assets, true);
    Equal("new", await File.ReadAllTextAsync(Path.Combine(target, "new.txt")));
    Equal("local-value", await File.ReadAllTextAsync(Path.Combine(target, "PluginData", "local.cfg")));
    False(File.Exists(Path.Combine(target, "old.txt")), "Il file vecchio non gestito doveva sparire.");
    True(result.BackupDirectory is not null, "Backup non creato.");
    await UpdateEngine.RollbackAsync(result.BackupDirectory!, new LauncherLocations(ksp, launcherData), true);
    Equal("old", await File.ReadAllTextAsync(Path.Combine(target, "old.txt")));
    False(File.Exists(Path.Combine(target, "new.txt")), "Il rollback non ha rimosso il file nuovo.");
}

static async Task WrongHashDoesNotModify()
{
    using var scope = new TempScope();
    var ksp = CreateKsp(scope.Root);
    var launcherData = Path.Combine(scope.Root, "LauncherData");
    var assets = Path.Combine(scope.Root, "assets");
    Directory.CreateDirectory(assets);
    var target = Path.Combine(ksp, "GameData", "TestMod");
    Directory.CreateDirectory(target);
    await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "untouched");
    CreateZip(Path.Combine(assets, "component.zip"), new Dictionary<string, string> { ["GameData/TestMod/new.txt"] = "new" });
    await ThrowsAsync<InvalidDataException>(() => new UpdateEngine().RunAsync(CreateManifest(new string('0', 64)), new LauncherLocations(ksp, launcherData), assets, true));
    Equal("untouched", await File.ReadAllTextAsync(Path.Combine(target, "keep.txt")));
}

static async Task GroupFailureRollsBack()
{
    using var scope = new TempScope();
    var ksp = CreateKsp(scope.Root);
    var launcherData = Path.Combine(scope.Root, "LauncherData");
    var assets = Path.Combine(scope.Root, "assets");
    Directory.CreateDirectory(assets);
    var firstTarget = Path.Combine(ksp, "GameData", "TestMod");
    Directory.CreateDirectory(firstTarget);
    await File.WriteAllTextAsync(Path.Combine(firstTarget, "old.txt"), "old");

    var firstZip = Path.Combine(assets, "component.zip");
    var secondZip = Path.Combine(assets, "blocked.zip");
    CreateZip(firstZip, new Dictionary<string, string> { ["GameData/TestMod/new.txt"] = "new" });
    CreateZip(secondZip, new Dictionary<string, string> { ["GameData/Blocked/Child/value.txt"] = "value" });
    await File.WriteAllTextAsync(Path.Combine(ksp, "GameData", "Blocked"), "impedisce la cartella");

    var manifest = CreateManifest(await PackageService.ComputeSha256Async(firstZip));
    manifest.Components.Add(new ComponentManifest
    {
        Id = "blocked-component",
        TransactionGroup = "ksp-client",
        Asset = "blocked.zip",
        Sha256 = await PackageService.ComputeSha256Async(secondZip),
        Source = "GameData/Blocked/Child",
        Target = "GameData/Blocked/Child",
        TargetKind = "ksp",
        Required = true,
        RequiredFiles = ["value.txt"]
    });

    await ThrowsAsync<IOException>(() => new UpdateEngine().RunAsync(
        manifest,
        new LauncherLocations(ksp, launcherData),
        assets,
        true,
        UpdatePolicy.InstallOrRepair));
    Equal("old", await File.ReadAllTextAsync(Path.Combine(firstTarget, "old.txt")));
    False(File.Exists(Path.Combine(firstTarget, "new.txt")), "Il primo componente non e stato ripristinato.");
}

static async Task GitHubSelectsStableRelease()
{
    var manifest = CreateManifest(new string('a', 64));
    var manifestJson = JsonSerializer.Serialize(manifest, ManifestService.JsonOptions);
    var releasesJson = """
        [
          {"tag_name":"v1.1.0-beta","draft":false,"prerelease":true,"assets":[]},
          {"tag_name":"v1.0.0","draft":false,"prerelease":false,"assets":[
            {"name":"ksr-release.json","browser_download_url":"https://github.com/fabioitaKSR/ksr-releases/releases/download/v1.0.0/ksr-release.json"}
          ]}
        ]
        """;
    using var http = new HttpClient(new FakeHttpHandler(request =>
        request.RequestUri!.Host == "api.github.com" ? releasesJson : manifestJson));
    var release = await new GitHubReleaseClient(http).ResolveAsync("fabioitaKSR/ksr-releases", "stable");
    Equal("v1.0.0", release.Tag);
    Equal("1.0.0", release.Manifest.Version);
    Equal("https://github.com/fabioitaKSR/ksr-releases/releases/download/v1.0.0", release.AssetsBaseUrl);
}

static async Task ExistingOnlySkipsMissing()
{
    using var scope = new TempScope();
    var ksp = CreateKsp(scope.Root);
    var launcherData = Path.Combine(scope.Root, "LauncherData");
    var manifest = CreateManifest(new string('a', 64));

    var result = await new UpdateEngine().RunAsync(
        manifest,
        new LauncherLocations(ksp, launcherData),
        Path.Combine(scope.Root, "assets-that-do-not-exist"),
        true);

    False(result.Applied, "Un componente assente non deve avviare l'aggiornamento ordinario.");
    False(result.Plan.Components[0].NeedsUpdate, "Il componente assente doveva essere ignorato.");
    False(result.Plan.Components[0].IsPresent, "Il componente e stato rilevato erroneamente come presente.");
    False(Directory.Exists(Path.Combine(ksp, "GameData", "TestMod")), "L'update ordinario ha installato una mod assente.");
    False(Directory.Exists(launcherData), "Un update senza componenti non deve creare dati del launcher.");
}

static async Task ExplicitInstallAddsMissing()
{
    using var scope = new TempScope();
    var ksp = CreateKsp(scope.Root);
    var launcherData = Path.Combine(scope.Root, "LauncherData");
    var assets = Path.Combine(scope.Root, "assets");
    Directory.CreateDirectory(assets);
    var zip = Path.Combine(assets, "component.zip");
    CreateZip(zip, new Dictionary<string, string> { ["GameData/TestMod/new.txt"] = "installed" });
    var manifest = CreateManifest(await PackageService.ComputeSha256Async(zip));

    var result = await new UpdateEngine().RunAsync(
        manifest,
        new LauncherLocations(ksp, launcherData),
        assets,
        true,
        UpdatePolicy.InstallOrRepair);

    True(result.Applied, "L'installazione esplicita non e stata applicata.");
    Equal("installed", await File.ReadAllTextAsync(Path.Combine(ksp, "GameData", "TestMod", "new.txt")));
}

static async Task ThirdPartyModIsUntouched()
{
    using var scope = new TempScope();
    var ksp = CreateKsp(scope.Root);
    var launcherData = Path.Combine(scope.Root, "LauncherData");
    var assets = Path.Combine(scope.Root, "assets");
    Directory.CreateDirectory(assets);
    var ownedTarget = Path.Combine(ksp, "GameData", "TestMod");
    var thirdPartyTarget = Path.Combine(ksp, "GameData", "ThirdPartyMod");
    Directory.CreateDirectory(ownedTarget);
    Directory.CreateDirectory(thirdPartyTarget);
    await File.WriteAllTextAsync(Path.Combine(ownedTarget, "old.txt"), "old");
    await File.WriteAllTextAsync(Path.Combine(thirdPartyTarget, "important.cfg"), "third-party-content");
    var zip = Path.Combine(assets, "component.zip");
    CreateZip(zip, new Dictionary<string, string> { ["GameData/TestMod/new.txt"] = "new" });
    var manifest = CreateManifest(await PackageService.ComputeSha256Async(zip));

    await new UpdateEngine().RunAsync(manifest, new LauncherLocations(ksp, launcherData), assets, true);

    Equal("third-party-content", await File.ReadAllTextAsync(Path.Combine(thirdPartyTarget, "important.cfg")));
}

static async Task SupportLogPackageIsSafe()
{
    using var scope = new TempScope();
    var log = Path.Combine(scope.Root, "KSP.log");
    const string original = "KSP diagnostic content";
    await File.WriteAllTextAsync(log, original);
    var created = new DateTimeOffset(2026, 8, 20, 19, 45, 0, TimeSpan.Zero);
    var request = new SupportReportRequest(
        SupportReportType.Log, log, "The game stopped during vessel recovery.", "Fabio Test!", "KSR-42",
        "Space Race", null, "1.0.0", "1.12.5");

    var package = await new SupportReportPackager().CreateAsync(request, Path.Combine(scope.Root, "queue"), created);

    Equal("2026-08-20_194500Z_Fabio-Test_LOG.zip", package.FileName);
    Equal(original, await File.ReadAllTextAsync(log));
    using var archive = ZipFile.OpenRead(package.FilePath);
    True(archive.GetEntry("KSP.log") is not null, "KSP.log is missing from the support package.");
    True(archive.GetEntry("report.txt") is not null, "report.txt is missing from the support package.");
    True(archive.GetEntry("manifest.json") is not null, "manifest.json is missing from the support package.");
    using var reportReader = new StreamReader(archive.GetEntry("report.txt")!.Open());
    var report = await reportReader.ReadToEndAsync();
    True(report.Contains("The game stopped during vessel recovery.", StringComparison.Ordinal), "The player description is missing.");
    False(report.Contains("password", StringComparison.OrdinalIgnoreCase), "Unexpected secret-related content in report.txt.");
    using var manifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
    using var manifest = JsonDocument.Parse(await manifestReader.ReadToEndAsync());
    var logManifest = manifest.RootElement.GetProperty("files").EnumerateArray()
        .Single(item => item.GetProperty("path").GetString() == "KSP.log");
    Equal(await PackageService.ComputeSha256Async(log), logManifest.GetProperty("sha256").GetString()!);
}

static async Task SupportSavePackageIsSafe()
{
    using var scope = new TempScope();
    var save = Path.Combine(scope.Root, "saves", "KSR-Campaign");
    Directory.CreateDirectory(Path.Combine(save, "Ships", "VAB"));
    await File.WriteAllTextAsync(Path.Combine(save, "persistent.sfs"), "original-save");
    await File.WriteAllTextAsync(Path.Combine(save, "Ships", "VAB", "Rocket.craft"), "original-craft");
    var request = new SupportReportRequest(
        SupportReportType.Save, save, "The vessel disappeared after loading the save.", "Fabio", "KSR-42",
        "Space Race", "KSR-Campaign", "1.0.0", "1.12.5");

    var package = await new SupportReportPackager().CreateAsync(request, Path.Combine(scope.Root, "queue"));

    Equal("original-save", await File.ReadAllTextAsync(Path.Combine(save, "persistent.sfs")));
    Equal("original-craft", await File.ReadAllTextAsync(Path.Combine(save, "Ships", "VAB", "Rocket.craft")));
    using var archive = ZipFile.OpenRead(package.FilePath);
    True(archive.GetEntry("save/persistent.sfs") is not null, "persistent.sfs is missing from the support package.");
    True(archive.GetEntry("save/Ships/VAB/Rocket.craft") is not null, "The craft file is missing from the support package.");
}

static async Task SupportDescriptionIsRequired()
{
    using var scope = new TempScope();
    var log = Path.Combine(scope.Root, "KSP.log");
    await File.WriteAllTextAsync(log, "log");
    var request = new SupportReportRequest(
        SupportReportType.Log, log, "too short", "Fabio", null, null, null, "1.0.0", "1.12.5");
    await ThrowsAsync<ArgumentException>(() =>
        new SupportReportPackager().CreateAsync(request, Path.Combine(scope.Root, "queue")));
    False(Directory.Exists(Path.Combine(scope.Root, "queue", ".work")), "A rejected report left temporary data behind.");
}

static async Task SupportUploadIsAuthenticated()
{
    using var scope = new TempScope();
    var packagePath = Path.Combine(scope.Root, "report.zip");
    CreateZip(packagePath, new Dictionary<string, string> { ["report.txt"] = "test" });
    var package = new SupportReportPackage(
        packagePath, "report.zip", await PackageService.ComputeSha256Async(packagePath),
        new FileInfo(packagePath).Length, DateTimeOffset.UtcNow, SupportReportType.Log, "KSR-42");
    var requestChecked = false;
    using var http = new HttpClient(new FakeHttpHandler(request =>
    {
        Equal("https://ksr.example/api/v1/support/reports", request.RequestUri!.ToString());
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        Equal("secret-token", request.Headers.Authorization.Parameter!);
        True(request.Content is MultipartFormDataContent, "The support request is not multipart/form-data.");
        requestChecked = true;
        return "{\"reportId\":\"KSR-RPT-000001\",\"status\":\"received\",\"receivedAtUtc\":\"2026-08-20T19:45:05Z\"}";
    }));

    var result = await new SupportReportUploader(http).UploadAsync("https://ksr.example", "secret-token", package);
    True(requestChecked, "The support endpoint was not called.");
    Equal("KSR-RPT-000001", result.ReportId);
    await ThrowsAsync<ArgumentException>(() =>
        new SupportReportUploader(http).UploadAsync("http://ksr.example", "secret-token", package));
}

static async Task PlatformLoginParsesSession()
{
    var requestChecked = false;
    using var http = new HttpClient(new FakeHttpHandler(request =>
    {
        Equal("https://ksr.example/api/v1/auth/login", request.RequestUri!.ToString());
        Equal("POST", request.Method.Method);
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(body);
        Equal("Fabio", document.RootElement.GetProperty("username").GetString()!);
        Equal("secret", document.RootElement.GetProperty("password").GetString()!);
        requestChecked = true;
        return "{\"accessToken\":\"access-1\",\"refreshToken\":\"refresh-1\",\"expiresIn\":1800,\"user\":{\"id\":7,\"username\":\"Fabio\"}}";
    }));

    var session = await new KsrPlatformClient(http).LoginAsync("https://ksr.example", "Fabio", "secret");
    True(requestChecked, "The login endpoint was not called.");
    Equal("access-1", session.AccessToken);
    Equal("refresh-1", session.RefreshToken);
    Equal("Fabio", session.User.Username);
    True(session.User.Id == 7, "The user ID was not parsed.");
}

static async Task PlatformCampaignsUseBearerSession()
{
    using var http = new HttpClient(new FakeHttpHandler(request =>
    {
        Equal("https://ksr.example/api/v1/campaigns", request.RequestUri!.ToString());
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        Equal("access-1", request.Headers.Authorization.Parameter!);
        return "{\"ok\":true,\"campaigns\":[{\"campaignCode\":\"KSR-20260820-ABC\",\"name\":\"Lunar Race\",\"status\":\"active\",\"role\":\"admin\",\"nationId\":null,\"masterSaveSha256\":\"abc123\",\"masterSaveSize\":12345}]}";
    }));

    var campaigns = await new KsrPlatformClient(http).GetCampaignsAsync("https://ksr.example", "access-1");
    True(campaigns.Count == 1, "The campaign list was not parsed.");
    Equal("KSR-20260820-ABC", campaigns[0].CampaignCode);
    Equal("Lunar Race", campaigns[0].Name);
    Equal("admin", campaigns[0].Role);
    True(campaigns[0].MasterSaveSize == 12345, "Master Save metadata was not parsed.");
}

static async Task PlatformRegistrationSendsEmail()
{
    using var http = new HttpClient(new FakeHttpHandler(request =>
    {
        Equal("https://ksr.example/api/v1/auth/register", request.RequestUri!.ToString());
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(body);
        Equal("Fabio", document.RootElement.GetProperty("username").GetString()!);
        Equal("fabio@example.com", document.RootElement.GetProperty("email").GetString()!);
        Equal("safe-password", document.RootElement.GetProperty("password").GetString()!);
        return "{\"ok\":true}";
    }));

    await new KsrPlatformClient(http).RegisterAsync(
        "https://ksr.example", "Fabio", "fabio@example.com", "safe-password");
}

static async Task PlatformPasswordRecoveryUsesV1Endpoints()
{
    var forgotCalled = false;
    var resetCalled = false;
    using var http = new HttpClient(new FakeHttpHandler(request =>
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(body);
        if (request.RequestUri!.AbsolutePath.EndsWith("/forgot-password", StringComparison.Ordinal))
        {
            Equal("fabio@example.com", document.RootElement.GetProperty("email").GetString()!);
            forgotCalled = true;
        }
        else if (request.RequestUri.AbsolutePath.EndsWith("/reset-password", StringComparison.Ordinal))
        {
            Equal("reset-token", document.RootElement.GetProperty("resetToken").GetString()!);
            Equal("new-password", document.RootElement.GetProperty("password").GetString()!);
            resetCalled = true;
        }
        else throw new Exception("Unexpected password recovery endpoint.");
        return "{\"ok\":true}";
    }));
    var client = new KsrPlatformClient(http);

    await client.RequestPasswordResetAsync("https://ksr.example", "fabio@example.com");
    await client.ResetPasswordAsync("https://ksr.example", "reset-token", "new-password");
    True(forgotCalled, "The forgot-password endpoint was not called.");
    True(resetCalled, "The reset-password endpoint was not called.");
}

static async Task PlatformHealthUsesV1Contract()
{
    using var http = new HttpClient(new FakeHttpHandler(request =>
    {
        Equal("https://play.kerbalspacerace.net/api/v1/health", request.RequestUri!.ToString());
        return "{\"ok\":true,\"service\":\"ksr-platform\",\"version\":\"v1\",\"legacy\":true}";
    }));

    var health = await new KsrPlatformClient(http).GetHealthAsync(KsrPlatformClient.ProductionServerUrl);
    Equal("ksr-platform", health.Service);
    Equal("v1", health.Version!);
    True(health.Legacy, "Legacy compatibility flag was not parsed.");
}

static async Task PlatformAuthenticationPreservesErrorCode()
{
    using var http = new HttpClient(new FakeHttpHandler(
        _ => "{\"error\":{\"code\":\"email_not_verified\",\"message\":\"Email verification required\"}}",
        HttpStatusCode.Unauthorized));

    try
    {
        await new KsrPlatformClient(http).LoginAsync("https://ksr.example", "Fabio", "safe-password");
        throw new Exception("The authentication request should have failed.");
    }
    catch (KsrApiException exception)
    {
        Equal("email_not_verified", exception.Code!);
        True(exception.StatusCode == 401, "The authentication HTTP status was not preserved.");
    }
}

static ReleaseManifest CreateManifest(string hash) => new()
{
    SchemaVersion = 1,
    Product = "KerbalSpaceRace",
    Version = "1.0.0",
    MinimumLauncherVersion = "1.0.0",
    Components = [new ComponentManifest
    {
        Id = "test-component", TransactionGroup = "ksp-client", Asset = "component.zip", Sha256 = hash,
        Source = "GameData/TestMod", Target = "GameData/TestMod", TargetKind = "ksp", Required = true,
        RequiredFiles = ["new.txt"]
    }]
};

static string CreateKsp(string root)
{
    var ksp = Path.Combine(root, "KSP");
    Directory.CreateDirectory(Path.Combine(ksp, "GameData"));
    File.WriteAllText(Path.Combine(ksp, "KSP_x64.exe"), "test");
    return ksp;
}

static void CreateZip(string path, IReadOnlyDictionary<string, string> files)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var pair in files) { var entry = archive.CreateEntry(pair.Key); using var writer = new StreamWriter(entry.Open()); writer.Write(pair.Value); }
}

static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
static void False(bool condition, string message) => True(!condition, message);
static void Equal(string expected, string actual) { if (expected != actual) throw new Exception($"Atteso '{expected}', ottenuto '{actual}'."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Eccezione {typeof(T).Name} non generata."); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"Eccezione {typeof(T).Name} non generata."); }

sealed class TempScope : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ksr-launcher-tests", Guid.NewGuid().ToString("N"));
    public TempScope() => Directory.CreateDirectory(Root);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

sealed class FakeHttpHandler(
    Func<HttpRequestMessage, string> responseFactory,
    HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseFactory(request), Encoding.UTF8, "application/json")
        });
}
