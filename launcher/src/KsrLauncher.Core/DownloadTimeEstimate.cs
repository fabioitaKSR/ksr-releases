namespace KsrLauncher.Core;

public static class DownloadTimeEstimate
{
    public static TimeSpan? Remaining(long downloadedBytes, long totalBytes, TimeSpan elapsed)
    {
        if (totalBytes <= 0 || downloadedBytes <= 0 || elapsed.TotalSeconds < 1)
            return null;
        if (downloadedBytes >= totalBytes) return TimeSpan.Zero;
        var seconds = (totalBytes - downloadedBytes) * elapsed.TotalSeconds / downloadedBytes;
        return double.IsFinite(seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(1).TotalSeconds))
            : null;
    }
}
