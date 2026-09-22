using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class ServerSettingsWindow : Window
{
    private string? _testKspRoot;
    public string? ServerUrl { get; private set; }

    public ServerSettingsWindow(string? currentServerUrl)
    {
        InitializeComponent();
        _testKspRoot = LauncherSettingsStore.LoadTestKspRoot();
        RefreshTestKspPath();
        ServerUrlTextBox.Text = currentServerUrl ?? string.Empty;
        ServerUrlTextBox.Focus();
        ServerUrlTextBox.CaretIndex = ServerUrlTextBox.Text.Length;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveSettings()) return;
        DialogResult = true;
    }

    private bool TrySaveSettings()
    {
        var value = ServerUrlTextBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            MessageBox.Show("Enter a valid absolute server URL.", "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var localHttp = uri.Scheme == Uri.UriSchemeHttp &&
            (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        if (uri.Scheme != Uri.UriSchemeHttps && !localHttp)
        {
            MessageBox.Show("Use HTTPS. Plain HTTP is allowed only on localhost.", "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        ServerUrl = value;
        LauncherSettingsStore.SaveServerAndTestKspRoot(value, _testKspRoot);
        return true;
    }

    private void BrowseTestKsp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Select the test Kerbal Space Program folder", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(_testKspRoot) && Directory.Exists(_testKspRoot))
            picker.InitialDirectory = _testKspRoot;
        if (picker.ShowDialog(this) != true) return;

        var selected = Path.GetFullPath(picker.FolderName);
        if (!File.Exists(Path.Combine(selected, "KSP_x64.exe")) || !Directory.Exists(Path.Combine(selected, "GameData")))
        {
            MessageBox.Show("The test folder must contain KSP_x64.exe and GameData.",
                "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _testKspRoot = selected;
        RefreshTestKspPath();
    }

    private void ClearTestKsp_Click(object sender, RoutedEventArgs e)
    {
        _testKspRoot = null;
        RefreshTestKspPath();
    }

    private void RefreshTestKspPath()
    {
        TestKspPathTextBox.Text = _testKspRoot ?? "No test installation selected";
        TestKspPathTextBox.ToolTip = _testKspRoot;
        LaunchTestKspButton.IsEnabled = !string.IsNullOrWhiteSpace(_testKspRoot) &&
            File.Exists(Path.Combine(_testKspRoot, "KSP_x64.exe")) &&
            Directory.Exists(Path.Combine(_testKspRoot, "GameData"));
    }

    private void LaunchTestKsp_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_testKspRoot) || !LaunchTestKspButton.IsEnabled) return;
        if (Process.GetProcessesByName("KSP_x64").Length > 0)
        {
            MessageBox.Show("Close the running KSP game before starting the test installation.",
                "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TrySaveSettings()) return;
        try
        {
            GameLoggerConfiguration.Clear(_testKspRoot);
            var executable = Path.Combine(_testKspRoot, "KSP_x64.exe");
            Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = _testKspRoot,
                UseShellExecute = true
            });
            DialogResult = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"The test game could not be launched.\n\n{exception.Message}",
                "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
