using System.Text.RegularExpressions;
using KerbalSpaceRace.LoggingPrivacy;

namespace KsrLauncher.Core;

public static class GameLoggingPrivacy
{
    public static bool IsLegacyInstallation(string root)
    {
        var path = Path.Combine(root,"GameData","KerbalSpaceRace","PluginData","LegacyCampaign.cfg");
        return File.Exists(path) && Regex.IsMatch(File.ReadAllText(path),@"(?im)^\s*enabled\s*=\s*true\s*(?://.*)?$");
    }
    public static bool IsStopped(string root, string code) =>
        !string.Equals(code,"test1",StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(code, "^[A-Za-z0-9_-]{1,80}$") &&
        File.Exists(Path.Combine(root, "GameData", "KSRLite", "PluginData", "LoggingPrivacy", code.ToLowerInvariant() + ".stop"));

    public static bool IsExitConfirmed(string root,string code,long userId)
    {
        if (!IsStopped(root,code)) return false;
        var prefix=Path.Combine(root,"GameData","KSRLite","PluginData","LoggingPrivacy",code.ToLowerInvariant());
        try
        {
            var fields=File.ReadAllText(prefix+".ack").Trim().Split('\n');
            return fields.Length==2 && fields[0]==File.ReadAllText(prefix+".stop").Trim() && long.TryParse(fields[1],out var owner) && owner==userId;
        }
        catch(IOException) { return false; }
        catch(UnauthorizedAccessException) { return false; }
    }

    public static void Stop(IEnumerable<string> roots, string code, long userId, string serverUrl)
    {
        var targets = roots.Where(root => !IsLegacyInstallation(root)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("Select a normal campaign installation. test1 is unchanged.");
        if (targets.Any(root => !File.Exists(Path.Combine(root,"GameData","KSRLite","Plugins","KerbalSpaceRace.LoggingPrivacy.dll"))))
            throw new InvalidOperationException("Install the Logging Privacy component and restart KSP before using this control.");
        foreach (var root in targets)
        {
            var token = ReadOwnTicket(root, code, userId);
            using var store = new LoggingPrivacyStore(root);
            store.Stop(code, userId, serverUrl, token);
        }
    }

    public static async Task<int> DeliverPendingAsync(IEnumerable<string> roots, long userId, string? accessToken)
    {
        var paths = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return await Task.Run(() =>
        {
            var pending = 0;
            foreach (var root in paths)
            {
                if (IsLegacyInstallation(root)) continue;
                if (!Directory.Exists(Path.Combine(root, "GameData", "KSRLite", "PluginData", "LoggingPrivacy"))) continue;
                using var store = new LoggingPrivacyStore(root);
                foreach (var code in store.PendingCodes().Take(10))
                {
                    try { if (!store.Deliver(code, userId, accessToken!)) pending++; }
                    catch { pending++; }
                }
                if (store.HasPending && pending == 0) pending = 1;
            }
            return pending;
        });
    }

    private static string ReadOwnTicket(string root, string code, long userId)
    {
        var path = GameLoggerConfiguration.GetTicketsPath(root);
        if (!File.Exists(path) || new FileInfo(path).Length > 65536) return "";
        var content = File.ReadAllText(path);
        var owner = Regex.Match(content, @"(?im)^\s*userId\s*=\s*(\d+)\s*$");
        if (!owner.Success || !long.TryParse(owner.Groups[1].Value, out var id) || id != userId) return "";
        foreach (Match block in Regex.Matches(content, @"TICKET\s*\{([^{}]*)\}"))
        {
            var campaign = Regex.Match(block.Groups[1].Value, @"(?im)^\s*campaignId\s*=\s*([^\r\n]+)");
            if (!campaign.Success || !string.Equals(campaign.Groups[1].Value.Trim(), code, StringComparison.OrdinalIgnoreCase)) continue;
            var token = Regex.Match(block.Groups[1].Value, @"(?im)^\s*token\s*=\s*([A-Za-z0-9_-]+)\s*$");
            return token.Success ? token.Groups[1].Value : "";
        }
        return "";
    }
}
