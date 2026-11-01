using System.IO.Compression;
using System.Text.Json;
using KsrLauncher.Core;

internal static class CsvSupportTests
{
    private static string Fixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "csv-support-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "GameData")); File.WriteAllText(Path.Combine(root, "KSP_x64.exe"), "fixture");
        return root;
    }
    private static string Save(string root, string name)
    {
        var folder = Path.Combine(root, "GameData", "KSRParameterLogger", "PluginData", name); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + "-parameter-events.csv"), "ut,event\n1,synthetic\n"); return folder;
    }
    private static void Check(bool value) { if (!value) throw new Exception("CSV assertion failed"); }
    public static Task Discovery()
    {
        var root = Fixture(); var other = Fixture();
        try
        {
            Check(LoggerCsvSupport.Discover(root).Count == 0);
            var a = Save(root, "Alpha"); Save(root, "Beta"); Save(other, "Foreign");
            File.WriteAllText(Path.Combine(a, "unrelated.csv"), "SECRET");
            var archive = Path.Combine(a, "Year 1"); Directory.CreateDirectory(archive);
            File.WriteAllText(Path.Combine(archive, "old-parameter-events.csv"), "ARCHIVED");
            var found = LoggerCsvSupport.Discover(root);
            Check(found.Count == 2 && found[0].SaveName == "Alpha" && found[1].SaveName == "Beta");
            Check(found[0].Files.Count == 1 && found[0].Files[0] == Path.Combine(a, "Alpha-parameter-events.csv"));
            return Task.CompletedTask;
        }
        finally { Directory.Delete(root, true); Directory.Delete(other, true); }
    }
    public static async Task Package()
    {
        var root = Fixture();
        try
        {
            var folder = Save(root, "Alpha");
            File.WriteAllText(Path.Combine(folder, "Alpha-kerbal-mission-times.csv"), "kerbal,time\nSynthetic,5\n");
            File.WriteAllText(Path.Combine(folder, "credential.txt"), "DO NOT INCLUDE");
            var before = LoggerCsvSupport.Files(root, folder).ToDictionary(path => path, File.ReadAllBytes);
            var request = new SupportReportRequest(SupportReportType.Csv, folder, "Synthetic CSV problem description", "SyntheticPlayer", null, null, "Alpha", "0.7.26", "1.12.5", LoggerKspRoot: root);
            var result = await new SupportReportPackager().CreateAsync(request, Path.Combine(root, "queue"));
            var firstHash = await PackageService.ComputeSha256Async(result.FilePath);
            var repeated = await new SupportReportPackager().CreateAsync(request, Path.Combine(root, "queue"), result.CreatedAtUtc);
            Check(repeated.FilePath != result.FilePath && await PackageService.ComputeSha256Async(result.FilePath) == firstHash);
            using var zip = ZipFile.OpenRead(result.FilePath);
            Check(zip.Entries.Select(entry => entry.FullName).Order().SequenceEqual(new[] { "csv/Alpha-kerbal-mission-times.csv", "csv/Alpha-parameter-events.csv", "manifest.json", "report.txt" }.Order()));
            using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
            Check(manifest.RootElement.GetProperty("reportType").GetString() == "csv");
            foreach (var entry in before) Check(entry.Value.SequenceEqual(File.ReadAllBytes(entry.Key)));
            Check(result.Type == SupportReportType.Csv && result.CampaignCode is null);
            using var http = new HttpClient(new CsvTransport());
            var uploaded = await new SupportReportUploader(http).UploadAsync("https://synthetic.invalid", "synthetic-token", result);
            Check(uploaded.ReportId == "SYNTHETIC-CSV");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class CsvTransport : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Headers.Authorization?.Parameter == "synthetic-token");
            Check(request.RequestUri!.AbsolutePath == "/api/v1/support/reports");
            var fields = ((MultipartFormDataContent)request.Content!).ToArray();
            Check(await fields.Single(field => field.Headers.ContentDisposition!.Name!.Trim('"') == "reportType").ReadAsStringAsync(token) == "csv");
            using var zip = new ZipArchive(new MemoryStream(await fields.Single(field => field.Headers.ContentDisposition!.Name!.Trim('"') == "package").ReadAsByteArrayAsync(token)));
            Check(zip.GetEntry("csv/Alpha-parameter-events.csv") is not null);
            return new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new StringContent("{\"reportId\":\"SYNTHETIC-CSV\",\"status\":\"received\",\"receivedAtUtc\":\"2026-11-01T00:00:00Z\"}") };
        }
    }
    public static async Task Boundaries()
    {
        var root = Fixture(); var other = Fixture();
        try
        {
            var folder = Save(root, "Alpha"); var foreign = Save(other, "Foreign");
            try { LoggerCsvSupport.Files(root, foreign); throw new Exception("Foreign CSV accepted"); } catch (InvalidDataException) { }
            var file = Path.Combine(folder, "Alpha-parameter-events.csv");
            using (var stream = File.OpenWrite(file)) stream.SetLength(LoggerCsvSupport.MaxBytes + 1);
            try { LoggerCsvSupport.Files(root, folder); throw new Exception("Oversized CSV accepted"); } catch (InvalidDataException) { }
            File.Delete(file);
            var request = new SupportReportRequest(SupportReportType.Csv, folder, "Synthetic CSV problem", "Player", null, null, "Alpha", "0.7.26", "1.12.5", LoggerKspRoot: root);
            try { await new SupportReportPackager().CreateAsync(request, Path.Combine(root, "queue")); throw new Exception("Empty CSV accepted"); } catch (FileNotFoundException) { }
            Check(!Directory.Exists(Path.Combine(root, "queue")));
        }
        finally { Directory.Delete(root, true); Directory.Delete(other, true); }
    }
}
