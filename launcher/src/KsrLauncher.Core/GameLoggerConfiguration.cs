using System.Text;

namespace KsrLauncher.Core;

public static class GameLoggerConfiguration
{
    public static string Write(string kspRoot, string serverUrl, string campaignCode, string gameTicket)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The game logger requires an HTTPS KSR server URL.");
        if (string.IsNullOrWhiteSpace(campaignCode) || string.IsNullOrWhiteSpace(gameTicket))
            throw new InvalidOperationException("A campaign-scoped game ticket is required.");

        static string Safe(string value) => value.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        var directory = Path.Combine(kspRoot, "GameData", "KerbalSpaceRace", "PluginData");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "RemoteLogger.cfg");
        var content = string.Join(Environment.NewLine,
            "RemoteLogger",
            "{",
            "    enabled = true",
            $"    serverUrl = {Safe(server.GetLeftPart(UriPartial.Authority))}",
            $"    serverScheme = {server.Scheme}",
            $"    serverHost = {server.Host}",
            $"    serverPort = {server.Port}",
            $"    campaignId = {Safe(campaignCode)}",
            $"    saveNameFallback = {Safe(CampaignSaveNaming.CreateStartFolderName(campaignCode))}",
            $"    token = {Safe(gameTicket)}",
            "    downloadIntervalSeconds = 120",
            "    maxRowsQueuedPerScan = 500",
            "    timeoutMilliseconds = 15000",
            $"    loggerRoot = {Safe(Path.Combine(kspRoot, "saves").Replace('\\', '/'))}",
            "}", string.Empty);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }
}
