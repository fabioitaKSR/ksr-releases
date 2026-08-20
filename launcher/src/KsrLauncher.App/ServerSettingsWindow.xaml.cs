using System.IO;
using System.Text.Json;
using System.Windows;

namespace KsrLauncher.App;

public partial class ServerSettingsWindow : Window
{
    public string? ServerUrl { get; private set; }

    public ServerSettingsWindow(string? currentServerUrl)
    {
        InitializeComponent();
        ServerUrlTextBox.Text = currentServerUrl ?? string.Empty;
        ServerUrlTextBox.Focus();
        ServerUrlTextBox.CaretIndex = ServerUrlTextBox.Text.Length;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var value = ServerUrlTextBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            MessageBox.Show("Enter a valid absolute server URL.", "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var localHttp = uri.Scheme == Uri.UriSchemeHttp &&
            (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        if (uri.Scheme != Uri.UriSchemeHttps && !localHttp)
        {
            MessageBox.Show("Use HTTPS. Plain HTTP is allowed only on localhost.", "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ServerUrl = value;
        LauncherSettingsStore.SaveServerUrl(value);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

internal static class LauncherSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KSRLauncher", "settings.json");

    public static string? LoadServerUrl()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsPath));
            return string.IsNullOrWhiteSpace(settings?.ServerUrl) ? null : settings.ServerUrl;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public static void SaveServerUrl(string serverUrl)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new LauncherSettings(serverUrl), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, true);
    }

    private sealed record LauncherSettings(string ServerUrl);
}
