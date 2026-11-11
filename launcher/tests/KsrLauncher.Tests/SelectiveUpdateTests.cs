using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KsrLauncher.Core;

internal static class SelectiveUpdateTests
{
    public static async Task SelectiveAndRepair()
    {
        using var fixture = new Fixture();
        var originalLarge = await File.ReadAllBytesAsync(Path.Combine(fixture.Target, "large.bin"));
        await fixture.Update(UpdatePolicy.VerifyAllFiles);
        Check(fixture.Handler.FullDownloads == 0, "Upgrade downloaded the whole ZIP");
        Check(fixture.Handler.Ranges.Count == 1, "Upgrade downloaded unchanged files");
        Check(fixture.Handler.Ranges.Single().From == fixture.Index.Files.Single(f => f.Path == "main.dll").Offset, "Wrong file range");
        Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target, "main.dll")) == "NEW OFFICIAL DLL", "DLL not updated");
        Check(originalLarge.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(fixture.Target, "large.bin"))), "Unchanged file changed");
        Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target, "personal.cfg")) == "PERSONAL", "Personal file lost");
        var requests = fixture.Handler.Requests;
        await fixture.Update(UpdatePolicy.VerifyAllFiles);
        Check(fixture.Handler.Requests == requests, "No-op requested payloads");
        File.Delete(Path.Combine(fixture.Target, "main.dll"));
        await fixture.Update(UpdatePolicy.VerifyAllFiles);
        Check(fixture.Handler.FullDownloads == 0 && fixture.Handler.Ranges.Count == 2, "Missing file did not use selective download");
        await fixture.Update(UpdatePolicy.ReinstallAll);
        Check(fixture.Handler.FullDownloads == 1, "Repair did not download the complete package");
        Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target, "personal.cfg")) == "PERSONAL", "Repair removed personal data");
    }

    public static async Task RejectionsAndCancellation()
    {
        foreach (var failure in new[] { "index", "hash", "ignored-range", "truncated", "cancel" })
        {
            using var fixture = new Fixture();
            fixture.Handler.Failure = failure;
            var before = await File.ReadAllBytesAsync(StateStore.GetStatePath(fixture.Data));
            var failed = false;
            try { await fixture.Update(UpdatePolicy.VerifyAllFiles); }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { failed = true; }
            Check(failed, "Unsafe transfer accepted: " + failure);
            Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target, "main.dll")) == "OLD DLL", "Failure changed installed files");
            Check(before.SequenceEqual(await File.ReadAllBytesAsync(StateStore.GetStatePath(fixture.Data))), "Failure changed installed state");
        }
        using var metadata = new Fixture();
        foreach (var path in new[] { "../escape", "file:stream", "A/../B", "/root", "empty//file" })
        {
            var invalid = new PackageFileIndex { ArchiveSha256 = metadata.Component.Sha256, ArchiveSize = metadata.Component.Size,
                Source = metadata.Component.Source, Files = [metadata.Index.Files[0] with { Path = path }] };
            var rejected = false;
            try { SelectivePackageService.Validate(invalid, metadata.Component); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Unsafe index path accepted");
        }
    }

    public static async Task DurableRecovery()
    {
        using var fixture = new Fixture();
        var backup = Path.Combine(fixture.Data, ".ksr-launcher", "backups", "synthetic-interruption");
        var copy = Path.Combine(backup, "components", "test"); Directory.CreateDirectory(copy);
        foreach (var file in Directory.EnumerateFiles(fixture.Target)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        File.Copy(StateStore.GetStatePath(fixture.Data), Path.Combine(backup, "installed-state.previous.json"));
        var journal = new BackupJournal { SchemaVersion = 2, InProgress = true, KspRoot = fixture.Root, LauncherDataRoot = fixture.Data,
            ReleaseVersion = "1.0.1", Entries = [new BackupEntry { ComponentId = "test", Target = "GameData/Test", TargetKind = "ksp", BackupRelativePath = "components/test", HadOriginal = true }] };
        var orphan = Path.Combine(fixture.Root, "GameData", "Test.ksr-new-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan); File.WriteAllText(Path.Combine(orphan,"old.dll"),"uncommitted staged file");
        journal.StagingDirectories.Add(new StagedUpdateDirectory("ksp", Path.GetRelativePath(fixture.Root,orphan)));
        await File.WriteAllTextAsync(Path.Combine(backup, "backup-manifest.json"), JsonSerializer.Serialize(journal, ManifestService.JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(fixture.Target, "main.dll"), "INTERRUPTED WRITE");
        await new UpdateEngine(new PackageService(new HttpClient(fixture.Handler))).RunAsync(fixture.Manifest,
            new LauncherLocations(fixture.Root, fixture.Data), "https://packages.test/files", false);
        Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target, "main.dll")) == "INTERRUPTED WRITE", "Read-only planning wrote files");
        await fixture.Update(UpdatePolicy.VerifyAllFiles);
        Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target, "main.dll")) == "NEW OFFICIAL DLL", "Recovery/retry did not complete");
        Check(!Directory.Exists(orphan), "Interrupted staging directory remained inside GameData");
        Check(!JsonSerializer.Deserialize<BackupJournal>(await File.ReadAllTextAsync(Path.Combine(backup, "backup-manifest.json")), ManifestService.JsonOptions)!.InProgress,
            "Recovery remained active");
        Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target, "personal.cfg")) == "PERSONAL", "Recovery lost user content");
    }

    public static async Task SharedOverlayOrder()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Target,"main.dll"),"LAST PATCH");
        using var bytes = new MemoryStream();
        using(var archive=new ZipArchive(bytes,ZipArchiveMode.Create,true))
        { using var writer=new StreamWriter(archive.CreateEntry("payload/main.dll").Open());writer.Write("LAST PATCH"); }
        var zip=bytes.ToArray(); long compressed;
        using(var archive=new ZipArchive(new MemoryStream(zip),ZipArchiveMode.Read)) compressed=archive.Entries[0].CompressedLength;
        var offset=30+BitConverter.ToUInt16(zip,26)+BitConverter.ToUInt16(zip,28);
        var index=new PackageFileIndex { ArchiveSize=zip.Length,ArchiveSha256=Hash(zip),Source="payload",
            Files=[new IndexedPackageFile("main.dll",Encoding.UTF8.GetByteCount("LAST PATCH"),Hash(Encoding.UTF8.GetBytes("LAST PATCH")),offset,compressed,8)] };
        using var encoded=new MemoryStream();
        using(var gzip=new GZipStream(encoded,CompressionLevel.Optimal,true)) gzip.Write(JsonSerializer.SerializeToUtf8Bytes(index,ManifestService.JsonOptions));
        var indexBytes=encoded.ToArray();
        var layer=new ComponentManifest { Id="layer",Version="1.0.1",Asset="layer.zip",AssetUrl="https://packages.test/files/layer.zip",
            Size=zip.Length,Sha256=Hash(zip),Source="payload",Target="GameData/Test",InstallMode="overlay",RequiredFiles=["main.dll"],
            FileIndex=new FileIndexReference { Asset="layer.files.json.gz",AssetUrl="https://packages.test/files/layer.files.json.gz",Size=indexBytes.Length,Sha256=Hash(indexBytes) } };
        fixture.Manifest.Components.Add(layer);
        fixture.Handler.Extra["/files/layer.zip"]=zip;fixture.Handler.Extra["/files/layer.files.json.gz"]=indexBytes;
        await fixture.Update(UpdatePolicy.VerifyAllFiles);
        Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target,"main.dll"))=="LAST PATCH","Selective reuse changed shared overlay order");
        Check(fixture.Handler.Ranges.Count==2 && fixture.Handler.FullDownloads==0,"Shared file conflict was not downloaded in both signed variants");
    }

    public static async Task GitHubRangeFallback()
    {
        using var fixture = new Fixture();
        fixture.Component.AssetUrl = "https://github.com/example/repo/releases/download/v1.0.1/test.zip";
        fixture.Handler.Failure = "github-range";
        await fixture.Update(UpdatePolicy.VerifyAllFiles);
        Check(fixture.Handler.LookupCount == 1 && fixture.Handler.Ranges.Count == 1 && fixture.Handler.FullDownloads == 0,
            "Public API fallback lost its range or downloaded the full ZIP");
    }

    public static async Task ProcessInterruption()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "fixture.zip"), fixture.Zip);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "fixture-index.gz"), fixture.IndexBytes);
        var second = JsonSerializer.Deserialize<ComponentManifest>(JsonSerializer.Serialize(fixture.Component, ManifestService.JsonOptions), ManifestService.JsonOptions)!;
        second.Id = "second"; second.Target = "GameData/Second";
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(JsonSerializer.Serialize(fixture.Manifest, ManifestService.JsonOptions), ManifestService.JsonOptions)!;
        manifest.Components.Add(second);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "fixture-manifest.json"), JsonSerializer.Serialize(manifest, ManifestService.JsonOptions));
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SelectiveUpdateTests).Assembly.Location);
        start.ArgumentList.Add("selective-crash-child"); start.ArgumentList.Add(fixture.Root);
        using var child = System.Diagnostics.Process.Start(start) ?? throw new Exception("Cannot start synthetic child");
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(fixture.Root, "ready-to-kill")) && !child.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Check(File.Exists(Path.Combine(fixture.Root, "ready-to-kill")), "Synthetic child did not reach the persisted transaction boundary");
            Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target, "main.dll")) == "NEW OFFICIAL DLL", "Child had not committed the first component");
            child.Kill(); await child.WaitForExitAsync();
            await fixture.Update(UpdatePolicy.VerifyAllFiles);
            Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target, "main.dll")) == "NEW OFFICIAL DLL", "Interrupted update did not recover and retry");
            Check(await File.ReadAllTextAsync(Path.Combine(fixture.Target, "personal.cfg")) == "PERSONAL", "Process interruption lost personal files");
            Check(!Directory.Exists(Path.Combine(fixture.Root, "GameData", "Second")), "Unapplied second component appeared during recovery");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }

    public static async Task CrashChild(string root)
    {
        var manifest = await ManifestService.LoadAsync(Path.Combine(root, "fixture-manifest.json"));
        var handler = new Handler(await File.ReadAllBytesAsync(Path.Combine(root, "fixture.zip")), await File.ReadAllBytesAsync(Path.Combine(root, "fixture-index.gz")));
        var progress = new DirectProgress(value =>
        {
            if (value.Phase == "INSTALLING" && value.ComponentsCompleted == 1)
            {
                var journalFile = Directory.GetFiles(Path.Combine(root, "LauncherData", ".ksr-launcher", "backups"), "backup-manifest.json", SearchOption.AllDirectories).Single();
                var journal = JsonSerializer.Deserialize<BackupJournal>(File.ReadAllText(journalFile), ManifestService.JsonOptions)!;
                Check(journal.SchemaVersion == 2 && journal.InProgress && journal.Entries.Count == 1, "No durable rollback journal before the next component");
                File.WriteAllText(Path.Combine(root, "ready-to-kill"), "ready");
                // Only the synthetic subprocess waits here. The parent kills it within 30 seconds.
                Thread.Sleep(Timeout.Infinite);
            }
        });
        await new UpdateEngine(new PackageService(new HttpClient(handler))).RunAsync(manifest,
            new LauncherLocations(root, Path.Combine(root, "LauncherData")), "https://packages.test/files", true,
            UpdatePolicy.VerifyAllFiles, progress);
    }

    sealed class DirectProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }

    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    sealed class Fixture : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "selective-update-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "LauncherData");
        public string Target => Path.Combine(Root, "GameData", "Test");
        public ComponentManifest Component;
        public ReleaseManifest Manifest;
        public PackageFileIndex Index;
        public Handler Handler;
        public byte[] Zip;
        public byte[] IndexBytes;
        public Fixture()
        {
            Directory.CreateDirectory(Target); File.WriteAllText(Path.Combine(Root, "KSP_x64.exe"), "fixture");
            File.WriteAllText(Path.Combine(Target, "main.dll"), "OLD DLL");
            var large = new byte[200000]; new Random(42).NextBytes(large); File.WriteAllBytes(Path.Combine(Target, "large.bin"), large);
            File.WriteAllText(Path.Combine(Target, "personal.cfg"), "PERSONAL");
            using var buffer = new MemoryStream();
            var contents = new Dictionary<string, byte[]> { ["main.dll"] = Encoding.UTF8.GetBytes("NEW OFFICIAL DLL"), ["large.bin"] = large };
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                foreach (var pair in contents) { using var stream = archive.CreateEntry("payload/" + pair.Key, CompressionLevel.Optimal).Open(); stream.Write(pair.Value); }
            var zip = buffer.ToArray(); Zip = zip;
            Component = new ComponentManifest { Id = "test", Version = "1.0.1", Asset = "test.zip", AssetUrl = "https://packages.test/files/test.zip",
                Sha256 = Hash(zip), Size = zip.Length, Source = "payload", Target = "GameData/Test", RequiredFiles = ["main.dll"], InstallMode = "overlay" };
            Index = new PackageFileIndex { ArchiveSha256 = Component.Sha256, ArchiveSize = zip.Length, Source = "payload" };
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                // The fixture's local headers are found through their exact UTF-8 entry names.
                foreach (var entry in archive.Entries)
                {
                    var name = Encoding.UTF8.GetBytes(entry.FullName); var header = Find(zip, name) - 30;
                    var offset = header + 30 + BitConverter.ToUInt16(zip, header + 26) + BitConverter.ToUInt16(zip, header + 28);
                    Index.Files.Add(new IndexedPackageFile(entry.Name, entry.Length, Hash(contents[entry.Name]), offset, entry.CompressedLength, 8));
                }
            }
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true)) gzip.Write(JsonSerializer.SerializeToUtf8Bytes(Index, ManifestService.JsonOptions));
            var indexBytes = compressed.ToArray(); IndexBytes = indexBytes;
            Component.FileIndex = new FileIndexReference { Asset = "test.files.json.gz", AssetUrl = "https://packages.test/files/test.files.json.gz", Size = indexBytes.Length, Sha256 = Hash(indexBytes) };
            Manifest = new ReleaseManifest { SchemaVersion = 1, Product = "KerbalSpaceRace", Version = "1.0.1", MinimumLauncherVersion = "0.7.35", Components = [Component] };
            Handler = new Handler(zip, indexBytes);
            StateStore.SaveAsync(Data, new InstalledState { Components = new(StringComparer.OrdinalIgnoreCase) {
                ["test"] = new InstalledComponent { Version = "1.0.0", Sha256 = new string('a',64), Target = "GameData/Test", TargetKind = "ksp", ExpectedFiles = ["main.dll", "large.bin"] } } }).GetAwaiter().GetResult();
        }
        public Task<UpdateResult> Update(UpdatePolicy policy) => new UpdateEngine(new PackageService(new HttpClient(Handler))).RunAsync(
            Manifest, new LauncherLocations(Root, Data), "https://packages.test/files", true, policy);
        public void Dispose() { Directory.Delete(Root, true); }
        static int Find(byte[] data, byte[] name) { for (var i=30;i<data.Length-name.Length;i++) if(data.AsSpan(i,name.Length).SequenceEqual(name)) return i; throw new Exception("ZIP fixture header missing"); }
    }

    sealed class Handler(byte[] zip, byte[] index) : HttpMessageHandler
    {
        public int Requests, FullDownloads, LookupCount;
        public List<RangeItemHeaderValue> Ranges = [];
        public Dictionary<string, byte[]> Extra = [];
        public string Failure = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Failure == "github-range" && request.RequestUri!.Host == "github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
            if (Failure == "github-range" && request.RequestUri!.AbsolutePath.EndsWith("/tags/v1.0.1"))
            {
                Check(request.Headers.Range is null, "Asset lookup received a byte range"); LookupCount++;
                var metadata = "{\"tag_name\":\"v1.0.1\",\"draft\":false,\"assets\":[{\"id\":123,\"name\":\"test.zip\",\"browser_download_url\":\"https://github.com/example/repo/releases/download/v1.0.1/test.zip\"}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata) });
            }
            var payload = Extra.TryGetValue(request.RequestUri!.AbsolutePath, out var extra) ? extra : zip;
            if (request.RequestUri!.AbsolutePath.EndsWith(".gz"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(extra ?? (Failure == "index" ? [1,2,3] : index)) });
            var range = request.Headers.Range?.Ranges.Single();
            if (range is null || Failure == "ignored-range") { FullDownloads++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }); }
            if (Failure == "cancel") throw new OperationCanceledException();
            Ranges.Add(range);
            var bytes = payload.AsSpan((int)range.From!.Value, (int)(range.To!.Value-range.From.Value+1)).ToArray();
            if (Failure == "hash") bytes[0] ^= 0xff;
            if (Failure == "truncated") bytes = bytes[..^1];
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(range.From.Value, range.To.Value, payload.Length);
            return Task.FromResult(response);
        }
    }
}
