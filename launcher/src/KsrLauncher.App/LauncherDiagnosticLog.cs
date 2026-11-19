using System.IO;

namespace KsrLauncher.App;

internal static class LauncherDiagnosticLog
{
    private const long MaxLength = 256 * 1024;
    public static string FilePath => Path.Combine(LauncherCache.DefaultFolder, "launcher.log");

    public static void Write(string stage, Exception? error = null)
    {
        // Only controlled phase names and exception types are recorded. Exception messages can contain private URLs or paths.
        try
        {
            using var logLock = KsrLauncher.Core.LauncherStorageCleanup.AcquireUpdateLock(FilePath);
            Directory.CreateDirectory(LauncherCache.DefaultFolder);
            var log = new FileInfo(FilePath);
            if (log.Exists && log.Length >= MaxLength) File.Move(FilePath, FilePath + ".previous", true);
            var failure = error is null ? string.Empty : $" | {error.GetType().Name} (0x{error.HResult:X8})";
            File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} | {stage}{failure}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
