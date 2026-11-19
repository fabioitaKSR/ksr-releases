using System.IO;
using System.Windows;
using KsrLauncher.Core;

namespace KsrLauncher.App;

internal static class LauncherCache
{
    public const string ConfirmationText = "Confirm the cache folder in Settings before downloading updates.";
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KSRLauncher");

    public static string? SelectedFolder => LauncherSettingsStore.LoadCacheFolder();
    public static bool IsConfirmed => SelectedFolder is { } folder &&
        (folder.Equals(DefaultFolder, StringComparison.OrdinalIgnoreCase) || Directory.Exists(folder));

    private static FileStream AcquireSettingsLock()
    {
        try { return LauncherStorageCleanup.AcquireUpdateLock(Path.Combine(DefaultFolder, "cache-settings")); }
        catch (IOException exception) when (!CacheDiskSpace.IsDiskFull(exception))
        {
            throw new IOException("The download cache is busy or unavailable. Try again after active updates finish.", exception);
        }
    }

    public static void Select(string folder)
    {
        using var settingsLock = AcquireSettingsLock();
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var cache = new LauncherDownloadCache(DefaultFolder, normalized);
        using var cacheLock = cache.AcquireLock();
        cache.VerifyWritable();
        LauncherSettingsStore.SaveCacheFolder(normalized);
        LauncherDiagnosticLog.Write("Cache folder confirmed");
    }

    public static DownloadSession BeginDownload()
    {
        FileStream? settingsLock = null;
        FileStream? cacheLock = null;
        try
        {
            settingsLock = AcquireSettingsLock();
            var folder = SelectedFolder ?? throw new IOException(ConfirmationText);
            var cache = new LauncherDownloadCache(DefaultFolder, folder);
            cacheLock = cache.AcquireLock();
            cache.VerifyWritable(probeWrite: false);
            return new DownloadSession(cache, settingsLock, cacheLock);
        }
        catch (Exception exception)
        {
            cacheLock?.Dispose();
            settingsLock?.Dispose();
            if (exception is IOException io && CacheDiskSpace.IsDiskFull(io))
                throw new CacheSpaceException("There is no space available to start the download. Free up space or choose another cache folder in Settings.", inner: io);
            throw;
        }
    }

    public static StorageCleanupResult CleanStaleDownloads()
    {
        using var session = BeginDownload();
        return session.Cache.Clean(typeof(LauncherCache).Assembly.GetName().Version!, Environment.ProcessPath!, clearAll: false);
    }

    public static StorageCleanupResult ClearManually(IProgress<StorageCleanupProgress> progress)
    {
        using var settingsLock = AcquireSettingsLock();
        return LauncherStorageCleanup.ClearUpdaterStorage(DefaultFolder, SelectedFolder,
            typeof(LauncherCache).Assembly.GetName().Version!, Environment.ProcessPath!, progress);
    }

    public static void ShowSpaceWarning(Window owner, CacheSpaceException exception) =>
        MessageBox.Show(owner, exception.Message, exception.DiskFull ? "Download Stopped - Disk Full" : "Download Cache Space",
            MessageBoxButton.OK, MessageBoxImage.Warning);

    internal sealed class DownloadSession(LauncherDownloadCache cache, FileStream settingsLock, FileStream cacheLock) : IDisposable
    {
        public LauncherDownloadCache Cache { get; } = cache;
        private readonly CacheDiskSpace _space = new(cache.Root);

        public Task<UpdateResult> RunUpdateAsync(ReleaseManifest manifest, LauncherLocations locations, string assetsBase,
            UpdatePolicy policy, IProgress<UpdateProgress>? progress, PackageService? packages = null, CancellationToken token = default)
        {
            var engine = new UpdateEngine(packages, InstallationWarningDialog.Review, Cache.WorkRoot, _space);
            return RunAsync(() => engine.RunAsync(manifest, locations, assetsBase, true, policy, progress, token),
                () => progress?.Report(new UpdateProgress("Download cache", 0, 0, 0, 0, "CACHE CLEANUP")));
        }

        public Task<string> DownloadLauncherAsync(LauncherUpdateService service, LauncherUpdateRelease update, IProgress<long> progress, Action cleaning) =>
            RunAsync(async () =>
            {
                await Task.Run(() => _space.Require(update.Size));
                return await service.DownloadAsync(update, Cache.GetUpdateDirectory(update.Tag), progress);
            }, cleaning, launcherDownload: true);

        private async Task<T> RunAsync<T>(Func<Task<T>> operation, Action cleaning, bool launcherDownload = false)
        {
            try { return await operation(); }
            catch (IOException exception) when (exception is CacheSpaceException { DiskFull: true } ||
                (launcherDownload && CacheDiskSpace.IsDiskFull(exception)))
            {
                cleaning();
                LauncherDiagnosticLog.Write("Download cache drive full. Cleaning temporary update files", exception);
                string cleanup;
                try
                {
                    var result = await Task.Run(() => Cache.Clean(
                        typeof(LauncherCache).Assembly.GetName().Version!, Environment.ProcessPath!, clearAll: true));
                    cleanup = result.Retained.Count == 0
                        ? "Temporary files were cleared from the download cache."
                        : "Temporary files were cleared where possible. Some cache files were retained.";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    LauncherDiagnosticLog.Write("Download cache cleanup failed", error);
                    cleanup = "The download cache could not be fully cleared.";
                }
                throw new CacheSpaceException(
                    $"The download cache drive ran out of space. {cleanup} Free up space or choose another folder in Settings before retrying.\n\nCache folder: {Cache.Root}",
                    diskFull: true, inner: exception);
            }
        }

        public void Dispose()
        {
            cacheLock.Dispose();
            settingsLock.Dispose();
        }
    }
}
