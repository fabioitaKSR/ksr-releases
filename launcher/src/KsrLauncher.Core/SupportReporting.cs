using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KsrLauncher.Core;

public enum SupportReportType
{
    Log,
    Save
}

public sealed record SupportReportRequest(
    SupportReportType Type,
    string SourcePath,
    string Description,
    string Username,
    string? CampaignCode,
    string? CampaignName,
    string? LocalSaveName,
    string LauncherVersion,
    string KspVersion);

public sealed record SupportReportPackage(
    string FilePath,
    string FileName,
    string Sha256,
    long Size,
    DateTimeOffset CreatedAtUtc,
    SupportReportType Type,
    string? CampaignCode);

public sealed record SupportUploadResult(string ReportId, string Status, DateTimeOffset ReceivedAtUtc);

public sealed class SupportReportPackager
{
    public async Task<SupportReportPackage> CreateAsync(
        SupportReportRequest request,
        string outputDirectory,
        DateTimeOffset? createdAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var created = (createdAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var username = SanitizeSegment(request.Username, "Player");
        var type = request.Type.ToString().ToUpperInvariant();
        var fileName = $"{created:yyyy-MM-dd_HHmmss}Z_{username}_{type}.zip";
        Directory.CreateDirectory(outputDirectory);
        var destination = Path.Combine(Path.GetFullPath(outputDirectory), fileName);
        var work = Path.Combine(outputDirectory, ".work-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(work);
            var includedFiles = new List<SupportManifestFile>();
            if (request.Type == SupportReportType.Log)
            {
                var logDestination = Path.Combine(work, "KSP.log");
                File.Copy(Path.GetFullPath(request.SourcePath), logDestination, true);
                includedFiles.Add(await DescribeAsync(logDestination, "KSP.log", cancellationToken));
            }
            else
            {
                var saveDestination = Path.Combine(work, "save");
                CopySaveSafely(Path.GetFullPath(request.SourcePath), saveDestination);
                foreach (var path in Directory.EnumerateFiles(saveDestination, "*", SearchOption.AllDirectories))
                    includedFiles.Add(await DescribeAsync(path, SafePaths.ManifestPath(Path.GetRelativePath(work, path)), cancellationToken));
            }

            var reportPath = Path.Combine(work, "report.txt");
            await File.WriteAllTextAsync(reportPath, BuildReport(request, created), new UTF8Encoding(false), cancellationToken);
            includedFiles.Add(await DescribeAsync(reportPath, "report.txt", cancellationToken));

            var manifestPath = Path.Combine(work, "manifest.json");
            var manifest = new SupportPackageManifest
            {
                ReportType = request.Type.ToString().ToLowerInvariant(),
                CreatedAtUtc = created,
                CampaignCode = NullIfWhiteSpace(request.CampaignCode),
                LocalSaveName = NullIfWhiteSpace(request.LocalSaveName),
                LauncherVersion = request.LauncherVersion,
                KspVersion = request.KspVersion,
                Files = includedFiles.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList()
            };
            await using (var stream = File.Create(manifestPath))
                await JsonSerializer.SerializeAsync(stream, manifest, ManifestService.JsonOptions, cancellationToken);

            if (File.Exists(destination)) throw new IOException($"A support package already exists: {destination}");
            ZipFile.CreateFromDirectory(work, destination, CompressionLevel.Optimal, false);
            var hash = await PackageService.ComputeSha256Async(destination, cancellationToken);
            return new SupportReportPackage(destination, fileName, hash, new FileInfo(destination).Length, created, request.Type, manifest.CampaignCode);
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
        finally
        {
            FileTree.DeleteDirectory(work);
        }
    }

    private static void Validate(SupportReportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length < 10)
            throw new ArgumentException("Describe the problem using at least 10 characters.");
        if (string.IsNullOrWhiteSpace(request.Username)) throw new ArgumentException("A signed-in username is required.");
        if (request.Type == SupportReportType.Log && !File.Exists(request.SourcePath))
            throw new FileNotFoundException("KSP.log was not found.", request.SourcePath);
        if (request.Type == SupportReportType.Log &&
            (File.GetAttributes(Path.GetFullPath(request.SourcePath)) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked log files cannot be uploaded.");
        if (request.Type == SupportReportType.Save)
        {
            if (!Directory.Exists(request.SourcePath)) throw new DirectoryNotFoundException("The selected save folder was not found.");
            if (!File.Exists(Path.Combine(request.SourcePath, "persistent.sfs")))
                throw new InvalidDataException("The selected folder is not a KSP save: persistent.sfs is missing.");
        }
    }

    private static void CopySaveSafely(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Save folders using links or reparse points cannot be uploaded.");
        Directory.CreateDirectory(destination);
        CopySaveDirectorySafely(source, source, destination);
    }

    private static void CopySaveDirectorySafely(string root, string current, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(current))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Linked save entry is not allowed: {entry}");
            var target = SafePaths.Under(destination, Path.GetRelativePath(root, entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopySaveDirectorySafely(root, entry, destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(entry, target, true);
        }
    }

    private static async Task<SupportManifestFile> DescribeAsync(string path, string relativePath, CancellationToken cancellationToken) =>
        new(SafePaths.ManifestPath(relativePath), new FileInfo(path).Length, await PackageService.ComputeSha256Async(path, cancellationToken));

    private static string BuildReport(SupportReportRequest request, DateTimeOffset created) => $"""
        KSR SUPPORT REPORT

        Report type: {request.Type.ToString().ToUpperInvariant()}
        Created at UTC: {created:O}
        Username: {request.Username.Trim()}
        Campaign ID: {NullIfWhiteSpace(request.CampaignCode) ?? "Not selected"}
        Campaign name: {NullIfWhiteSpace(request.CampaignName) ?? "Not selected"}
        Local save name: {NullIfWhiteSpace(request.LocalSaveName) ?? "Not selected"}
        Launcher version: {request.LauncherVersion}
        KSP version: {request.KspVersion}

        PLAYER DESCRIPTION
        {request.Description.Trim()}
        """;

    private static string SanitizeSegment(string value, string fallback)
    {
        var sanitized = Regex.Replace(value.Trim(), "[^A-Za-z0-9_-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized[..Math.Min(sanitized.Length, 48)];
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class SupportPackageManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string ReportType { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; }
        public string? CampaignCode { get; set; }
        public string? LocalSaveName { get; set; }
        public string LauncherVersion { get; set; } = "";
        public string KspVersion { get; set; } = "";
        public List<SupportManifestFile> Files { get; set; } = [];
    }

    private sealed record SupportManifestFile(string Path, long Size, string Sha256);
}

public sealed class SupportReportUploader(HttpClient? httpClient = null)
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();

    public async Task<SupportUploadResult> UploadAsync(
        string serverUrl,
        string accessToken,
        SupportReportPackage package,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A valid HTTPS KSR server URL is required.");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("Sign in before sending a support report.");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/api/v1/support/reports"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(package.Type.ToString().ToLowerInvariant()), "reportType");
        if (!string.IsNullOrWhiteSpace(package.CampaignCode)) form.Add(new StringContent(package.CampaignCode), "campaignCode");
        form.Add(new StringContent(package.Sha256), "packageSha256");
        await using var file = File.OpenRead(package.FilePath);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(content, "package", package.FileName);
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<SupportUploadResponse>(stream, ManifestService.JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The KSR server returned an empty support response.");
        return new SupportUploadResult(result.ReportId, result.Status, result.ReceivedAtUtc);
    }

    private sealed class SupportUploadResponse
    {
        public string ReportId { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTimeOffset ReceivedAtUtc { get; set; }
    }
}
