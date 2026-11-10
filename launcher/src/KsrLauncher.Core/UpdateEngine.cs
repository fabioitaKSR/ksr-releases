using System.Text.Json;

namespace KsrLauncher.Core;

public sealed class UpdateEngine(PackageService? packageService = null,
    Func<LauncherLocations, IReadOnlyList<string>, bool>? reviewInstallation = null)
{
    private readonly PackageService _packages = packageService ?? new PackageService();

    public async Task<UpdateResult> RunAsync(
        ReleaseManifest manifest,
        LauncherLocations locations,
        string assetsBase,
        bool apply,
        UpdatePolicy policy = UpdatePolicy.ExistingOnly,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ManifestService.Validate(manifest);
        if (manifest.Components.Any(component => !string.Equals(component.TargetKind, "launcherData", StringComparison.OrdinalIgnoreCase)))
            UpdatePlanner.ValidateKspRoot(locations.KspRoot);

        using var updateLock = apply ? LauncherStorageCleanup.AcquireUpdateLock(locations.LauncherDataRoot) : null;
        if (apply) await Task.Run(() => RecoverInterruptedAsync(locations, progress, cancellationToken), cancellationToken);
        var state = await StateStore.LoadAsync(locations.LauncherDataRoot, cancellationToken);
        if (Version.TryParse(manifest.Version, out var incomingVersion) && manifest.Components.Any(component =>
            state.Components.TryGetValue(component.Id, out var installed) && Version.TryParse(installed.Version, out var previousVersion) && previousVersion > incomingVersion))
            throw new InvalidDataException("An older component catalog cannot replace an installed newer catalog.");
        progress?.Report(new UpdateProgress("Installed components", 0, 0, 0, manifest.Components.Count, "CHECKING"));
        var plan = await Task.Run(() => UpdatePlanner.Create(manifest, state, locations, policy), cancellationToken);
        if (apply && reviewInstallation is not null)
        {
            var warnings = await Task.Run(() => InstallationWarnings.Inspect(plan, state), cancellationToken);
            if (warnings.Count > 0 && !reviewInstallation(locations, warnings))
                throw new IOException("Installation cancelled. No files were changed by this operation. Review the existing mods and try again when ready.");
        }
        if (!apply || !plan.HasUpdates)
        {
            if (apply) await CleanCompletedCacheAsync(locations.LauncherDataRoot, progress);
            return new UpdateResult(plan, false, null);
        }
        if (string.IsNullOrWhiteSpace(assetsBase)) throw new ArgumentException("assetsBase e obbligatorio per applicare un aggiornamento.");
        Directory.CreateDirectory(locations.LauncherDataRoot);

        var launcherRoot = Path.Combine(locations.LauncherDataRoot, ".ksr-launcher");
        var transactionId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var workRoot = Path.Combine(launcherRoot, "work", transactionId);
        var backupRoot = Path.Combine(launcherRoot, "backups", transactionId);
        var journal = new BackupJournal
        {
            ReleaseVersion = manifest.Version,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            KspRoot = Path.GetFullPath(locations.KspRoot),
            LauncherDataRoot = Path.GetFullPath(locations.LauncherDataRoot),
            InProgress = true
        };

        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(backupRoot);
        UpdateResult result;
        try
        {
            var prepared = await PrepareAsync(plan, assetsBase, workRoot, policy, progress, cancellationToken);
            if (reviewInstallation is not null)
            {
                var overlayWarnings = await Task.Run(() => prepared.SelectMany(item =>
                    InstallationWarnings.InspectOverlayFiles(item.Plan, state, item.ExpectedFiles)).ToArray(), cancellationToken);
                if (overlayWarnings.Length > 0 && !reviewInstallation(locations, overlayWarnings))
                    throw new IOException("Installation cancelled. No files were changed by this operation. Review the existing mods and try again when ready.");
            }
            await Task.Run(() =>
            {
                var statePath = StateStore.GetStatePath(locations.LauncherDataRoot);
                if (File.Exists(statePath)) File.Copy(statePath, Path.Combine(backupRoot, "installed-state.previous.json"), true);

                WriteJournalAsync(backupRoot, journal, cancellationToken).GetAwaiter().GetResult();
                foreach (var group in prepared.GroupBy(item => item.Plan.Component.TransactionGroup))
                {
                    foreach (var item in group)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var installIndex = prepared.IndexOf(item);
                        progress?.Report(new UpdateProgress(
                            item.Plan.Component.Id, 0, 0, installIndex, prepared.Count, "INSTALLING"));
                        InstallComponent(item, manifest, locations, backupRoot, journal, cancellationToken);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
            }, cancellationToken);

            state.Product = manifest.Product;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            foreach (var item in plan.Components.Where(item => item.NeedsUpdate))
            {
                var installedFiles = prepared.First(preparedItem => preparedItem.Plan == item).ExpectedFiles;
                state.Components[item.Component.Id] = new InstalledComponent
                {
                    Version = manifest.Version,
                    Sha256 = item.Component.Sha256.ToLowerInvariant(),
                    TargetKind = item.Component.TargetKind,
                    Target = item.Component.Target,
                    ExpectedFiles = installedFiles
                };
            }
            await StateStore.SaveAsync(locations.LauncherDataRoot, state, cancellationToken);
            journal.InProgress = false;
            await Task.Run(() => WriteJournalAsync(backupRoot, journal, cancellationToken), cancellationToken);
            result = new UpdateResult(plan, true, backupRoot);
        }
        catch
        {
            // Se la preparazione fallisce prima della prima sostituzione, l'installazione
            // e lo stato esistente non sono stati toccati e non devono essere ripristinati.
            if (journal.Entries.Count > 0 || journal.StagingDirectories.Count > 0)
            {
                progress?.Report(new UpdateProgress("Previous installation", 0, 0, 0, journal.Entries.Count, "RESTORING"));
                await Task.Run(() => RollbackEntriesAsync(journal, locations, backupRoot, CancellationToken.None));
                journal.InProgress = false;
                await WriteJournalAsync(backupRoot, journal, CancellationToken.None);
            }
            throw;
        }
        finally
        {
            try { await Task.Run(() => FileTree.DeleteDirectory(workRoot)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        await CleanCompletedCacheAsync(locations.LauncherDataRoot, progress);
        return Directory.Exists(backupRoot) ? result : result with { BackupDirectory = null };
    }

    private static async Task CleanCompletedCacheAsync(string root, IProgress<UpdateProgress>? progress)
    {
        var cleanupProgress = new Progress<StorageCleanupProgress>(item => progress?.Report(
            new UpdateProgress("Completed update backups", 0, 0, item.Completed, item.Total, "CLEANING")));
        try { await Task.Run(() => LauncherStorageCleanup.CleanCompletedUpdatesAsync(root, cleanupProgress)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
    }

    private async Task<List<PreparedComponent>> PrepareAsync(
        UpdatePlan plan,
        string assetsBase,
        string workRoot,
        UpdatePolicy policy,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new List<PreparedComponent>();
        var pending = plan.Components.Where(item => item.NeedsUpdate).ToList();
        var selective = policy == UpdatePolicy.ReinstallAll
            ? new Dictionary<ComponentPlan, SelectedFiles>()
            : await Task.Run(() => SelectFilesAsync(pending, assetsBase, progress, cancellationToken), cancellationToken);
        var totalBytes = pending.Sum(item => selective.TryGetValue(item, out var selection)
            ? selection.Changed.Sum(file => file.CompressedSize) : Math.Max(0, item.Component.Size));
        long completedBytes = 0;
        for (var index = 0; index < pending.Count; index++)
        {
            var item = pending[index];
            var currentIndex = index;
            var componentStart = completedBytes;
            var componentProgress = new Progress<long>(bytes => progress?.Report(new UpdateProgress(
                item.Component.Id,
                Math.Min(totalBytes, componentStart + bytes),
                totalBytes,
                currentIndex,
                pending.Count,
                "DOWNLOADING")));
            if (selective.TryGetValue(item, out var selected))
            {
                var source = SafePaths.Under(workRoot, Path.Combine("selected", item.Component.Id));
                Directory.CreateDirectory(source);
                foreach (var file in selected.Changed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileStart = completedBytes;
                    var fileProgress = new Progress<long>(bytes => progress?.Report(new UpdateProgress(
                        item.Component.Id, fileStart + bytes, totalBytes, currentIndex, pending.Count, "DOWNLOADING")));
                    await Task.Run(() => new SelectivePackageService(_packages.Http).DownloadFileAsync(item.Component, file, assetsBase,
                        SafePaths.Under(source, file.Path), fileProgress, cancellationToken), cancellationToken);
                    completedBytes += file.CompressedSize;
                }
                progress?.Report(new UpdateProgress(item.Component.Id, completedBytes, totalBytes, index + 1, pending.Count, "DOWNLOADING"));
                result.Add(new PreparedComponent(item, source, selected.Index.Files.Select(file => file.Path).ToList(), selected.Reused));
                continue;
            }
            var zip = await Task.Run(() => _packages.AcquireAsync(
                assetsBase, item.Component, Path.Combine(workRoot, "downloads"), cancellationToken, componentProgress), cancellationToken);
            completedBytes += item.Component.Size > 0 ? item.Component.Size : new FileInfo(zip).Length;
            progress?.Report(new UpdateProgress(item.Component.Id, Math.Min(totalBytes, completedBytes), totalBytes, currentIndex + 1, pending.Count, "DOWNLOADING"));
            var prepared = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new UpdateProgress(item.Component.Id, 0, 0, currentIndex, pending.Count, "EXTRACTING"));
                var extractRoot = SafePaths.Under(workRoot, Path.Combine("extracted", Guid.NewGuid().ToString("N")));
                PackageService.ExtractSafely(zip, extractRoot);
                var source = SafePaths.Under(extractRoot, item.Component.Source);
                if (!Directory.Exists(source)) throw new InvalidDataException($"{item.Component.Id}: source '{item.Component.Source}' assente nel pacchetto.");
                foreach (var required in item.Component.RequiredFiles)
                {
                    var requiredPath = SafePaths.Under(source, required);
                    if (!File.Exists(requiredPath) && !Directory.Exists(requiredPath))
                        throw new InvalidDataException($"{item.Component.Id}: file obbligatorio assente: {required}");
                }
                var expectedFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                    .Select(path => SafePaths.ManifestPath(Path.GetRelativePath(source, path)))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new PreparedComponent(item, source, expectedFiles);
            }, cancellationToken);
            result.Add(prepared);
        }
        return result;
    }

    private async Task<Dictionary<ComponentPlan, SelectedFiles>> SelectFilesAsync(List<ComponentPlan> pending, string assetsBase,
        IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        var indexes = new Dictionary<ComponentPlan, PackageFileIndex>();
        var service = new SelectivePackageService(_packages.Http);
        foreach (var item in pending.Where(item => item.IsPresent && item.Component.FileIndex is not null))
        {
            progress?.Report(new UpdateProgress(item.Component.Id, 0, 0, 0, pending.Count, "CHECKING"));
            indexes[item] = await service.LoadIndexAsync(item.Component, assetsBase, token);
        }
        // Shared overlays can intentionally contain different versions of the same file.
        // Download such entries so later components retain the established installation order.
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in indexes)
            foreach (var file in pair.Value.Files)
            {
                var path = SafePaths.Under(pair.Key.TargetPath, file.Path);
                if (expected.TryGetValue(path, out var hash) && hash != file.Sha256.ToLowerInvariant()) conflicts.Add(path);
                expected[path] = file.Sha256.ToLowerInvariant();
            }
        var result = new Dictionary<ComponentPlan, SelectedFiles>();
        foreach (var pair in indexes)
        {
            progress?.Report(new UpdateProgress(pair.Key.Component.Id, 0, 0, result.Count, pending.Count, "CHECKING"));
            var changed = new List<IndexedPackageFile>();
            var reused = new List<IndexedPackageFile>();
            foreach (var file in pair.Value.Files)
            {
                token.ThrowIfCancellationRequested();
                var path = SafePaths.Under(pair.Key.TargetPath, file.Path);
                var legacyOverlap = pending.Any(other => other != pair.Key && other.Component.FileIndex is null &&
                    path.StartsWith(other.TargetPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (!conflicts.Contains(path) && !legacyOverlap && await SelectivePackageService.MatchesAsync(pair.Key.TargetPath, file, token))
                    reused.Add(file);
                else changed.Add(file);
            }
            result[pair.Key] = new SelectedFiles(pair.Value, changed, reused);
        }
        return result;
    }

    private static async Task RecoverInterruptedAsync(LauncherLocations locations, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        var root = SafePaths.Under(locations.LauncherDataRoot, ".ksr-launcher/backups");
        if (!Directory.Exists(root)) return;
        SafePaths.RejectReparsePoints(locations.LauncherDataRoot, root);
        foreach (var backup in Directory.EnumerateDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            var path = SafePaths.Under(backup, "backup-manifest.json");
            if (!File.Exists(path)) continue;
            SafePaths.RejectReparsePoints(root, path);
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Recovery journal is too large.");
            var journal = JsonSerializer.Deserialize<BackupJournal>(await File.ReadAllTextAsync(path, token), ManifestService.JsonOptions)
                ?? throw new InvalidDataException("Invalid update recovery journal.");
            if (!journal.InProgress) continue;
            ValidateJournalRoots(journal, locations);
            progress?.Report(new UpdateProgress("Interrupted update", 0, 0, 0, journal.Entries.Count, "RESTORING"));
            await Task.Run(() => RollbackEntriesAsync(journal, locations, backup, CancellationToken.None));
            journal.InProgress = false;
            await WriteJournalAsync(backup, journal, CancellationToken.None);
        }
    }

    private static void InstallComponent(PreparedComponent prepared, ReleaseManifest manifest, LauncherLocations locations, string backupRoot, BackupJournal journal, CancellationToken token)
    {
        var component = prepared.Plan.Component;
        var root = UpdatePlanner.GetTargetRoot(component, locations);
        var target = SafePaths.Under(root, component.Target);
        SafePaths.RejectReparsePoints(root, target);
        if (prepared.ReusedFiles is not null)
            foreach (var file in prepared.ReusedFiles)
                if (!SelectivePackageService.MatchesAsync(target, file, token).GetAwaiter().GetResult())
                    throw new IOException("An installed file changed while the update was being prepared. Try again with KSP closed.");
        var replacement = target + ".ksr-new-" + Guid.NewGuid().ToString("N");
        var backupRelative = Path.Combine("components", component.Id);
        var backup = SafePaths.Under(backupRoot, backupRelative);
        var hadOriginal = Directory.Exists(target);

        try
        {
            journal.StagingDirectories.Add(new StagedUpdateDirectory(component.TargetKind,
                SafePaths.ManifestPath(Path.GetRelativePath(root, replacement))));
            WriteJournalAsync(backupRoot, journal, CancellationToken.None).GetAwaiter().GetResult();
            // Updates preserve installed content absent from the incoming package.
            // Removal requires a separate, explicitly authorized operation.
            if (hadOriginal) FileTree.CopyDirectory(target, replacement);
            FileTree.CopyDirectory(prepared.SourcePath, replacement);
            if (hadOriginal) CopyPreservedFiles(target, replacement, root, manifest);
            if (hadOriginal)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                FileTree.CopyDirectory(target, backup);
            }
            // The backup and durable journal must exist before the first destructive write.
            journal.Entries.Add(new BackupEntry
            {
                ComponentId = component.Id,
                TargetKind = component.TargetKind,
                Target = component.Target,
                BackupRelativePath = SafePaths.ManifestPath(backupRelative),
                HadOriginal = hadOriginal
            });
            WriteJournalAsync(backupRoot, journal, CancellationToken.None).GetAwaiter().GetResult();
            if (hadOriginal) FileTree.DeleteDirectory(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(replacement, target);
        }
        catch
        {
            FileTree.DeleteDirectory(replacement);
            if (!Directory.Exists(target) && Directory.Exists(backup))
                FileTree.CopyDirectory(backup, target);
            throw;
        }
    }

    private static void CopyPreservedFiles(string oldTarget, string replacement, string targetRoot, ReleaseManifest manifest)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(oldTarget, "*", SearchOption.AllDirectories))
        {
            var globalPath = SafePaths.ManifestPath(Path.GetRelativePath(targetRoot, sourceFile));
            if (!GlobMatcher.Any(globalPath, manifest.Preserve) || GlobMatcher.Any(globalPath, manifest.ManagedFilesInsidePreservedPaths)) continue;
            var relative = Path.GetRelativePath(oldTarget, sourceFile);
            var destination = SafePaths.Under(replacement, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination, true);
        }
    }

    public static async Task RollbackAsync(string backupRoot, LauncherLocations locations, bool apply, CancellationToken cancellationToken = default)
    {
        var journalPath = Path.Combine(Path.GetFullPath(backupRoot), "backup-manifest.json");
        await using var stream = File.OpenRead(journalPath);
        var journal = await JsonSerializer.DeserializeAsync<BackupJournal>(stream, ManifestService.JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Manifest di backup non valido.");
        ValidateJournalRoots(journal, locations);
        if (apply) await RollbackEntriesAsync(journal, locations, Path.GetFullPath(backupRoot), cancellationToken);
    }

    private static async Task RollbackEntriesAsync(BackupJournal journal, LauncherLocations locations, string backupRoot, CancellationToken cancellationToken)
    {
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var component = new ComponentManifest { TargetKind = entry.TargetKind, Target = entry.Target };
            var root = UpdatePlanner.GetTargetRoot(component, locations);
            var target = SafePaths.Under(root, entry.Target);
            var backup = SafePaths.Under(backupRoot, entry.BackupRelativePath);
            SafePaths.RejectReparsePoints(root, target);
            SafePaths.RejectReparsePoints(backupRoot, backup);
            if (entry.HadOriginal && !Directory.Exists(backup))
                throw new DirectoryNotFoundException($"Backup mancante per {entry.ComponentId}: {backup}");
            FileTree.DeleteDirectory(target);
            if (entry.HadOriginal)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                FileTree.CopyDirectory(backup, target);
            }
        }

        foreach (var staging in journal.StagingDirectories)
        {
            var root = staging.TargetKind.Equals("launcherData", StringComparison.OrdinalIgnoreCase)
                ? locations.LauncherDataRoot : locations.KspRoot;
            var path = SafePaths.Under(root, staging.RelativePath);
            var suffix = Path.GetFileName(path).Split(".ksr-new-", StringSplitOptions.None).Last();
            if (suffix.Length != 32 || !suffix.All(Uri.IsHexDigit))
                throw new InvalidDataException("Unsafe interrupted staging directory.");
            SafePaths.RejectReparsePoints(root, path);
            FileTree.DeleteDirectory(path);
        }

        var previousState = Path.Combine(backupRoot, "installed-state.previous.json");
        var statePath = StateStore.GetStatePath(locations.LauncherDataRoot);
        if (File.Exists(previousState))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.Copy(previousState, statePath, true);
        }
        else if (File.Exists(statePath)) File.Delete(statePath);
        await Task.CompletedTask;
    }

    private static async Task WriteJournalAsync(string backupRoot, BackupJournal journal, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(backupRoot, "backup-manifest.json");
        var temporary = destination + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, journal, ManifestService.JsonOptions, cancellationToken);
            stream.Flush(true);
        }
        File.Move(temporary, destination, true);
    }

    private static void ValidateJournalRoots(BackupJournal journal, LauncherLocations locations)
    {
        if (!string.Equals(Path.GetFullPath(journal.KspRoot), Path.GetFullPath(locations.KspRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(journal.LauncherDataRoot), Path.GetFullPath(locations.LauncherDataRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Il backup appartiene a un'altra installazione.");
    }

    private sealed record PreparedComponent(ComponentPlan Plan, string SourcePath, List<string> ExpectedFiles,
        List<IndexedPackageFile>? ReusedFiles = null);
    private sealed record SelectedFiles(PackageFileIndex Index, List<IndexedPackageFile> Changed, List<IndexedPackageFile> Reused);

}
