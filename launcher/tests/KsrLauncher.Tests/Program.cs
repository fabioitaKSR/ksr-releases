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
    ("Launcher auto-update downloads a verified newer release", LauncherAutoUpdateDownloadsVerifiedRelease),
    ("Update ordinario ignora componente assente", ExistingOnlySkipsMissing),
    ("Installazione mancante richiede consenso esplicito", ExplicitInstallAddsMissing),
    ("Mod di terzi non viene toccata", ThirdPartyModIsUntouched),
    ("Support LOG package is safe and complete", SupportLogPackageIsSafe),
    ("Support SAVE package is safe and complete", SupportSavePackageIsSafe),
    ("Support report requires a useful description", SupportDescriptionIsRequired),
    ("Support upload uses authenticated HTTPS endpoint", SupportUploadIsAuthenticated),
    ("Platform login parses V1 session", PlatformLoginParsesSession),
    ("Platform refresh rotates remembered session", PlatformRefreshRotatesSession),
    ("Platform campaigns use bearer session data", PlatformCampaignsUseBearerSession),
    ("Platform campaign creation uploads baseline idempotently", PlatformCampaignCreationIsMultipartAndIdempotent),
    ("Platform joins campaign through authenticated endpoint", PlatformJoinCampaignIsAuthenticated),
    ("Platform downloads and verifies campaign artifacts", PlatformDownloadsVerifiedCampaignArtifacts),
    ("Platform closes campaign through authenticated endpoint", PlatformCloseCampaignIsAuthenticated),
    ("Platform registration sends private email payload", PlatformRegistrationSendsEmail),
    ("Platform password recovery uses V1 endpoints", PlatformPasswordRecoveryUsesV1Endpoints),
    ("Platform health uses production V1 contract", PlatformHealthUsesV1Contract),
    ("Platform authentication preserves server error code", PlatformAuthenticationPreservesErrorCode),
    ("Only one non-terminal admin campaign is allowed", OnlyOneAdminCampaignIsAllowed),
    ("Campaign baseline packages Career save safely", CampaignBaselinePackagesCareerSaveSafely),
    ("Campaign baseline reports GameData scanner activity", CampaignBaselineReportsScannerActivity),
    ("Campaign creation accepts Career and Science but rejects Sandbox", CampaignCreationAcceptsCareerAndScienceOnly),
    ("Campaign baseline detects mod and setting differences", CampaignBaselineDetectsDifferences),
    ("Campaign settings alignment backs up and preserves progress", CampaignSettingsAlignmentPreservesProgress),
    ("Campaign whitelist ignores exact GameData folder", CampaignWhitelistIgnoresExactFolder),
    ("Campaign whitelist rejects protected KSP and KSR folders", CampaignWhitelistRejectsProtectedFolders)
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

static async Task PlatformRefreshRotatesSession()
{
    var requestChecked = false;
    using var http = new HttpClient(new FakeHttpHandler(request =>
    {
        Equal("https://ksr.example/api/v1/auth/refresh", request.RequestUri!.ToString());
        Equal("POST", request.Method.Method);
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(body);
        Equal("refresh-old", document.RootElement.GetProperty("refreshToken").GetString()!);
        requestChecked = true;
        return "{\"accessToken\":\"access-new\",\"refreshToken\":\"refresh-new\",\"expiresIn\":1800,\"user\":{\"id\":7,\"username\":\"Fabio\"}}";
    }));

    var session = await new KsrPlatformClient(http).RefreshAsync("https://ksr.example", "refresh-old");
    True(requestChecked, "The refresh endpoint was not called.");
    Equal("access-new", session.AccessToken);
    Equal("refresh-new", session.RefreshToken);
    Equal("Fabio", session.User.Username);
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

static async Task PlatformCloseCampaignIsAuthenticated()
{
    var requestChecked = false;
    using var http = new HttpClient(new FakeHttpHandler(request =>
    {
        Equal("https://ksr.example/api/v1/campaigns/KSR-20260821-ABC/close", request.RequestUri!.ToString());
        Equal("POST", request.Method.Method);
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        Equal("access-1", request.Headers.Authorization.Parameter!);
        requestChecked = true;
        return "{\"ok\":true}";
    }));

    await new KsrPlatformClient(http).CloseCampaignAsync(
        "https://ksr.example", "access-1", "KSR-20260821-ABC");
    True(requestChecked, "The campaign close endpoint was not called.");
}

static async Task PlatformJoinCampaignIsAuthenticated()
{
    var requestChecked = false;
    using var http = new HttpClient(new FakeHttpHandler(request =>
    {
        Equal("https://ksr.example/api/v1/campaigns/KSR-20260822-ABC123/join", request.RequestUri!.ToString());
        Equal("POST", request.Method.Method);
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        Equal("access-1", request.Headers.Authorization.Parameter!);
        requestChecked = true;
        return "{\"ok\":true,\"campaign\":{\"campaignCode\":\"KSR-20260822-ABC123\",\"name\":\"Lunar Race\",\"status\":\"active\",\"role\":\"player\",\"nationId\":null,\"masterSaveSha256\":\"abc123\",\"masterSaveSize\":12345}}";
    }));

    var campaign = await new KsrPlatformClient(http).JoinCampaignAsync(
        "https://ksr.example", "access-1", "KSR-20260822-ABC123");
    True(requestChecked, "The campaign join endpoint was not called.");
    Equal("KSR-20260822-ABC123", campaign.CampaignCode);
    Equal("player", campaign.Role);
}

static async Task LauncherAutoUpdateDownloadsVerifiedRelease()
{
    using var scope = new TempScope();
    var executable = Encoding.UTF8.GetBytes("verified launcher update");
    var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(executable)).ToLowerInvariant();
    using var http = new HttpClient(new ResponseHttpHandler(request =>
    {
        var path = request.RequestUri!.AbsoluteUri;
        if (path.Contains("/releases?", StringComparison.Ordinal))
        {
            var json = "[{\"tag_name\":\"v0.1.2\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
                       "{\"name\":\"KSR-Launcher-v0.1.2-win-x64.exe\",\"browser_download_url\":\"https://downloads.example/launcher.exe\"}," +
                       "{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://downloads.example/SHA256SUMS.txt\"}]}]";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
        if (path.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{sha256}  KSR-Launcher-v0.1.2-win-x64.exe\n", Encoding.UTF8, "text/plain")
            };
        if (path.EndsWith("launcher.exe", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(executable) };
        throw new Exception("Unexpected auto-update endpoint.");
    }));
    var service = new LauncherUpdateService(http);
    var update = await service.CheckAsync("fabioitaKSR/ksr-releases", new Version(0, 1, 1));
    True(update is not null, "The newer launcher release was not detected.");
    Equal("v0.1.2", update!.Tag);
    var downloaded = await service.DownloadAsync(update, Path.Combine(scope.Root, "updates"));
    Equal(sha256, await PackageService.ComputeSha256Async(downloaded));
}

static async Task PlatformDownloadsVerifiedCampaignArtifacts()
{
    using var scope = new TempScope();
    var sourceMaster = Path.Combine(scope.Root, "source-master.zip");
    CreateZip(sourceMaster, new Dictionary<string, string> { ["persistent.sfs"] = "GAME\n{\n mode = CAREER\n}" });
    var masterBytes = await File.ReadAllBytesAsync(sourceMaster);
    var masterSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(masterBytes)).ToLowerInvariant();
    var manifest = new CampaignBaselineManifest
    {
        CampaignName = "Lunar Race",
        SourceSaveName = "LunarRace",
        KspVersion = "1.12.5",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        MasterSaveSha256 = masterSha,
        MasterSaveSize = masterBytes.Length,
        SaveFiles = [new BaselineFile("persistent.sfs", 1, "save")],
        GameDataFiles = [new BaselineFile("TestMod/mod.dll", 1, "mod")]
    };
    var baselineBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestService.JsonOptions);
    var baselineSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(baselineBytes)).ToLowerInvariant();
    var calls = 0;
    using var http = new HttpClient(new ResponseHttpHandler(request =>
    {
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        Equal("access-1", request.Headers.Authorization.Parameter!);
        calls++;
        if (request.RequestUri!.AbsolutePath.EndsWith("/master-save", StringComparison.Ordinal))
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(masterBytes) };
            response.Headers.TryAddWithoutValidation("X-KSR-Master-Save-SHA256", masterSha);
            response.Content.Headers.ContentLength = masterBytes.Length;
            return response;
        }
        if (request.RequestUri.AbsolutePath.EndsWith("/baseline", StringComparison.Ordinal))
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(baselineBytes) };
            response.Headers.TryAddWithoutValidation("X-KSR-Baseline-SHA256", baselineSha);
            return response;
        }
        throw new Exception("Unexpected campaign artifact endpoint.");
    }));
    var campaign = new KsrCampaign(
        "KSR-20260822-ABC123", "Lunar Race", "active", "player", null,
        masterSha, masterBytes.Length, 1, baselineSha);
    var destination = Path.Combine(scope.Root, "download");
    var package = await new KsrPlatformClient(http).DownloadCampaignArtifactsAsync(
        "https://ksr.example", "access-1", campaign, destination);

    True(calls == 2, "Both campaign artifacts were not downloaded.");
    Equal(masterSha, await PackageService.ComputeSha256Async(package.MasterSavePath));
    Equal(baselineSha, await PackageService.ComputeSha256Async(package.ManifestPath));
    Equal("Lunar Race", package.Manifest.CampaignName);
}

static async Task PlatformCampaignCreationIsMultipartAndIdempotent()
{
    using var scope = new TempScope();
    var directory = Path.Combine(scope.Root, "campaign");
    Directory.CreateDirectory(directory);
    var masterSave = Path.Combine(directory, "master-save.zip");
    CreateZip(masterSave, new Dictionary<string, string> { ["persistent.sfs"] = "GAME { Mode = CAREER }" });
    var masterSha = await PackageService.ComputeSha256Async(masterSave);
    var manifest = new CampaignBaselineManifest
    {
        CampaignName = "Lunar Race",
        SourceSaveName = "LunarRace",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        MasterSaveSha256 = masterSha,
        MasterSaveSize = new FileInfo(masterSave).Length
    };
    var baseline = Path.Combine(directory, "baseline.json");
    await File.WriteAllTextAsync(baseline, JsonSerializer.Serialize(manifest, ManifestService.JsonOptions));
    var baselineSha = await PackageService.ComputeSha256Async(baseline);
    var package = new CampaignBaselinePackage(directory, baseline, masterSave, manifest);
    string? firstIdempotencyKey = null;
    var calls = 0;
    using var http = new HttpClient(new FakeHttpHandler(request =>
    {
        Equal("https://ksr.example/api/v1/campaigns", request.RequestUri!.ToString());
        Equal("POST", request.Method.Method);
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        Equal("access-1", request.Headers.Authorization.Parameter!);
        var multipart = request.Content as MultipartFormDataContent ??
            throw new Exception("Campaign creation is not multipart/form-data.");
        var key = request.Headers.GetValues("Idempotency-Key").Single();
        if (firstIdempotencyKey is null) firstIdempotencyKey = key;
        else Equal(firstIdempotencyKey, key);
        var parts = multipart.ToList();
        True(parts.Any(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "name"), "Campaign name part is missing.");
        True(parts.Any(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "masterSave" && part.Headers.ContentType?.MediaType == "application/zip"), "Master Save part is missing.");
        True(parts.Any(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "baseline" && part.Headers.ContentType?.MediaType == "application/json"), "Baseline part is missing.");
        calls++;
        return "{\"ok\":true,\"campaign\":{\"campaignCode\":\"KSR-20260821-ABC123\",\"name\":\"Lunar Race\",\"status\":\"active\",\"role\":\"admin\",\"nationId\":null,\"masterSaveSha256\":\"" + masterSha + "\",\"masterSaveSize\":" + manifest.MasterSaveSize + ",\"baselineSchemaVersion\":1,\"baselineSha256\":\"" + baselineSha + "\"}}";
    }));
    var client = new KsrPlatformClient(http);

    var first = await client.CreateCampaignAsync("https://ksr.example", "access-1", package);
    var retry = await client.CreateCampaignAsync("https://ksr.example", "access-1", package);

    Equal("KSR-20260821-ABC123", first.CampaignCode);
    Equal(first.CampaignCode, retry.CampaignCode);
    True(first.BaselineSchemaVersion == 1, "Baseline schema metadata was not parsed.");
    True(calls == 2, "The retry request was not sent.");
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

static Task OnlyOneAdminCampaignIsAllowed()
{
    True(CampaignRules.BlocksNewAdminCampaign("admin", "active"), "An active admin campaign must block creation.");
    True(CampaignRules.BlocksNewAdminCampaign("ADMIN", "DRAFT"), "A local admin draft must block creation.");
    True(CampaignRules.BlocksNewAdminCampaign("ADMIN", null), "An unknown admin campaign status must fail closed.");
    False(CampaignRules.BlocksNewAdminCampaign("player", "active"), "Player membership must not block admin creation.");
    foreach (var status in new[] { "closed", "completed", "cancelled", "archived", "ended" })
    {
        False(CampaignRules.BlocksNewAdminCampaign("admin", status), $"Terminal status {status} must allow a new campaign.");
        True(CampaignRules.IsTerminalStatus(status), $"Terminal status {status} was not recognized.");
    }
    return Task.CompletedTask;
}

static async Task CampaignBaselinePackagesCareerSaveSafely()
{
    using var scope = new TempScope();
    var (ksp, save) = CreateCampaignKsp(scope.Root);
    var campaignData = Path.Combine(save, "KSR_CampaignData");
    Directory.CreateDirectory(Path.Combine(campaignData, "previous-draft"));
    await File.WriteAllTextAsync(Path.Combine(campaignData, "previous-draft", "baseline.json"), "old-local-artifact");
    var package = await new CampaignBaselineBuilder().CreateAsync("Lunar Race", save, campaignData);

    True(File.Exists(package.ManifestPath), "The baseline manifest was not created.");
    True(File.Exists(package.MasterSavePath), "The Master Save was not created.");
    True(package.MasterSavePath.StartsWith(campaignData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
        "The Master Save was not stored inside the selected KSP save.");
    True(package.Manifest.GameDataFiles.Any(item => item.Path == "TestMod/Plugins/TestMod.dll"), "GameData was not captured.");
    True(package.Manifest.Settings.Any(item => item.Source == "KCT_Settings.cfg" && item.Key.EndsWith("OverallMultiplier")), "KCT settings were not parsed.");
    using var archive = ZipFile.OpenRead(package.MasterSavePath);
    True(archive.GetEntry("persistent.sfs") is not null, "persistent.sfs is missing from the Master Save.");
    True(archive.GetEntry("KCT_Settings.cfg") is not null, "KCT_Settings.cfg is missing from the Master Save.");
    True(archive.GetEntry("KCT_Backup.sfs") is null, "KCT backup must not be included in the Master Save.");
    False(archive.Entries.Any(entry => entry.FullName.StartsWith("KSR_CampaignData/", StringComparison.OrdinalIgnoreCase)),
        "Local campaign artifacts must not be packaged inside the Master Save.");
}

static async Task CampaignBaselineReportsScannerActivity()
{
    using var scope = new TempScope();
    var (_, save) = CreateCampaignKsp(scope.Root);
    var updates = new List<BaselineProgress>();
    await new CampaignBaselineBuilder().CreateAsync(
        "Lunar Race", save, Path.Combine(scope.Root, "drafts"), progress: new InlineProgress<BaselineProgress>(updates.Add));

    True(updates.Any(item => item.Stage == "Discovering GameData files"), "GameData discovery was not reported.");
    True(updates.Any(item => item.Stage == "Scanning GameData" && item.CurrentPath is not null),
        "GameData file reading was not reported.");
    True(updates.Any(item => item.Stage == "GameData reading complete" && item.Completed == item.Total),
        "GameData completion was not reported.");
}

static Task CampaignCreationAcceptsCareerAndScienceOnly()
{
    using var scope = new TempScope();
    var (_, save) = CreateCampaignKsp(scope.Root);
    KspCareerSaveLocator.Resolve(save);

    File.WriteAllText(Path.Combine(save, "persistent.sfs"), "GAME\n{\n mode = SCIENCE_SANDBOX\n}");
    KspCareerSaveLocator.Resolve(save);

    File.WriteAllText(Path.Combine(save, "persistent.sfs"), "GAME\n{\n mode = SANDBOX\n}");

    try
    {
        KspCareerSaveLocator.Resolve(save);
        throw new Exception("A Sandbox save must not be accepted as a campaign starting point.");
    }
    catch (InvalidDataException exception)
    {
        True(exception.Message.Contains("Career or Science", StringComparison.OrdinalIgnoreCase),
            "The unsupported game-mode validation message is unclear.");
    }
    return Task.CompletedTask;
}

static async Task CampaignBaselineDetectsDifferences()
{
    using var scope = new TempScope();
    var (ksp, save) = CreateCampaignKsp(scope.Root);
    var package = await new CampaignBaselineBuilder().CreateAsync("Lunar Race", save, Path.Combine(scope.Root, "drafts"));
    var comparer = new CampaignBaselineComparer();
    var matching = await comparer.CompareAsync(package.Manifest, save);
    True(matching.ReadyToLaunch, "An unchanged installation should match its baseline.");

    await File.WriteAllTextAsync(Path.Combine(ksp, "GameData", "TestMod", "Plugins", "TestMod.dll"), "modified");
    await File.WriteAllTextAsync(Path.Combine(save, "KCT_Settings.cfg"), "KCT_Preset\n{\n KCT_Preset_Time\n {\n  OverallMultiplier = 20\n }\n}");
    var changed = await comparer.CompareAsync(package.Manifest, save);
    False(changed.ReadyToLaunch, "A modified installation must not be launch-ready.");
    True(changed.Differences.Any(item => item.Area == BaselineDifferenceArea.GameData), "The modified mod file was not detected.");
    True(changed.Differences.Any(item => item.Area == BaselineDifferenceArea.ModConfiguration), "The modified KCT setting was not detected.");
}

static async Task CampaignSettingsAlignmentPreservesProgress()
{
    using var scope = new TempScope();
    var (ksp, save) = CreateCampaignKsp(scope.Root);
    var package = await new CampaignBaselineBuilder().CreateAsync("Lunar Race", save, Path.Combine(scope.Root, "drafts"));
    var persistent = Path.Combine(save, "persistent.sfs");
    await File.WriteAllTextAsync(persistent, "GAME\n{\n mode = CAREER\n progressMarker = KEEP_ME\n PARAMETERS\n {\n  DIFFICULTY\n  {\n   ReentryHeatScale = 0.5\n  }\n }\n}");
    await File.WriteAllTextAsync(Path.Combine(save, "KCT_Settings.cfg"), "KCT_Preset\n{\n KCT_Preset_Time\n {\n  OverallMultiplier = 20\n }\n}");
    var comparer = new CampaignBaselineComparer();
    var mismatch = await comparer.CompareAsync(package.Manifest, save);
    True(mismatch.ModsMatch && !mismatch.SettingsMatch, "The fixture should contain settings-only differences.");

    var aligned = await new CampaignSettingsAligner().AlignAsync(package, save, mismatch);
    True(Directory.Exists(aligned.BackupDirectory), "The settings backup was not created.");
    True(File.Exists(Path.Combine(aligned.BackupDirectory, "persistent.sfs")), "persistent.sfs was not backed up.");
    var text = await File.ReadAllTextAsync(persistent);
    True(text.Contains("progressMarker = KEEP_ME", StringComparison.Ordinal), "Save progress outside PARAMETERS was replaced.");
    True(text.Contains("ReentryHeatScale = 1.2", StringComparison.Ordinal), "Campaign difficulty was not aligned.");
    var result = await comparer.CompareAsync(package.Manifest, save);
    True(result.ReadyToLaunch, "The aligned save should match the campaign baseline.");
}

static async Task CampaignWhitelistIgnoresExactFolder()
{
    using var scope = new TempScope();
    var (ksp, save) = CreateCampaignKsp(scope.Root);
    var visualFolder = Path.Combine(ksp, "GameData", "PlayerVisuals");
    Directory.CreateDirectory(visualFolder);
    await File.WriteAllTextAsync(Path.Combine(visualFolder, "visual.dll"), "admin-version");
    var package = await new CampaignBaselineBuilder().CreateAsync(
        "Lunar Race", save, Path.Combine(scope.Root, "drafts"), ["PlayerVisuals"]);
    True(package.Manifest.IgnoredGameDataFolders.SequenceEqual(["PlayerVisuals"]), "The ignored folder was not stored.");
    False(package.Manifest.GameDataFiles.Any(item => item.Path.StartsWith("PlayerVisuals/", StringComparison.OrdinalIgnoreCase)),
        "Ignored folder files must not enter the baseline.");

    await File.WriteAllTextAsync(Path.Combine(visualFolder, "visual.dll"), "different-player-version");
    await File.WriteAllTextAsync(Path.Combine(visualFolder, "extra.cfg"), "anything");
    var result = await new CampaignBaselineComparer().CompareAsync(package.Manifest, save);
    True(result.ReadyToLaunch, "Changes inside an ignored GameData folder must not block campaign launch.");
}

static Task CampaignWhitelistRejectsProtectedFolders()
{
    foreach (var folder in new[]
             {
                 "Squad", "SquadExpansion", "KerbalSpaceRace", "KerbalSpaceRaceNationSelector",
                 "KerbalSpaceRaceSuite", "ContractPacks", "KSRParameterLogger", "KSRDisableDBSUI"
             })
    {
        Throws<InvalidDataException>(() => CampaignBaselineBuilder.NormalizeIgnoredFolders([folder]));
        True(CampaignBaselineBuilder.IsProtectedGameDataFolder(folder), $"{folder} was not recognized as protected.");
    }
    False(CampaignBaselineBuilder.IsProtectedGameDataFolder("EnvironmentalVisualEnhancements"),
        "An optional visual mod was incorrectly protected.");
    return Task.CompletedTask;
}

static (string Ksp, string Save) CreateCampaignKsp(string root)
{
    var ksp = CreateKsp(root);
    var plugin = Path.Combine(ksp, "GameData", "TestMod", "Plugins");
    Directory.CreateDirectory(plugin);
    File.WriteAllText(Path.Combine(plugin, "TestMod.dll"), "official");
    var save = Path.Combine(ksp, "saves", "Admin Career");
    Directory.CreateDirectory(save);
    File.WriteAllText(Path.Combine(save, "persistent.sfs"), "GAME\n{\n mode = CAREER\n PARAMETERS\n {\n  DIFFICULTY\n  {\n   ReentryHeatScale = 1.2\n  }\n }\n}");
    File.WriteAllText(Path.Combine(save, "KCT_Settings.cfg"), "KCT_Preset\n{\n KCT_Preset_Time\n {\n  OverallMultiplier = 38.4\n }\n}");
    File.WriteAllText(Path.Combine(save, "KCT_Backup.sfs"), "temporary");
    return (ksp, save);
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

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
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

sealed class ResponseHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(responseFactory(request));
}
