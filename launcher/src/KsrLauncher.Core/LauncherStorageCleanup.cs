using System.Text.Json;
using System.Text.RegularExpressions;

namespace KsrLauncher.Core;

public sealed record StorageCleanupProgress(string Description, int Completed, int Total);

public sealed record StorageCleanupResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Retained);

public static class LauncherStorageCleanup
{
    public static FileStream AcquireUpdateLock(string dataRoot)
    {
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
        var path = Path.Combine(Path.GetTempPath(), "KSRLauncherLocks", identity + ".lock");
        SafePaths.RejectReparsePoints(Path.GetPathRoot(path)!, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    public static async Task<StorageCleanupResult> CleanApplicationAsync(string dataRoot, Version runningVersion, string executable, IProgress<StorageCleanupProgress>? progress = null)
    {
        return await Task.Run(async () =>
        {
            var deleted = new List<string>();
            var retained = new List<string>();
            var roots = DataRoots(dataRoot).ToArray();
            var total = roots.Sum(root => CountBackupFolders(root)) + CountDownloadFolders(dataRoot);
            var completed = 0;
            var backupCounts = roots.ToDictionary(root => root, CountBackupFolders);
            foreach (var root in roots)
            {
                try
                {
                    using var updateLock = AcquireUpdateLock(root);
                    var result = await CleanCompletedUpdatesAsync(root, progress, completed, total);
                    deleted.AddRange(result.Deleted);
                    retained.AddRange(result.Retained);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                { retained.Add(root); }
                completed += backupCounts[root];
            }
            try
            {
                using var updateLock = AcquireUpdateLock(dataRoot);
                CleanLauncherDownloads(dataRoot, runningVersion, executable, deleted, retained, progress, completed, total);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { retained.Add(Path.Combine(dataRoot, "Updates")); }
            progress?.Report(new("Cleanup complete", total, total));
            return new StorageCleanupResult(deleted, retained);
        });
    }

    internal static async Task<StorageCleanupResult> CleanCompletedUpdatesAsync(string dataRoot, IProgress<StorageCleanupProgress>? progress = null,
        int completed = 0, int total = 0)
    {
        var deleted = new List<string>();
        var retained = new List<string>();
        var backups = SafePaths.Under(dataRoot, ".ksr-launcher/backups");
        if (!Directory.Exists(backups)) return new(deleted, retained);
        SafePaths.RejectReparsePoints(dataRoot, backups);
        var state = await StateStore.LoadAsync(dataRoot);
        if (total == 0) total = Directory.EnumerateDirectories(backups).Count();
        foreach (var backup in Directory.EnumerateDirectories(backups))
        {
            progress?.Report(new("Removing completed mod and beta update backups", completed, total));
            completed++;
            try
            {
                SafePaths.RejectReparsePoints(backups, backup);
                if (!Directory.EnumerateFileSystemEntries(backup).Any())
                {
                    Directory.Delete(backup);
                    deleted.Add(backup);
                    continue;
                }
                var journalFile = SafePaths.Under(backup, "backup-manifest.json");
                var approval = SafePaths.Under(dataRoot, Path.Combine(".ksr-launcher", "cleanup", Path.GetFileName(backup) + ".json"));
                SafePaths.RejectReparsePoints(backup, journalFile);
                SafePaths.RejectReparsePoints(dataRoot, approval);
                var proof = File.Exists(journalFile) ? journalFile : approval;
                if (!File.Exists(proof)) { retained.Add(backup); continue; }
                var journal = JsonSerializer.Deserialize<BackupJournal>(await File.ReadAllTextAsync(proof), ManifestService.JsonOptions);
                if (journal?.InProgress == true) { retained.Add(backup); continue; }
                if (journal is null || journal.SchemaVersion != 1 || journal.CreatedAtUtc == default
                    || state.UpdatedAtUtc < journal.CreatedAtUtc
                    || !string.Equals(Path.GetFullPath(journal.LauncherDataRoot).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                { retained.Add(backup); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(approval)!);
                var temporary = approval + ".tmp";
                SafePaths.RejectReparsePoints(dataRoot, temporary);
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(journal, ManifestService.JsonOptions));
                File.Move(temporary, approval, true);
                var work = SafePaths.Under(dataRoot, Path.Combine(".ksr-launcher", "work", Path.GetFileName(backup)));
                DeleteCacheTree(dataRoot, work);
                DeleteCacheTree(dataRoot, backup);
                File.Delete(approval);
                deleted.Add(backup);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
            { retained.Add(backup); }
        }
        return new(deleted, retained);
    }

    private static IEnumerable<string> DataRoots(string dataRoot)
    {
        yield return Path.GetFullPath(dataRoot);
        var tests = SafePaths.Under(dataRoot, "test-installations");
        if (Directory.Exists(tests))
        {
            SafePaths.RejectReparsePoints(dataRoot, tests);
            foreach (var root in Directory.EnumerateDirectories(tests))
                if (Regex.IsMatch(Path.GetFileName(root), "^[A-Fa-f0-9]{64}$")) yield return root;
        }
        var betas = SafePaths.Under(dataRoot, "beta");
        if (!Directory.Exists(betas)) yield break;
        SafePaths.RejectReparsePoints(dataRoot, betas);
        foreach (var module in Directory.EnumerateDirectories(betas))
        {
            if ((File.GetAttributes(module) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var root in Directory.EnumerateDirectories(module))
                if (Regex.IsMatch(Path.GetFileName(root), "^[A-Fa-f0-9]{16}$")) yield return root;
        }
    }

    private static void CleanLauncherDownloads(string dataRoot, Version runningVersion, string executable,
        List<string> deleted, List<string> retained, IProgress<StorageCleanupProgress>? progress, int completed, int total)
    {
        var updates = SafePaths.Under(dataRoot, "Updates");
        if (!Directory.Exists(updates)) return;
        SafePaths.RejectReparsePoints(dataRoot, updates);
        foreach (var folder in Directory.EnumerateDirectories(updates))
        {
            progress?.Report(new("Removing old launcher downloads", completed, total));
            completed++;
            var tag = Path.GetFileName(folder);
            if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version >= runningVersion) continue;
            try
            {
                SafePaths.RejectReparsePoints(updates, folder);
                var files = Directory.EnumerateFiles(folder).ToArray();
                if (Directory.EnumerateDirectories(folder).Any()
                    || files.Any(file => string.Equals(Path.GetFullPath(file), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
                        || !IsLauncherCacheFile(Path.GetFileName(file), tag)))
                { retained.Add(folder); continue; }
                DeleteCacheTree(dataRoot, folder);
                deleted.Add(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { retained.Add(folder); }
        }
    }

    private static int CountBackupFolders(string root) => CountFolders(root, ".ksr-launcher/backups");
    private static int CountDownloadFolders(string root) => CountFolders(root, "Updates");
    private static int CountFolders(string root, string relative)
    {
        var folder = SafePaths.Under(root, relative);
        if (!Directory.Exists(folder)) return 0;
        SafePaths.RejectReparsePoints(root, folder);
        return Directory.EnumerateDirectories(folder).Count();
    }

    private static bool IsLauncherCacheFile(string name, string tag) =>
        name.Equals("apply-launcher-update.ps1", StringComparison.OrdinalIgnoreCase)
        || name.Equals($"KSR-Launcher-{tag}-win-x64.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals($"KSR-Launcher-{tag}-win-x64.exe.part", StringComparison.OrdinalIgnoreCase);

    internal static void DeleteCacheTree(string root, string target)
    {
        var checkedTarget = SafePaths.Under(root, Path.GetRelativePath(root, target));
        SafePaths.RejectReparsePoints(root, checkedTarget);
        FileTree.DeleteDirectory(checkedTarget);
    }
}
