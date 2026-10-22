using System.IO.Compression;
using System.Security.Cryptography;

namespace KsrLauncher.Core;

public sealed class PackageService(HttpClient? httpClient = null)
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();
    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xEDB88320u : 0);
            table[i] = value;
        }
        return table;
    }

    public async Task<string> AcquireAsync(
        string assetsBase,
        ComponentManifest component,
        string downloadDirectory,
        CancellationToken cancellationToken,
        IProgress<long>? progress = null)
    {
        var limits = PackageExtractionLimits.FromEnvironment();
        limits.Validate();
        if (component.Size < 0 || component.Size > limits.MaxArchiveBytes)
            throw new InvalidDataException("The package exceeds the permitted download size.");
        Directory.CreateDirectory(downloadDirectory);
        var destination = SafePaths.Under(downloadDirectory, component.Asset);
        Uri? assetUri = null;
        if (!string.IsNullOrWhiteSpace(component.AssetUrl))
        {
            if (!Uri.TryCreate(component.AssetUrl, UriKind.Absolute, out assetUri) || assetUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"assetUrl non valido per {component.Id}.");
        }
        else if (Uri.TryCreate(assetsBase, UriKind.Absolute, out var baseUri) &&
                 (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps))
        {
            assetUri = new Uri(baseUri.ToString().TrimEnd('/') + "/" + Uri.EscapeDataString(component.Asset));
        }

        if (assetUri is not null)
        {
            using var response = await _httpClient.GetAsync(assetUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared &&
                (declared > limits.MaxArchiveBytes || (component.Size > 0 && declared != component.Size)))
                throw new InvalidDataException("The package response has an unexpected size.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(destination);
            var buffer = new byte[81920];
            long downloaded = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                var maximum = component.Size > 0 ? component.Size : limits.MaxArchiveBytes;
                if (read > maximum - downloaded)
                    throw new InvalidDataException("The package download exceeded its permitted size.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                progress?.Report(downloaded);
            }
        }
        else
        {
            var source = SafePaths.Under(Path.GetFullPath(assetsBase), component.Asset);
            var size = new FileInfo(source).Length;
            if (size > limits.MaxArchiveBytes || (component.Size > 0 && size != component.Size))
                throw new InvalidDataException("The package file has an unexpected size.");
            File.Copy(source, destination, true);
            progress?.Report(new FileInfo(destination).Length);
        }

        if (component.Size > 0 && new FileInfo(destination).Length != component.Size)
            throw new InvalidDataException("The package download is incomplete.");
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

    public static void ExtractSafely(string zipPath, string destination, PackageExtractionLimits? limits = null)
    {
        limits ??= PackageExtractionLimits.FromEnvironment();
        limits.Validate();
        if (new FileInfo(zipPath).Length > limits.MaxArchiveBytes)
            throw new InvalidDataException("The ZIP exceeds the permitted archive size.");
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        SafePaths.RejectReparsePoints(Path.GetPathRoot(root)!, destination);
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > limits.MaxEntries)
            throw new InvalidDataException("The ZIP contains too many entries.");
        long declaredTotal = 0;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Check all metadata before writing any output. Actual byte counts are checked below as well.
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > limits.MaxFileBytes || entry.Length > limits.MaxTotalBytes - declaredTotal)
                throw new InvalidDataException("The ZIP exceeds the permitted extracted size.");
            declaredTotal += entry.Length;
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000) throw new InvalidDataException($"Il pacchetto contiene un link simbolico non consentito: {entry.FullName}");
            var target = Path.GetFullPath(Path.Combine(destination, SafePaths.Normalize(entry.FullName)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Voce ZIP pericolosa: {entry.FullName}");
            SafePaths.RejectReparsePoints(destination, target);
            if (!targets.Add(target)) throw new InvalidDataException("The ZIP contains duplicate output paths.");
        }
        Directory.CreateDirectory(destination);
        long totalWritten = 0;
        var buffer = new byte[81920];
        foreach (var entry in archive.Entries)
        {
            var target = SafePaths.Under(destination, entry.FullName);
            SafePaths.RejectReparsePoints(destination, target);
            if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                long fileWritten = 0;
                uint crc = uint.MaxValue;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (read > limits.MaxFileBytes - fileWritten || read > limits.MaxTotalBytes - totalWritten)
                        throw new InvalidDataException("The ZIP exceeded the permitted extracted size while unpacking.");
                    output.Write(buffer, 0, read);
                    for (var index = 0; index < read; index++) crc = CrcTable[(crc ^ buffer[index]) & 255] ^ (crc >> 8);
                    fileWritten += read;
                    totalWritten += read;
                }
                if (fileWritten != entry.Length || (crc ^ uint.MaxValue) != entry.Crc32)
                    throw new InvalidDataException("The extracted file does not match the ZIP metadata.");
            }
        }
    }
}
