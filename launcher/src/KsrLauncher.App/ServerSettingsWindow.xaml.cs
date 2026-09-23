using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class ServerSettingsWindow : Window
{
    private string? _testKspRoot;
    private bool _maintenanceInProgress;
    public string? ServerUrl { get; private set; }

    public ServerSettingsWindow(string? currentServerUrl)
    {
        InitializeComponent();
        _testKspRoot = LauncherSettingsStore.LoadTestKspRoot();
        RefreshTestKspPath();
        ServerUrlTextBox.Text = currentServerUrl ?? string.Empty;
        ServerUrlTextBox.Focus();
        ServerUrlTextBox.CaretIndex = ServerUrlTextBox.Text.Length;
        Closing += (_, e) => { if (_maintenanceInProgress) e.Cancel = true; };
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
        UpgradeTestKspButton.IsEnabled = LaunchTestKspButton.IsEnabled;
        RepairTestKspButton.IsEnabled = LaunchTestKspButton.IsEnabled;
    }

    private async void UpgradeTestKsp_Click(object sender, RoutedEventArgs e) =>
        await MaintainTestKspAsync(UpdatePolicy.VerifyAllFiles);

    private async void RepairTestKsp_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reinstall all official KSR game components in the selected test installation?",
                "Repair test game", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await MaintainTestKspAsync(UpdatePolicy.ReinstallAll);
    }

    private async Task MaintainTestKspAsync(UpdatePolicy policy)
    {
        if (_maintenanceInProgress || string.IsNullOrWhiteSpace(_testKspRoot) || !UpgradeTestKspButton.IsEnabled) return;
        if (Process.GetProcessesByName("KSP_x64").Length > 0)
        {
            MessageBox.Show("Close KSP before updating the test installation.",
                "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _maintenanceInProgress = true;
        BrowseTestKspButton.IsEnabled = false;
        ClearTestKspButton.IsEnabled = false;
        UpgradeTestKspButton.IsEnabled = false;
        RepairTestKspButton.IsEnabled = false;
        LaunchTestKspButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        TestKspMaintenanceProgress.Visibility = Visibility.Visible;
        TestKspMaintenanceProgress.IsIndeterminate = true;
        TestKspMaintenanceStatus.Foreground = (Brush)FindResource("MutedBrush");
        TestKspMaintenanceStatus.Text = "Checking official KSR release...";
        try
        {
            var release = await new GitHubReleaseClient().ResolveAsync("fabioitaKSR/ksr-releases");
            release.Manifest.Components = release.Manifest.Components
                .Where(component => string.Equals(component.TargetKind, "ksp", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (release.Manifest.Components.Count == 0)
                throw new InvalidDataException("The official release has no KSP game components.");

            var progress = new Progress<UpdateProgress>(value =>
            {
                var percent = value.Phase == "INSTALLING"
                    ? Math.Clamp(value.ComponentsCompleted * 100d / Math.Max(1, value.TotalComponents), 0, 100)
                    : value.TotalBytes > 0
                    ? Math.Clamp(value.BytesDownloaded * 100d / value.TotalBytes, 0, 100)
                    : 0;
                TestKspMaintenanceProgress.IsIndeterminate = false;
                TestKspMaintenanceProgress.Value = percent;
                TestKspMaintenanceStatus.Text = $"{value.Phase} {value.ComponentId}  {percent:0}%";
            });
            var launcherData = GetTestLauncherDataRoot(_testKspRoot);
            var result = await new UpdateEngine().RunAsync(release.Manifest,
                new LauncherLocations(_testKspRoot, launcherData), release.AssetsBaseUrl,
                true, policy, progress);
            TestKspMaintenanceProgress.IsIndeterminate = false;
            TestKspMaintenanceProgress.Value = 100;
            TestKspMaintenanceStatus.Foreground = (Brush)FindResource("GreenBrush");
            TestKspMaintenanceStatus.Text = result.Applied
                ? $"{(policy == UpdatePolicy.ReinstallAll ? "Repair" : "Upgrade")} complete: {result.Plan.Components.Count(item => item.NeedsUpdate)} components."
                : "All KSR game components are up to date.";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TestKspMaintenanceProgress.IsIndeterminate = false;
            TestKspMaintenanceStatus.Foreground = (Brush)FindResource("ErrorBrush");
            TestKspMaintenanceStatus.Text = $"Operation failed: {exception.Message}";
        }
        finally
        {
            _maintenanceInProgress = false;
            BrowseTestKspButton.IsEnabled = true;
            ClearTestKspButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            RefreshTestKspPath();
        }
    }

    private static string GetTestLauncherDataRoot(string testKspRoot)
    {
        var launcherData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KSRLauncher");
        var campaignRoot = LauncherSettingsStore.LoadKspRoot();
        var normalizedTestRoot = Path.GetFullPath(testKspRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(campaignRoot) &&
            string.Equals(normalizedTestRoot, Path.GetFullPath(campaignRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)) return launcherData;

        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedTestRoot.ToUpperInvariant())));
        return Path.Combine(launcherData, "test-installations", identity);
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
