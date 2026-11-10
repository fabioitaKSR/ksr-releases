using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace KsrLauncher.Core;

public sealed class SelectivePackageService(HttpClient http)
{
    public const int MaximumIndexBytes = 2 * 1024 * 1024;
    private const int MaximumExpandedIndexBytes = 32 * 1024 * 1024;

    public async Task<PackageFileIndex> LoadIndexAsync(ComponentManifest component, string assetsBase, CancellationToken token)
    {
        var reference = component.FileIndex ?? throw new InvalidDataException("Missing file index.");
        byte[] compressed;
        if (Uri.TryCreate(assetsBase, UriKind.Absolute, out var origin) && origin.Scheme is "https" or "http" ||
            !string.IsNullOrWhiteSpace(reference.AssetUrl))
        {
            var url = string.IsNullOrWhiteSpace(reference.AssetUrl)
                ? assetsBase.TrimEnd('/') + "/" + Uri.EscapeDataString(reference.Asset) : reference.AssetUrl;
            compressed = await ReleaseSignatures.DownloadBoundedAsync(http, url, MaximumIndexBytes, token);
        }
        else
        {
            var file = SafePaths.Under(assetsBase, reference.Asset);
            if (new FileInfo(file).Length != reference.Size || reference.Size > MaximumIndexBytes)
                throw new InvalidDataException("Invalid file index size.");
            compressed = await File.ReadAllBytesAsync(file, token);
        }
        if (compressed.LongLength != reference.Size ||
            !Convert.ToHexString(SHA256.HashData(compressed)).Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file index failed its signed checksum verification.");
        using var input = new MemoryStream(compressed, false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var json = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await gzip.ReadAsync(buffer, token)) > 0)
        {
            if (json.Length + count > MaximumExpandedIndexBytes) throw new InvalidDataException("The file index is too large.");
            json.Write(buffer, 0, count);
        }
        var index = JsonSerializer.Deserialize<PackageFileIndex>(json.ToArray(), ManifestService.JsonOptions)
            ?? throw new InvalidDataException("Empty file index.");
        Validate(index, component);
        return index;
    }

    public static void Validate(PackageFileIndex index, ComponentManifest component)
    {
        var limits = PackageExtractionLimits.FromEnvironment();
        limits.Validate();
        if (index.SchemaVersion != 1 || index.Source != component.Source || index.ArchiveSize != component.Size ||
            !string.Equals(index.ArchiveSha256, component.Sha256, StringComparison.OrdinalIgnoreCase) ||
            index.ArchiveSize <= 0 || index.ArchiveSize > limits.MaxArchiveBytes || index.Files is null ||
            index.Files.Count == 0 || index.Files.Count > limits.MaxEntries)
            throw new InvalidDataException("The file index does not match the signed package.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in index.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || file.Path.Contains('\\') ||
                file.Path.Split('/').Any(part => part.Length == 0 || part is "." or ".." ||
                    part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c))) ||
                !paths.Add(file.Path)) throw new InvalidDataException("Unsafe or duplicate indexed file path.");
            _ = SafePaths.Under(Path.GetTempPath(), file.Path);
            if (file.Size < 0 || file.Size > limits.MaxFileBytes || file.Size > limits.MaxTotalBytes - total ||
                file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
                file.Offset < 0 || file.Offset > component.Size || file.CompressedSize < 0 ||
                file.CompressedSize > component.Size - file.Offset || file.Compression is not (0 or 8) ||
                file.Compression == 0 && file.CompressedSize != file.Size || file.CompressedSize == 0 && file.Size != 0)
                throw new InvalidDataException("Invalid indexed file size, hash or archive range.");
            total += file.Size;
        }
        foreach (var required in component.RequiredFiles)
        {
            var normalized = SafePaths.ManifestPath(required).TrimEnd('/');
            if (!index.Files.Any(file => file.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                file.Path.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The file index omits a required package file.");
        }
    }

    public static async Task<bool> MatchesAsync(string root, IndexedPackageFile file, CancellationToken token)
    {
        var path = SafePaths.Under(root, file.Path);
        SafePaths.RejectReparsePoints(Path.GetPathRoot(path)!, path);
        return File.Exists(path) && new FileInfo(path).Length == file.Size &&
            (await PackageService.ComputeSha256Async(path, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task DownloadFileAsync(ComponentManifest component, IndexedPackageFile file, string assetsBase,
        string destination, IProgress<long>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            await DownloadCompressedAsync(component, file, assetsBase, temporary, progress, token);
            await using (var input = File.OpenRead(temporary))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var decoder = file.Compression == 8 ? new DeflateStream(input, CompressionMode.Decompress, true) : null;
                var source = (Stream?)decoder ?? input;
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    if (count > file.Size - written) throw new InvalidDataException("The downloaded file expands beyond its signed size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    written += count;
                }
                if (written != file.Size) throw new InvalidDataException("The downloaded file is truncated.");
            }
            if (!(await PackageService.ComputeSha256Async(destination, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file failed its signed checksum verification.");
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task DownloadCompressedAsync(ComponentManifest component, IndexedPackageFile file, string assetsBase,
        string destination, IProgress<long>? progress, CancellationToken token)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        if (file.CompressedSize == 0) return;
        var url = component.AssetUrl;
        if (string.IsNullOrWhiteSpace(url) && Uri.TryCreate(assetsBase, UriKind.Absolute, out var origin) && origin.Scheme is "https" or "http")
            url = assetsBase.TrimEnd('/') + "/" + Uri.EscapeDataString(component.Asset);
        if (string.IsNullOrWhiteSpace(url))
        {
            await using var local = File.OpenRead(SafePaths.Under(assetsBase, component.Asset));
            if (local.Length != component.Size) throw new InvalidDataException("Package size changed.");
            local.Seek(file.Offset, SeekOrigin.Begin);
            await CopyRangeAsync(local, output, file.CompressedSize, progress, token);
            return;
        }
        using var response = await GitHubAssetDownloads.OpenAsync(http, url, token,
            new RangeHeaderValue(file.Offset, file.Offset + file.CompressedSize - 1));
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range is null || range.Unit != "bytes" ||
            range.From != file.Offset || range.To != file.Offset + file.CompressedSize - 1 || range.Length != component.Size ||
            response.Content.Headers.ContentLength != file.CompressedSize || response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException("The download server did not return the exact requested file range. No files were installed.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        await CopyRangeAsync(stream, output, file.CompressedSize, progress, token);
        if (await stream.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("Unexpected bytes after the requested file range.");
    }

    private static async Task CopyRangeAsync(Stream input, Stream output, long length, IProgress<long>? progress, CancellationToken token)
    {
        var buffer = new byte[81920];
        long copied = 0;
        while (copied < length)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), token);
            if (count == 0) throw new IOException("The file download was interrupted.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            copied += count;
            progress?.Report(copied);
        }
    }
}
