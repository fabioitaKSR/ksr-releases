using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsrLauncher.Core;

public static class LauncherSettingsStore
{
    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KSRLauncher", "settings.json");

    public static string? LoadServerUrl(string? settingsPath = null)
    {
        var value = TryRead(settingsPath).ServerUrl;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? LoadKspRoot(string? settingsPath = null)
    {
        var value = TryRead(settingsPath).KspRoot;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? LoadTestKspRoot(string? settingsPath = null)
    {
        var value = TryRead(settingsPath).TestKspRoot;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static void SaveKspRoot(string kspRoot, string? settingsPath = null)
    {
        var settings = Read(settingsPath);
        settings.KspRoot = Path.GetFullPath(kspRoot);
        Write(settings, settingsPath);
    }

    public static void SaveServerAndTestKspRoot(string serverUrl, string? testKspRoot, string? settingsPath = null)
    {
        var settings = Read(settingsPath);
        settings.ServerUrl = serverUrl;
        settings.TestKspRoot = testKspRoot is null ? null : Path.GetFullPath(testKspRoot);
        Write(settings, settingsPath);
    }

    private static LauncherSettings Read(string? settingsPath)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        if (!File.Exists(path)) return new LauncherSettings();
        return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path)) ??
            throw new InvalidDataException($"Launcher settings are empty: {path}");
    }

    private static LauncherSettings TryRead(string? settingsPath)
    {
        try { return Read(settingsPath); }
        catch (JsonException) { return new LauncherSettings(); }
        catch (IOException) { return new LauncherSettings(); }
    }

    private static void Write(LauncherSettings settings, string? settingsPath)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private sealed class LauncherSettings
    {
        public string? ServerUrl { get; set; }
        public string? KspRoot { get; set; }
        public string? TestKspRoot { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> AdditionalSettings { get; set; } = new();
    }
}
