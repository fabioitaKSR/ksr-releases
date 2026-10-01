using System.Text;
using System.Text.RegularExpressions;

namespace KsrLauncher.Core;

public static class GameLoggerConfiguration
{
    public static string GetPath(string kspRoot) =>
        Path.Combine(kspRoot, "GameData", IsLegacyCampaign(kspRoot) ? "KerbalSpaceRace" : "KSRLite", "PluginData", "RemoteLogger.cfg");

    private static bool IsLegacyCampaign(string kspRoot)
    {
        var path = Path.Combine(kspRoot, "GameData", "KerbalSpaceRace", "PluginData", "LegacyCampaign.cfg");
        if (!File.Exists(path)) return false;
        var content = File.ReadAllText(path);
        return Regex.IsMatch(content,
            @"(?im)^\s*enabled\s*=\s*true\s*(?://.*)?$");
    }

    public static bool Clear(string kspRoot)
    {
        var removed = false;
        foreach (var path in new[] {
            Path.Combine(kspRoot, "GameData", "KerbalSpaceRace", "PluginData", "RemoteLogger.cfg"),
            Path.Combine(kspRoot, "GameData", "KSRLite", "PluginData", "RemoteLogger.cfg") })
        {
            if (!File.Exists(path)) continue;
            File.Delete(path);
            removed = true;
        }
        return removed;
    }

    public static string Write(string kspRoot, string serverUrl, string campaignCode, string gameTicket)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The game logger requires an HTTPS KSR server URL.");
        if (string.IsNullOrWhiteSpace(campaignCode) || string.IsNullOrWhiteSpace(gameTicket))
            throw new InvalidOperationException("A campaign-scoped game ticket is required.");

        static string Safe(string value) => value.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        var path = GetPath(kspRoot);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
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

    public static string GetTicketsPath(string kspRoot) =>
        Path.Combine(kspRoot, "GameData", "KSRLite", "PluginData", "GameTickets.cfg");

    public static void ClearTickets(string kspRoot)
    {
        var path = GetTicketsPath(kspRoot);
        if (File.Exists(path)) File.Delete(path);
    }

    public static string WriteTickets(string kspRoot, string serverUrl, long userId, IEnumerable<KsrGameTicket> tickets,
        IReadOnlyDictionary<string, string>? campaignSaveFolders = null)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Game tickets require an HTTPS KSR server URL.");
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        var entries = tickets.ToList();
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ticket in entries)
        {
            if (!Regex.IsMatch(ticket.CampaignCode, "^[A-Za-z0-9_-]+$") ||
                !Regex.IsMatch(ticket.Token, "^[A-Za-z0-9_-]+$") || !codes.Add(ticket.CampaignCode))
                throw new InvalidDataException("A game ticket contains an invalid or duplicate campaign code.");
        }

        var lines = new List<string> {
            "KSR_GAME_TICKETS", "{", "    schemaVersion = 1", $"    userId = {userId}",
            $"    serverUrl = {server.GetLeftPart(UriPartial.Authority)}",
            $"    serverScheme = {server.Scheme}",
            $"    serverHost = {server.Host}",
            $"    serverPort = {server.Port}"
        };
        foreach (var ticket in entries)
        {
            lines.AddRange(new[] {
                "    TICKET", "    {", $"        campaignId = {ticket.CampaignCode}",
                $"        token = {ticket.Token}"
            });
            if (campaignSaveFolders is not null && campaignSaveFolders.TryGetValue(ticket.CampaignCode, out var saveFolder))
            {
                var folderName = Path.GetFileName(saveFolder);
                if (!string.Equals(folderName, saveFolder, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(folderName))
                    throw new InvalidDataException("A campaign save folder must be a single folder name.");
                lines.Add($"        saveFolderBase64 = {Convert.ToBase64String(Encoding.UTF8.GetBytes(folderName))}");
            }
            lines.Add("    }");
        }
        lines.Add("}");
        var path = GetTicketsPath(kspRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllLines(temporaryPath, lines, new UTF8Encoding(false));
        File.Move(temporaryPath, path, true);
        return path;
    }
}
