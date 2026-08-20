using System.IO.Compression;
using System.Security.Cryptography;

namespace KsrLauncher.Core;

public sealed class PackageService(HttpClient? httpClient = null)
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();

    public async Task<string> AcquireAsync(string assetsBase, ComponentManifest component, string downloadDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(downloadDirectory);
        var destination = Path.Combine(downloadDirectory, component.Asset);
        if (Uri.TryCreate(assetsBase, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var assetUri = new Uri(uri.ToString().TrimEnd('/') + "/" + Uri.EscapeDataString(component.Asset));
            using var response = await _httpClient.GetAsync(assetUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(destination);
            await source.CopyToAsync(output, cancellationToken);
        }
        else
        {
            var source = SafePaths.Under(Path.GetFullPath(assetsBase), component.Asset);
            File.Copy(source, destination, true);
        }

        var actualHash = await ComputeSha256Async(destination, cancellationToken);
        if (!string.Equals(actualHash, component.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new InvalidDataException($"SHA-256 errato per {component.Asset}. Atteso {component.Sha256}, ottenuto {actualHash}.");
        }
        return destination;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static void ExtractSafely(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000) throw new InvalidDataException($"Il pacchetto contiene un link simbolico non consentito: {entry.FullName}");
            var target = Path.GetFullPath(Path.Combine(destination, SafePaths.Normalize(entry.FullName)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Voce ZIP pericolosa: {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }
        }
    }
}
