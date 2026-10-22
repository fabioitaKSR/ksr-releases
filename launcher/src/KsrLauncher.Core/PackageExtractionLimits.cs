namespace KsrLauncher.Core;

public sealed record PackageExtractionLimits(
    long MaxTotalBytes = 4L * 1024 * 1024 * 1024,
    long MaxFileBytes = 512L * 1024 * 1024,
    int MaxEntries = 50_000,
    long MaxArchiveBytes = 1024L * 1024 * 1024)
{
    public static PackageExtractionLimits FromEnvironment() => new(
        Read("KSR_ZIP_MAX_TOTAL_BYTES", 4L * 1024 * 1024 * 1024),
        Read("KSR_ZIP_MAX_FILE_BYTES", 512L * 1024 * 1024),
        ReadEntries(),
        Read("KSR_ZIP_MAX_ARCHIVE_BYTES", 1024L * 1024 * 1024));

    public void Validate()
    {
        if (MaxTotalBytes <= 0 || MaxFileBytes <= 0 || MaxEntries <= 0 || MaxArchiveBytes <= 0)
            throw new InvalidDataException("Package limits must be positive.");
    }

    private static int ReadEntries()
    {
        var value = Read("KSR_ZIP_MAX_ENTRIES", 50_000);
        if (value > int.MaxValue) throw new InvalidDataException("Invalid package limit: KSR_ZIP_MAX_ENTRIES.");
        return (int)value;
    }

    private static long Read(string name, long fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!long.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new InvalidDataException($"Invalid package limit: {name}.");
        return parsed;
    }
}
