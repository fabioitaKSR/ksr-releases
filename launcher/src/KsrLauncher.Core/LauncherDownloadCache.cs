using System.Text.RegularExpressions;

namespace KsrLauncher.Core;

public sealed class LauncherDownloadCache
{
    private static readonly Regex TransactionName = new(@"^\d{8}-\d{6}-[a-f0-9]{8}$");
    private readonly string _root;
    private readonly bool _isDefault;

    public LauncherDownloadCache(string dataRoot, string selectedFolder)
    {
        var defaultFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedFolder));
        _isDefault = selected.Equals(defaultFolder, StringComparison.OrdinalIgnoreCase);
        _root = _isDefault
            ? defaultFolder
            : SafePaths.Under(selected, "KSRLauncherCache");
    }

    public string Root => _root;
    public string WorkRoot => SafePaths.Under(_root, "UpdateCacheWork");
    public string UpdatesRoot => SafePaths.Under(_root, "Updates");
    public FileStream AcquireLock() => LauncherStorageCleanup.AcquireUpdateLock(Path.Combine(_root, "download-cache"));

    public void VerifyWritable(bool probeWrite = true)
    {
        CheckPath(_root);
        if (!_isDefault && !Directory.Exists(Path.GetDirectoryName(_root)))
            throw new IOException("The selected cache folder is unavailable. Choose an available folder in Settings.");

        // Claim only an empty, dedicated cache directory.
        var marker = SafePaths.Under(_root, ".ksr-download-cache");
        if (!_isDefault)
        {
            CheckPath(marker);
            if (!File.Exists(marker))
            {
                if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
                    throw new IOException("KSRLauncherCache already exists and contains unrelated files. Choose another folder.");
                Directory.CreateDirectory(_root);
                using var ownership = new FileStream(marker, FileMode.CreateNew, FileAccess.Write);
            }
        }
        Directory.CreateDirectory(_root);
        CheckPath(WorkRoot);
        CheckPath(UpdatesRoot);
        if (!probeWrite) return;
        using var probe = new FileStream(SafePaths.Under(_root, Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    public string GetUpdateDirectory(string tag)
    {
        var folder = SafePaths.Under(UpdatesRoot, tag);
        CheckPath(folder);
        if (Directory.Exists(folder))
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder)) CheckPath(entry);
        return folder;
    }

    public StorageCleanupResult Clean(Version runningVersion, string executable, bool clearAll)
    {
        CheckPath(UpdatesRoot);
        var deleted = new List<string>();
        var retained = new List<string>();
        LauncherStorageCleanup.CleanLauncherDownloads(_root, runningVersion, executable, deleted, retained,
            null, 0, 0, clearAll);
        if (Directory.Exists(WorkRoot))
        {
            CheckPath(WorkRoot);
            foreach (var folder in Directory.EnumerateDirectories(WorkRoot))
            {
                if (!TransactionName.IsMatch(Path.GetFileName(folder))) continue;
                if (Path.GetFullPath(executable).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    retained.Add(folder);
                    continue;
                }
                try
                {
                    LauncherStorageCleanup.DeleteCacheTree(WorkRoot, folder);
                    deleted.Add(folder);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    retained.Add(folder);
                }
            }
        }
        return new(deleted, retained);
    }

    private static void CheckPath(string path)
    {
        try { SafePaths.RejectReparsePoints(Path.GetPathRoot(path)!, path); }
        catch (IOException exception) { throw new IOException("The download cache cannot use linked folders or files.", exception); }
    }
}
