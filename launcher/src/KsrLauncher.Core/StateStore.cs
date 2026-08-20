using System.Text.Json;

namespace KsrLauncher.Core;

public static class StateStore
{
    public static string GetStatePath(string launcherDataRoot) => Path.Combine(launcherDataRoot, ".ksr-launcher", "installed-state.json");

    public static async Task<InstalledState> LoadAsync(string launcherDataRoot, CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(launcherDataRoot);
        if (!File.Exists(path)) return new InstalledState();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<InstalledState>(stream, ManifestService.JsonOptions, cancellationToken)
            ?? new InstalledState();
    }

    public static async Task SaveAsync(string launcherDataRoot, InstalledState state, CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(launcherDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, state, ManifestService.JsonOptions, cancellationToken);
        File.Move(temporary, path, true);
    }
}
