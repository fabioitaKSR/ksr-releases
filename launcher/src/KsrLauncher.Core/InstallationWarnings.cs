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
            if (!state.Components.TryGetValue(label, out var installed))
            {
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
            if (missingRequired.Length > 0)
                warnings.Add($"{label}: required files are missing: {string.Join(", ", missingRequired)}.");
        }
        return warnings;
    }
}
