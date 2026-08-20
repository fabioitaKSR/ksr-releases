namespace KsrLauncher.Core;

public static class UpdatePlanner
{
    public static UpdatePlan Create(ReleaseManifest manifest, InstalledState state, LauncherLocations locations)
    {
        var components = manifest.Components.Select(component =>
        {
            state.Components.TryGetValue(component.Id, out var installed);
            var root = GetTargetRoot(component, locations);
            var target = SafePaths.Under(root, component.Target);
            var exists = Directory.Exists(target) || File.Exists(target);
            var needsUpdate = installed is null || !exists ||
                !string.Equals(installed.Version, manifest.Version, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(installed.Sha256, component.Sha256, StringComparison.OrdinalIgnoreCase);
            var reason = installed is null ? "non installato" : !exists ? "cartella mancante" : needsUpdate ? "versione o pacchetto differente" : "aggiornato";
            return new ComponentPlan(component, installed?.Version, needsUpdate, reason, target);
        }).ToList();
        return new UpdatePlan(manifest, components);
    }

    public static string GetTargetRoot(ComponentManifest component, LauncherLocations locations) =>
        string.Equals(component.TargetKind, "launcherData", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(locations.LauncherDataRoot)
            : Path.GetFullPath(locations.KspRoot);

    public static void ValidateKspRoot(string root)
    {
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, "KSP_x64.exe"))) throw new DirectoryNotFoundException($"KSP_x64.exe non trovato in {root}");
        if (!Directory.Exists(Path.Combine(root, "GameData"))) throw new DirectoryNotFoundException($"GameData non trovata in {root}");
    }
}
