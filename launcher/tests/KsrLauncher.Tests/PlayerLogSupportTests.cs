using System.IO.Compression;
using KsrLauncher.Core;

internal static class PlayerLogSupportTests
{
    public static async Task PackageAndGuards()
    {
        if (!PlayerLogSupport.IsAllowed("HansLanda") || !PlayerLogSupport.IsAllowed("ALE202") ||
            !PlayerLogSupport.IsAllowed("FABIOITA") || PlayerLogSupport.IsAllowed(null) || PlayerLogSupport.IsAllowed("landa"))
            throw new Exception("Player.log account gate failed.");
        var root = Path.Combine(Path.GetTempPath(), "player-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var player = Path.Combine(root, "Player.log");
            var log = Path.Combine(root, "KSP.log");
            await File.WriteAllTextAsync(player, "Synthetic Unity crash diagnostic");
            await File.WriteAllTextAsync(log, "Synthetic KSP log");
            var request = new SupportReportRequest(SupportReportType.Log, log, "Synthetic crash problem description", "HansLanda", null, null, null, "0.7.34", "1.12.5", PlayerLogPath: player);
            var result = await new SupportReportPackager().CreateAsync(request, Path.Combine(root, "queue"));
            using (var zip = ZipFile.OpenRead(result.FilePath))
            {
                var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                if (!names.SequenceEqual(new[] { "KSP.log", "Player.log", "manifest.json", "report.txt" })) throw new Exception("Unexpected support attachments.");
                using var reader = new StreamReader(zip.GetEntry("Player.log")!.Open());
                if (await reader.ReadToEndAsync() != await File.ReadAllTextAsync(player)) throw new Exception("Player.log snapshot mismatch.");
            }
            // The existing log report wire format stays compatible with the deployed server.
            if (result.Type != SupportReportType.Log || await File.ReadAllTextAsync(log) != "Synthetic KSP log") throw new Exception("Log source changed.");
            await Reject(request with { Username = "Other" }, root);
            await Reject(request with { Type = SupportReportType.Save }, root);
            await Reject(request with { PlayerLogPath = log }, root);
            await File.WriteAllTextAsync(player, "");
            await Reject(request, root);
            using (var oversized = new FileStream(player, FileMode.Create)) oversized.SetLength(PlayerLogSupport.MaximumBytes + 1);
            await Reject(request, root);
            File.Delete(player);
            await Reject(request, root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Reject(SupportReportRequest request, string root)
    {
        try { await new SupportReportPackager().CreateAsync(request, Path.Combine(root, "rejected")); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
        throw new Exception("Invalid Player.log request accepted.");
    }
}


