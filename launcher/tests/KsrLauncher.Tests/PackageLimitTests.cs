using System.IO.Compression;
using System.Text;
using KsrLauncher.Core;

internal static class PackageLimitTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ksr-package-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void MakeZip(string path, params (string Name, string Content)[] files)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open(), new UTF8Encoding(false));
            writer.Write(file.Content);
        }
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Unsafe ZIP exceeded its budget without rejection.");
    }

    public static Task MetadataBudgetsRejectBeforeWriting()
    {
        var root = NewRoot();
        try
        {
            var zip = Path.Combine(root, "package.zip");
            var target = Path.Combine(root, "extract");
            MakeZip(zip, ("one.txt", "1234567890"), ("two.txt", "1234567890"));
            foreach (var limits in new[] {
                new PackageExtractionLimits(20,9,2), new PackageExtractionLimits(19,10,2),
                new PackageExtractionLimits(20,10,1), new PackageExtractionLimits(20,10,2,1) })
            {
                Reject(() => PackageService.ExtractSafely(zip, target, limits));
                if (Directory.Exists(target)) throw new Exception("Oversized ZIP wrote output before metadata rejection.");
            }
            PackageService.ExtractSafely(zip, target, new PackageExtractionLimits(20,10,2));
            if (File.ReadAllText(Path.Combine(target,"two.txt")) != "1234567890") throw new Exception("Exact limit boundary failed.");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    public static Task ActualBytesCannotBypassZipMetadata()
    {
        var root = NewRoot();
        try
        {
            var zip = Path.Combine(root, "package.zip");
            MakeZip(zip, ("payload.txt",new string('x',1024)));
            var bytes = File.ReadAllBytes(zip);
            for(var i=0;i+46<=bytes.Length;i++)
                if(BitConverter.ToUInt32(bytes,i)==0x02014b50) BitConverter.GetBytes(1).CopyTo(bytes,i+24);
            File.WriteAllBytes(zip,bytes);
            Reject(() => PackageService.ExtractSafely(zip,Path.Combine(root,"extract"),new PackageExtractionLimits(32,32,2)));
        }
        finally { Directory.Delete(root,true); }
        return Task.CompletedTask;
    }

    public static async Task RejectedZipPreservesInstalledContent()
    {
        var root = NewRoot();
        var previous=Environment.GetEnvironmentVariable("KSR_ZIP_MAX_TOTAL_BYTES");
        try
        {
            var ksp=Path.Combine(root,"KSP");Directory.CreateDirectory(Path.Combine(ksp,"GameData","TestMod"));
            File.WriteAllText(Path.Combine(ksp,"KSP_x64.exe"),"fixture");
            var keep=Path.Combine(ksp,"GameData","TestMod","keep.cfg");File.WriteAllText(keep,"existing configuration");
            var zip=Path.Combine(root,"component.zip");MakeZip(zip,("payload/new.dll",new string('x',100)));
            var manifest=new ReleaseManifest{SchemaVersion=1,Product="KerbalSpaceRace",Version="1.0.0",MinimumLauncherVersion="0.1.0",
                Components=[new ComponentManifest{Id="client",Asset="component.zip",Sha256=await PackageService.ComputeSha256Async(zip),Source="payload",Target="GameData/TestMod",Version="1.0.0"}]};
            Environment.SetEnvironmentVariable("KSR_ZIP_MAX_TOTAL_BYTES","4");
            try {await new UpdateEngine().RunAsync(manifest,new LauncherLocations(ksp,Path.Combine(root,"data")),root,true,UpdatePolicy.InstallOrRepair);}
            catch(InvalidDataException)
            {
                if(File.ReadAllText(keep)!="existing configuration"||File.Exists(Path.Combine(ksp,"GameData","TestMod","new.dll")))throw new Exception("Rejected ZIP changed installed files.");
                return;
            }
            throw new Exception("Update installed an oversized ZIP.");
        }
        finally {Environment.SetEnvironmentVariable("KSR_ZIP_MAX_TOTAL_BYTES",previous);Directory.Delete(root,true);}
    }

    public static async Task DownloadChecksActualBytesDespiteFalseHeader()
    {
        var root=NewRoot();
        var previous=Environment.GetEnvironmentVariable("KSR_ZIP_MAX_ARCHIVE_BYTES");
        try
        {
            Environment.SetEnvironmentVariable("KSR_ZIP_MAX_ARCHIVE_BYTES","8");
            var service=new PackageService(new HttpClient(new FalseLengthHandler()));
            var component=new ComponentManifest{Asset="package.zip",AssetUrl="https://example.test/package.zip",Sha256=new string('a',64)};
            try{await service.AcquireAsync("",component,root,CancellationToken.None);}
            catch(InvalidDataException){return;}
            throw new Exception("Download ignored the actual byte budget.");
        }
        finally {Environment.SetEnvironmentVariable("KSR_ZIP_MAX_ARCHIVE_BYTES",previous);Directory.Delete(root,true);}
    }

    private sealed class FalseLengthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var response=new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new ByteArrayContent(new byte[16])};
            response.Content.Headers.ContentLength=4;
            return Task.FromResult(response);
        }
    }
}
