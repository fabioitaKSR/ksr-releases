namespace KsrLauncher.Core;

public static class UpdatePlanner
{
    public static UpdatePlan Create(
        ReleaseManifest manifest,
        InstalledState state,
        LauncherLocations locations,
        UpdatePolicy policy = UpdatePolicy.ExistingOnly)
    {
        var components = manifest.Components.Select(component =>
        {
            state.Components.TryGetValue(component.Id, out var installed);
            var root = GetTargetRoot(component, locations);
            var target = SafePaths.Under(root, component.Target);
            var exists = Directory.Exists(target) || File.Exists(target);
            var requiredFilesPresent = exists && component.RequiredFiles.All(required =>
            {
                var requiredPath = SafePaths.Under(target, required);
                return File.Exists(requiredPath) || Directory.Exists(requiredPath);
            });
            var differs = installed is null ||
                          !string.Equals(installed.Version, manifest.Version, StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(installed.Sha256, component.Sha256, StringComparison.OrdinalIgnoreCase) ||
                          !requiredFilesPresent;
            var needsUpdate = exists ? differs : policy == UpdatePolicy.InstallOrRepair;
            var reason = (exists, needsUpdate, installed, policy) switch
            {
                (false, false, _, UpdatePolicy.ExistingOnly) => "non installato: ignorato",
                (false, true, _, UpdatePolicy.InstallOrRepair) => "mancante: installazione/riparazione richiesta",
                (true, true, _, _) when !requiredFilesPresent => "installazione incompleta: file obbligatori mancanti",
                (true, true, null, _) => "presente ma versione non registrata",
                (true, true, _, _) => "versione o pacchetto differente",
                _ => "aggiornato"
            };
            return new ComponentPlan(component, installed?.Version, exists, needsUpdate, reason, target);
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
