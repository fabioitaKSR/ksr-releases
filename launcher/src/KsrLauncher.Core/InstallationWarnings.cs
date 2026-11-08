namespace KsrLauncher.Core;

public static class InstallationWarnings
{
    public static IReadOnlyList<string> Inspect(UpdatePlan plan, InstalledState state)
    {
        var warnings = new List<string>();
        foreach (var item in plan.Components.Where(item => item.IsPresent &&
            !string.Equals(item.Component.TargetKind, "launcherData", StringComparison.OrdinalIgnoreCase)))
        {
            var label = item.Component.Id;
            // An additive package can share a directory with an installed component.
            // Its complete verified payload is checked separately before any writes.
            var managedOverlay = ManagedOverlayFiles(item, state) is not null;
            if (!state.Components.TryGetValue(label, out var installed))
            {
                if (!managedOverlay)
                    warnings.Add($"{label}: already present, but its installed version is not registered by this launcher.");
            }
            else
            {
                if (!string.Equals(installed.Sha256, item.Component.Sha256, StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"{label}: the registered package differs from the selected release (installed catalog {installed.Version}; selected catalog {plan.Manifest.Version}).");
                if (!string.Equals(installed.Target, item.Component.Target, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(installed.TargetKind, item.Component.TargetKind, StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"{label}: the registered installation location differs from the selected package.");

                var missing = installed.ExpectedFiles
                    .Where(path => !File.Exists(SafePaths.Under(item.TargetPath, path))).Take(5).ToArray();
                if (missing.Length > 0)
                    warnings.Add($"{label}: registered files are missing: {string.Join(", ", missing)}.");
            }
            var missingRequired = item.Component.RequiredFiles.Where(path =>
            {
                var target = SafePaths.Under(item.TargetPath, path);
                return !File.Exists(target) && !Directory.Exists(target);
            }).Take(5).ToArray();
            if (missingRequired.Length > 0 && !managedOverlay)
                warnings.Add($"{label}: required files are missing: {string.Join(", ", missingRequired)}.");
        }
        return warnings;
    }

    public static IReadOnlyList<string> InspectOverlayFiles(ComponentPlan item, InstalledState state, IEnumerable<string> incomingFiles)
    {
        var owned = ManagedOverlayFiles(item, state);
        if (owned is null) return [];
        var collisions = incomingFiles.Where(relative =>
        {
            var path = SafePaths.Under(item.TargetPath, relative);
            return (File.Exists(path) || Directory.Exists(path)) && !owned.Contains(SafePaths.ManifestPath(relative));
        }).Take(5).ToArray();
        return collisions.Length == 0 ? [] :
            [$"{item.Component.Id}: the update would overwrite files not registered by this launcher: {string.Join(", ", collisions)}."];
    }

    private static HashSet<string>? ManagedOverlayFiles(ComponentPlan item, InstalledState state)
    {
        if (state.Components.ContainsKey(item.Component.Id) ||
            !string.Equals(item.Component.InstallMode, "overlay", StringComparison.OrdinalIgnoreCase)) return null;

        // Virtual paths only: map each registered inventory into the incoming
        // target's coordinates, preserving path boundaries and target-kind isolation.
        var scope = Path.Combine(Path.GetTempPath(), "ksr-warning-inventory");
        var target = SafePaths.Under(scope, item.Component.Target);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var registered in state.Components.Values.Where(component =>
            string.Equals(component.TargetKind, item.Component.TargetKind, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var owner = SafePaths.Under(scope, registered.Target);
                if (!string.Equals(target, owner, StringComparison.OrdinalIgnoreCase) &&
                    !target.StartsWith(owner + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var relative in registered.ExpectedFiles)
                {
                    var file = SafePaths.Under(owner, relative);
                    if (file.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        owned.Add(SafePaths.ManifestPath(Path.GetRelativePath(target, file)));
                }
            }
            catch (InvalidDataException) { /* Invalid inventory cannot establish ownership. */ }
        }
        return owned.Any(relative => File.Exists(SafePaths.Under(item.TargetPath, relative))) ? owned : null;
    }
}
