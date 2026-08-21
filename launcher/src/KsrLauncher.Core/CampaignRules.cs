namespace KsrLauncher.Core;

public static class CampaignRules
{
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLOSED",
        "COMPLETED",
        "CANCELLED",
        "ARCHIVED",
        "ENDED"
    };

    public static bool BlocksNewAdminCampaign(string? role, string? status) =>
        string.Equals(role?.Trim(), "ADMIN", StringComparison.OrdinalIgnoreCase) &&
        !IsTerminalStatus(status);

    public static bool IsTerminalStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) && TerminalStatuses.Contains(status.Trim());
}
