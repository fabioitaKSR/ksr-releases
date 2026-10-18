using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KsrLauncher.Core;

internal sealed class TestSignatures : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    public ReleaseSignatures Verifier => new(Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo()));
    public byte[] Sign(byte[] payload) => JsonSerializer.SerializeToUtf8Bytes(new {
        algorithm = "RSA-PSS-SHA256",
        keyId = Convert.ToHexString(SHA256.HashData(_rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant(),
        signature = Convert.ToBase64String(_rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
    });
    public void Dispose() => _rsa.Dispose();
}

internal static class SecurityTests
{
    public static async Task InternalCatalogRequiresTrustedSignatureAndHttpsPackages()
    {
        using var key = new TestSignatures();
        using var otherKey = new TestSignatures();
        var root = Path.Combine(Path.GetTempPath(), "ksr-internal-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ksr-release.json");
        var manifest = new ReleaseManifest { SchemaVersion=1, Product="KerbalSpaceRace", Version="1.0.0", MinimumLauncherVersion="0.1.0",
            Components=[new ComponentManifest {Id="client",Asset="client.zip",AssetUrl="https://example.test/client.zip",Sha256=new string('a',64),Source="payload",Target="GameData/Test"}] };
        async Task WriteSigned(TestSignatures signer)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestService.JsonOptions);
            await File.WriteAllBytesAsync(path, bytes);
            await File.WriteAllBytesAsync(path + ".sig", signer.Sign(bytes));
        }
        async Task MustRejectAsync(Func<Task> action)
        {
            try { await action(); } catch (Exception ex) when (ex is InvalidDataException or IOException) { return; }
            throw new Exception("An unsafe internal catalog was accepted.");
        }
        try
        {
            var client = new GitHubReleaseClient(signatures:key.Verifier, internalSignedCatalog:path);
            await WriteSigned(key);
            var resolved = await client.ResolveAsync("fabioitaKSR/ksr-releases");
            if(resolved.Tag != "internal-1.0.0") throw new Exception("Valid signed internal catalog failed.");
            await File.AppendAllTextAsync(path, " ");
            await MustRejectAsync(() => client.ResolveAsync("fabioitaKSR/ksr-releases"));
            await WriteSigned(otherKey);
            await MustRejectAsync(() => client.ResolveAsync("fabioitaKSR/ksr-releases"));
            await WriteSigned(key);
            File.Delete(path + ".sig");
            await MustRejectAsync(() => client.ResolveAsync("fabioitaKSR/ksr-releases"));
            manifest.Components[0].AssetUrl="";
            await WriteSigned(key);
            await MustRejectAsync(() => client.ResolveAsync("fabioitaKSR/ksr-releases"));
            manifest.Components[0].AssetUrl="http://example.test/client.zip";
            await WriteSigned(key);
            await MustRejectAsync(() => client.ResolveAsync("fabioitaKSR/ksr-releases"));
            manifest.Components[0].AssetUrl="https://example.test/client.zip";
            manifest.Channel="beta";
            await WriteSigned(key);
            await MustRejectAsync(() => client.ResolveAsync("fabioitaKSR/ksr-releases"));
            await File.WriteAllBytesAsync(path, new byte[2 * 1024 * 1024 + 1]);
            await MustRejectAsync(() => client.ResolveAsync("fabioitaKSR/ksr-releases"));
            await WriteSigned(key);
            await File.WriteAllBytesAsync(path + ".sig",new byte[16 * 1024 + 1]);
            await MustRejectAsync(() => client.ResolveAsync("fabioitaKSR/ksr-releases"));
        }
        finally { Directory.Delete(root, true); }
    }

    public static Task SignaturesRejectTamperingAndOtherKeys()
    {
        using var key = new TestSignatures();
        using var otherKey = new TestSignatures();
        var payload = Encoding.UTF8.GetBytes("authorized release");
        var proof = key.Sign(payload);
        key.Verifier.Verify(payload, proof);
        MustReject(() => key.Verifier.Verify(Encoding.UTF8.GetBytes("modified release"), proof));
        MustReject(() => otherKey.Verifier.Verify(payload, proof));
        MustReject(() => key.Verifier.Verify(payload, Encoding.UTF8.GetBytes("{}")));
        return Task.CompletedTask;
    }

    public static async Task RejectUnsafeIdsBeforeFilesystemChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "ksr-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var id in new[] { "../../outside", "C:\\outside", "a/b", "a\\b", ".", new string('a', 81) })
            {
                var manifest = new ReleaseManifest {SchemaVersion=1,Product="KerbalSpaceRace",Version="1.0.0",MinimumLauncherVersion="0.1.0",
                    Components=[new ComponentManifest {Id=id,Asset="client.zip",Sha256=new string('a',64),Source="payload",Target="payload",TargetKind="launcherData"}]};
                try { await new UpdateEngine().RunAsync(manifest,new LauncherLocations(root,root),root,true,UpdatePolicy.InstallOrRepair); }
                catch(InvalidDataException) { continue; }
                throw new Exception("Unsafe component id was accepted.");
            }
            if (Directory.EnumerateFileSystemEntries(root).Any()) throw new Exception("Invalid manifest changed the filesystem.");
        }
        finally { Directory.Delete(root,true); }
    }

    public static async Task DownloadRejectsUnsignedOrModifiedAuthorization()
    {
        using var key = new TestSignatures();
        var metadata = new SignedLauncherMetadata(1,"KSRLauncher","1.0.0","update.exe","https://example.test/update.exe",new string('a',64),10);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(metadata,ManifestService.JsonOptions);
        var authorized=new LauncherUpdateRelease(new Version(1,0,0),"v1.0.0",metadata.AssetName,metadata.AssetUrl,metadata.Sha256,10,bytes,key.Sign(bytes));
        var service=new LauncherUpdateService(new HttpClient(),key.Verifier);
        foreach(var modified in new[] {authorized with {SignedMetadata=null},authorized with {Sha256=new string('b',64)},authorized with {AssetUrl="https://example.test/evil.exe"}})
        {
            try { await service.DownloadAsync(modified,Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"))); }
            catch(InvalidDataException) {continue;}
            throw new Exception("Changed authorization was accepted.");
        }
    }

    private static void MustReject(Action action)
    {
        try {action();} catch(InvalidDataException) {return;}
        throw new Exception("Untrusted release was accepted.");
    }
}
