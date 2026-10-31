namespace KsrLauncher.Core;

public static class BetaInstallationUpdates
{
    public static bool HasValidDestination(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try { UpdatePlanner.ValidateKspRoot(root); return true; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    public static string GetLauncherDataRoot(string moduleId, string root, string? mainRoot, string launcherData)
    {
        BetaModulesClient.ValidateId(moduleId);
        if (moduleId == "ksr-full") return TestInstallationPaths.GetLauncherDataRoot(root, mainRoot, launcherData);
        var pathKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..16];
        return Path.Combine(launcherData, "beta", moduleId, pathKey);
    }

    public static bool HasIndependentDestination(string moduleId, string root, string? mainRoot,
        IEnumerable<(string Id, string? Root)> destinations) =>
        (moduleId == "ksr-full" || !TestInstallationPaths.IsSameKspRoot(mainRoot, root)) &&
        !destinations.Any(other => other.Id != moduleId && TestInstallationPaths.IsSameKspRoot(other.Root, root));

    // Destination validation precedes all authorization/download callbacks. One
    // sequential pass per startup/login, no timer or cross-beta fallback path.
    public static async Task RunAsync(IEnumerable<KsrBetaModule> authorizedModules,
        Func<string, string?> selectedRoot, Func<bool> canContinue,
        Func<KsrBetaModule, string, Task> update, Action<KsrBetaModule, Exception> failure,
        string? mainRoot = null)
    {
        var targets = authorizedModules.Select(module => (Module: module, Root: selectedRoot(module.Id)))
            .Where(target => HasValidDestination(target.Root))
            .Select(target => (target.Module, Root: Path.GetFullPath(target.Root!)))
            .ToArray();
        foreach (var target in targets)
        {
            if (!canContinue()) return;
            var module = target.Module;
            if (!HasIndependentDestination(module.Id, target.Root, mainRoot,
                targets.Select(other => (other.Module.Id, (string?)other.Root))))
            {
                failure(module, new InvalidDataException("Select a separate KSP folder for this beta before automatic updates."));
                continue;
            }
            // Selection may have changed while another beta was updating.
            if (!TestInstallationPaths.IsSameKspRoot(target.Root, selectedRoot(module.Id))) continue;
            try { await update(module, target.Root); }
            catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or
                UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            { failure(module, e); }
        }
    }
}
