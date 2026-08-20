using System.IO.Compression;
using KsrLauncher.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Manifest valido", ManifestValid),
    ("Manifest blocca path traversal", ManifestRejectsTraversal),
    ("ZIP blocca path traversal", ZipRejectsTraversal),
    ("Aggiornamento, preserve e rollback", UpdatePreserveRollback),
    ("SHA errato non modifica installazione", WrongHashDoesNotModify),
    ("Errore nel gruppo ripristina componenti precedenti", GroupFailureRollsBack)
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

    await ThrowsAsync<IOException>(() => new UpdateEngine().RunAsync(manifest, new LauncherLocations(ksp, launcherData), assets, true));
    Equal("old", await File.ReadAllTextAsync(Path.Combine(firstTarget, "old.txt")));
    False(File.Exists(Path.Combine(firstTarget, "new.txt")), "Il primo componente non e stato ripristinato.");
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
