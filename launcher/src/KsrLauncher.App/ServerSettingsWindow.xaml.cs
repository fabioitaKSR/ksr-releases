using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class ServerSettingsWindow : Window
{
    private readonly KsrBetaModule? _module;
    private string ModuleId => _module?.Id ?? "ksr-full";
    private bool IsFullMode => ModuleId == "ksr-full";
    private bool HasModuleAccess => _module is not null && (IsFullMode ? LauncherSession.CanAccessTestGame :
        BetaAccess.Modules.Any(m => m.Id == ModuleId));
    // Shared support controls apply to every authorized beta, including future modules.
    private bool CanSendBetaSupport => HasModuleAccess && !_maintenanceInProgress &&
        !string.IsNullOrWhiteSpace(_testKspRoot) &&
        File.Exists(Path.Combine(_testKspRoot, "KSP_x64.exe")) &&
        Directory.Exists(Path.Combine(_testKspRoot, "GameData"));
    private bool _accessRevoked;
    private readonly System.Threading.CancellationTokenSource _operationCancellation = new();
    private string? _testKspRoot;
    private bool _maintenanceInProgress;
    public string? ServerUrl { get; private set; }

    public ServerSettingsWindow(string? currentServerUrl, KsrBetaModule? module = null)
    {
        _module = module;
        InitializeComponent();
        _testKspRoot = LauncherSettingsStore.LoadBetaKspRoot(ModuleId);
        if (_module is not null)
        {
            Title = _module.Label;
            ServerSection.Visibility = Visibility.Collapsed;
            ServerUrlLabel.Visibility = Visibility.Collapsed;
            ServerUrlTextBox.Visibility = Visibility.Collapsed;
            ServerDescription.Visibility = Visibility.Collapsed;
            foreach (var row in new[] { ServerTitleRow, ServerTitleGapRow, ServerLabelRow,
                ServerLabelGapRow, ServerUrlRow, ServerDescriptionRow, ServerBottomGapRow }) row.Height = new GridLength(0);
            TestGameTitle.Text = _module.Label.ToUpperInvariant();
            TestGameDescription.Text = _module.Description +
                (IsFullMode ? " This installation uses the existing test1 campaign." : string.Empty);
            LaunchTestKspButton.Content = "LAUNCH KSP";
        }
        BetaAccess.Changed += AccessChanged;
        Closed += (_, _) => { BetaAccess.Changed -= AccessChanged; _operationCancellation.Cancel(); };
        if (!HasModuleAccess)
        {
            Height = 380;
            foreach (var row in new[] { TestGameTitleRow, TestGameTitleGapRow, TestGamePathRow,
                         TestGameDescriptionRow, TestGameMaintenanceRow, TestGameMaintenanceGapRow,
                         TestGameSupportRow, TestGameSupportGapRow, TestGameProgressRow, TestGameBottomGapRow })
                row.Height = new GridLength(0);
            TestGameTitle.Visibility = Visibility.Collapsed;
            TestGamePath.Visibility = Visibility.Collapsed;
            TestGameDescription.Visibility = Visibility.Collapsed;
            TestGameMaintenance.Visibility = Visibility.Collapsed;
            TestGameSupport.Visibility = Visibility.Collapsed;
            LaunchTestKspButton.Visibility = Visibility.Collapsed;
        }
        RefreshTestKspPath();
        ServerUrlTextBox.Text = currentServerUrl ?? string.Empty;
        if (_module is null) ServerUrlTextBox.Focus();
        ServerUrlTextBox.CaretIndex = ServerUrlTextBox.Text.Length;
        Closing += (_, e) => { if (_maintenanceInProgress && !_accessRevoked) e.Cancel = true; };
    }

    private void AccessChanged()
    {
        if (HasModuleAccess) return;
        _accessRevoked = true;
        _operationCancellation.Cancel();
        _testKspRoot = null;
        TestKspPathTextBox.Text = string.Empty;
        if (_module is not null) Close();
        else
        {
            Height = 380;
            foreach (var row in new[] { TestGameTitleRow, TestGameTitleGapRow, TestGamePathRow,
                TestGameDescriptionRow, TestGameMaintenanceRow, TestGameMaintenanceGapRow,
                TestGameSupportRow, TestGameSupportGapRow, TestGameProgressRow, TestGameBottomGapRow })
                row.Height = new GridLength(0);
            foreach (var element in new FrameworkElement[] { TestGameTitle, TestGamePath, TestGameDescription,
                TestGameMaintenance, TestGameSupport, LaunchTestKspButton }) element.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<bool> CheckAccessAsync()
    {
        if (!string.Equals(ServerUrlTextBox.Text.Trim().TrimEnd('/'),
                LauncherSession.ServerUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Save the server setting and sign in to that server before managing a beta.",
                "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var module = await BetaAccess.RequireAsync(ModuleId);
        if (module is not null && !_accessRevoked) return true;
        if (_module is null) RefreshTestKspPath();
        return false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_module is not null && !await CheckAccessAsync()) return;
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
        if (_module is not null)
            LauncherSettingsStore.SaveBetaKspRoot(ModuleId, _testKspRoot);
        else
            LauncherSettingsStore.SaveServerAndTestKspRoot(value,
                HasModuleAccess ? _testKspRoot : LauncherSettingsStore.LoadTestKspRoot());
        return true;
    }

    private void BrowseTestKsp_Click(object sender, RoutedEventArgs e)
    {
        if (!HasModuleAccess) return;
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
        if (!HasModuleAccess) return;
        _testKspRoot = null;
        RefreshTestKspPath();
    }

    private void RefreshTestKspPath()
    {
        TestKspPathTextBox.Text = _testKspRoot ?? "No test installation selected";
        TestKspPathTextBox.ToolTip = _testKspRoot;
        LaunchTestKspButton.IsEnabled = HasModuleAccess && !string.IsNullOrWhiteSpace(_testKspRoot) &&
            File.Exists(Path.Combine(_testKspRoot, "KSP_x64.exe")) &&
            Directory.Exists(Path.Combine(_testKspRoot, "GameData"));
        UpgradeTestKspButton.IsEnabled = LaunchTestKspButton.IsEnabled;
        RepairTestKspButton.IsEnabled = LaunchTestKspButton.IsEnabled;
        SendTestSaveButton.IsEnabled = CanSendBetaSupport;
        SendTestLogButton.IsEnabled = CanSendBetaSupport;
        TestGameSupport.Visibility = HasModuleAccess ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SendTestSave_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSendBetaSupport || !await CheckAccessAsync() || !CanSendBetaSupport) return;
        var supportRoot = _testKspRoot!;
        try
        {
            var picker = new OpenFolderDialog
            {
                Title = "Select a save from the test installation",
                InitialDirectory = TestGameSupportPaths.SavesRoot(supportRoot),
                Multiselect = false
            };
            if (picker.ShowDialog(this) != true) return;
            var save = TestGameSupportPaths.SaveFolder(supportRoot, picker.FolderName);
            OpenTestSupportDialog(SupportReportType.Save, save, Path.GetFileName(save), true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(exception.Message, "KSR Support", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SendTestLog_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSendBetaSupport || !await CheckAccessAsync() || !CanSendBetaSupport) return;
        var supportRoot = _testKspRoot!;
        try
        {
            var log = TestGameSupportPaths.LogFile(supportRoot);
            OpenTestSupportDialog(SupportReportType.Log, log, null, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(exception.Message, "KSR Support", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenTestSupportDialog(SupportReportType type, string sourcePath, string? saveName, bool persistentOnly)
    {
        var dialog = new SupportReportWindow(type, sourcePath, saveName, persistentOnly, useSelectedCampaign: false)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private async void UpgradeTestKsp_Click(object sender, RoutedEventArgs e) =>
        await MaintainTestKspAsync(UpdatePolicy.VerifyAllFiles);

    private async void RepairTestKsp_Click(object sender, RoutedEventArgs e)
    {
        if (!HasModuleAccess) return;
        if (MessageBox.Show("Reinstall all official KSR game components in the selected test installation?",
                "Repair test game", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await MaintainTestKspAsync(UpdatePolicy.ReinstallAll);
    }

    private async Task MaintainTestKspAsync(UpdatePolicy policy)
    {
        if (!HasModuleAccess || _maintenanceInProgress || string.IsNullOrWhiteSpace(_testKspRoot) || !UpgradeTestKspButton.IsEnabled) return;
        if (Process.GetProcessesByName("KSP_x64").Length > 0)
        {
            MessageBox.Show("Close KSP before updating the test installation.",
                "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!await CheckAccessAsync()) return;
        _maintenanceInProgress = true;
        BrowseTestKspButton.IsEnabled = false;
        ClearTestKspButton.IsEnabled = false;
        UpgradeTestKspButton.IsEnabled = false;
        RepairTestKspButton.IsEnabled = false;
        LaunchTestKspButton.IsEnabled = false;
        SendTestSaveButton.IsEnabled = false;
        SendTestLogButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        TestKspMaintenanceProgress.Visibility = Visibility.Visible;
        TestKspMaintenanceProgress.IsIndeterminate = true;
        TestKspMaintenanceStatus.Foreground = (Brush)FindResource("MutedBrush");
        TestKspMaintenanceStatus.Text = "Checking official KSR release...";
        try
        {
            using var privateRelease = new PrivateBetaReleaseClient(ServerUrlTextBox.Text.Trim(), ModuleId, LauncherSession.AccessToken!);
            var release = await privateRelease.ResolveAsync(_operationCancellation.Token);
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
            var result = await new UpdateEngine(new PackageService(privateRelease.Http)).RunAsync(release.Manifest,
                new LauncherLocations(_testKspRoot, launcherData), release.AssetsBaseUrl,
                true, policy, progress, _operationCancellation.Token);
            TestKspMaintenanceProgress.IsIndeterminate = false;
            TestKspMaintenanceProgress.Value = 100;
            TestKspMaintenanceStatus.Foreground = (Brush)FindResource("GreenBrush");
            TestKspMaintenanceStatus.Text = result.Applied
                ? $"{(policy == UpdatePolicy.ReinstallAll ? "Repair" : "Upgrade")} complete: {result.Plan.Components.Count(item => item.NeedsUpdate)} components."
                : "All KSR game components are up to date.";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
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

    private string GetTestLauncherDataRoot(string testKspRoot)
    {
        var launcherData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KSRLauncher");
        if (IsFullMode) return TestInstallationPaths.GetLauncherDataRoot(testKspRoot, LauncherSettingsStore.LoadKspRoot(), launcherData);
        var pathKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(testKspRoot).ToUpperInvariant())))[..16];
        return Path.Combine(launcherData, "beta", ModuleId, pathKey);
    }

    private async void LaunchTestKsp_Click(object sender, RoutedEventArgs e)
    {
        if (!HasModuleAccess || string.IsNullOrWhiteSpace(_testKspRoot) || !LaunchTestKspButton.IsEnabled) return;
        if (Process.GetProcessesByName("KSP_x64").Length > 0)
        {
            MessageBox.Show("Close the running KSP game before starting the test installation.",
                "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!await CheckAccessAsync() || !TrySaveSettings()) return;
        if (!IsFullMode)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(_testKspRoot, "KSP_x64.exe")) { WorkingDirectory = _testKspRoot, UseShellExecute = true });
                DialogResult = true;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show("The beta installation could not be launched.\n\n" + exception.Message,
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }
        if (!LauncherSession.IsAuthenticated || string.IsNullOrWhiteSpace(LauncherSession.AccessToken))
        {
            MessageBox.Show("Sign in before launching the test game so it can connect to test1.",
                "KSR Test Game", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!string.Equals(ServerUrl, LauncherSession.ServerUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Sign in to the selected KSR server before launching the test game.",
                "KSR Test Game", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var launchingUserId = LauncherSession.UserId;
            GameLoggerConfiguration.Clear(_testKspRoot);
            await LauncherSession.EnsureFreshAccessTokenAsync(ServerUrl!);
            var ticket = await new KsrPlatformClient().GetGameTicketAsync(
                ServerUrl!, LauncherSession.AccessToken, "test1");
            var client = new KsrPlatformClient();
            var tickets = new List<KsrGameTicket> { ticket };
            var saveFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var campaigns = await client.GetCampaignsAsync(ServerUrl!, LauncherSession.AccessToken);
            foreach (var campaign in campaigns.Where(c => string.Equals(c.Status, "active", StringComparison.OrdinalIgnoreCase) &&
                                                          !string.Equals(c.CampaignCode, "test1", StringComparison.OrdinalIgnoreCase)))
            {
                tickets.Add(await client.GetGameTicketAsync(ServerUrl!, LauncherSession.AccessToken, campaign.CampaignCode));
                saveFolders[campaign.CampaignCode] = CampaignSaveNaming.CreateStartFolderName(campaign.Name);
            }
            if (!LauncherSession.IsAuthenticated || launchingUserId <= 0 || LauncherSession.UserId != launchingUserId)
                throw new InvalidOperationException("Sign in again before launching the test game.");
            GameLoggerConfiguration.WriteTickets(_testKspRoot, ServerUrl!, LauncherSession.UserId, tickets, saveFolders);
            GameLoggerConfiguration.Write(_testKspRoot, ServerUrl!, "test1", ticket.Token);
            var executable = Path.Combine(_testKspRoot, "KSP_x64.exe");
            Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = _testKspRoot,
                UseShellExecute = true
            });
            DialogResult = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or HttpRequestException or KsrApiException or InvalidOperationException)
        {
            GameLoggerConfiguration.Clear(_testKspRoot);
            GameLoggerConfiguration.ClearTickets(_testKspRoot);
            MessageBox.Show($"The test game could not be launched.\n\n{exception.Message}",
                "KSR Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
