namespace KsrLauncher.Core;

public static class CampaignSaveNaming
{
    public const string StartPrefix = "KSRstart";

    public static string CreateStartFolderName(string? campaignName)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string((campaignName ?? string.Empty)
            .Trim()
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '-' : character)
            .ToArray())
            .Trim(' ', '.', '-');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Campaign";
        return $"{StartPrefix}-{cleaned}";
    }

    public static bool IsStartFolder(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return false;
        return folderName.Equals(StartPrefix, StringComparison.OrdinalIgnoreCase) ||
               folderName.StartsWith(StartPrefix + "-", StringComparison.OrdinalIgnoreCase);
    }
}
