using System.Text.Json;

namespace KsrLauncher.Core;

public sealed class UpdateEngine(PackageService? packageService = null)
{
    private readonly PackageService _packages = packageService ?? new PackageService();

    public async Task<UpdateResult> RunAsync(
        ReleaseManifest manifest,
        LauncherLocations locations,
        string assetsBase,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        if (manifest.Components.Any(component => !string.Equals(component.TargetKind, "launcherData", StringComparison.OrdinalIgnoreCase)))
            UpdatePlanner.ValidateKspRoot(locations.KspRoot);
        Directory.CreateDirectory(locations.LauncherDataRoot);

        var state = await StateStore.LoadAsync(locations.LauncherDataRoot, cancellationToken);
        var plan = UpdatePlanner.Create(manifest, state, locations);
        if (!apply || !plan.HasUpdates) return new UpdateResult(plan, false, null);
        if (string.IsNullOrWhiteSpace(assetsBase)) throw new ArgumentException("assetsBase e obbligatorio per applicare un aggiornamento.");

        var launcherRoot = Path.Combine(locations.LauncherDataRoot, ".ksr-launcher");
        var transactionId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var workRoot = Path.Combine(launcherRoot, "work", transactionId);
        var backupRoot = Path.Combine(launcherRoot, "backups", transactionId);
        var journal = new BackupJournal
        {
            ReleaseVersion = manifest.Version,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            KspRoot = Path.GetFullPath(locations.KspRoot),
            LauncherDataRoot = Path.GetFullPath(locations.LauncherDataRoot)
        };

        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(backupRoot);
        try
        {
            var prepared = await PrepareAsync(plan, assetsBase, workRoot, cancellationToken);
            var statePath = StateStore.GetStatePath(locations.LauncherDataRoot);
            if (File.Exists(statePath)) File.Copy(statePath, Path.Combine(backupRoot, "installed-state.previous.json"), true);

            foreach (var group in prepared.GroupBy(item => item.Plan.Component.TransactionGroup))
            {
                foreach (var item in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InstallComponent(item, manifest, locations, backupRoot, journal);
                }
            }

            state.Product = manifest.Product;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            foreach (var item in plan.Components.Where(item => item.NeedsUpdate))
                state.Components[item.Component.Id] = new InstalledComponent
                {
                    Version = manifest.Version,
                    Sha256 = item.Component.Sha256.ToLowerInvariant(),
                    TargetKind = item.Component.TargetKind,
                    Target = item.Component.Target
                };
            await StateStore.SaveAsync(locations.LauncherDataRoot, state, cancellationToken);
            await WriteJournalAsync(backupRoot, journal, cancellationToken);
            return new UpdateResult(plan, true, backupRoot);
        }
        catch
        {
            // Se la preparazione fallisce prima della prima sostituzione, l'installazione
            // e lo stato esistente non sono stati toccati e non devono essere ripristinati.
            if (journal.Entries.Count > 0)
                await RollbackEntriesAsync(journal, locations, backupRoot, cancellationToken);
            throw;
        }
        finally
        {
            FileTree.DeleteDirectory(workRoot);
        }
    }

    private async Task<List<PreparedComponent>> PrepareAsync(UpdatePlan plan, string assetsBase, string workRoot, CancellationToken cancellationToken)
    {
        var result = new List<PreparedComponent>();
        foreach (var item in plan.Components.Where(item => item.NeedsUpdate))
        {
            var zip = await _packages.AcquireAsync(assetsBase, item.Component, Path.Combine(workRoot, "downloads"), cancellationToken);
            var extractRoot = Path.Combine(workRoot, "extracted", item.Component.Id);
            PackageService.ExtractSafely(zip, extractRoot);
            var source = SafePaths.Under(extractRoot, item.Component.Source);
            if (!Directory.Exists(source)) throw new InvalidDataException($"{item.Component.Id}: source '{item.Component.Source}' assente nel pacchetto.");
            foreach (var required in item.Component.RequiredFiles)
            {
                var requiredPath = SafePaths.Under(source, required);
                if (!File.Exists(requiredPath) && !Directory.Exists(requiredPath))
                    throw new InvalidDataException($"{item.Component.Id}: file obbligatorio assente: {required}");
            }
            result.Add(new PreparedComponent(item, source));
        }
        return result;
    }

    private static void InstallComponent(PreparedComponent prepared, ReleaseManifest manifest, LauncherLocations locations, string backupRoot, BackupJournal journal)
    {
        var component = prepared.Plan.Component;
        var root = UpdatePlanner.GetTargetRoot(component, locations);
        var target = SafePaths.Under(root, component.Target);
        SafePaths.RejectReparsePoints(root, target);
        var replacement = target + ".ksr-new-" + Guid.NewGuid().ToString("N");
        var backupRelative = Path.Combine("components", component.Id);
        var backup = SafePaths.Under(backupRoot, backupRelative);
        var hadOriginal = Directory.Exists(target);

        try
        {
            FileTree.CopyDirectory(prepared.SourcePath, replacement);
            if (hadOriginal) CopyPreservedFiles(target, replacement, root, manifest);
            if (hadOriginal)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(target, backup);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(replacement, target);
            journal.Entries.Add(new BackupEntry
            {
                ComponentId = component.Id,
                TargetKind = component.TargetKind,
                Target = component.Target,
                BackupRelativePath = SafePaths.ManifestPath(backupRelative),
                HadOriginal = hadOriginal
            });
        }
        catch
        {
            FileTree.DeleteDirectory(replacement);
            if (!Directory.Exists(target) && Directory.Exists(backup)) Directory.Move(backup, target);
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
            FileTree.DeleteDirectory(target);
            if (entry.HadOriginal)
            {
                if (!Directory.Exists(backup)) throw new DirectoryNotFoundException($"Backup mancante per {entry.ComponentId}: {backup}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(backup, target);
            }
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
        await using var stream = File.Create(Path.Combine(backupRoot, "backup-manifest.json"));
        await JsonSerializer.SerializeAsync(stream, journal, ManifestService.JsonOptions, cancellationToken);
    }

    private static void ValidateJournalRoots(BackupJournal journal, LauncherLocations locations)
    {
        if (!string.Equals(Path.GetFullPath(journal.KspRoot), Path.GetFullPath(locations.KspRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(journal.LauncherDataRoot), Path.GetFullPath(locations.LauncherDataRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Il backup appartiene a un'altra installazione.");
    }

    private sealed record PreparedComponent(ComponentPlan Plan, string SourcePath);
}
