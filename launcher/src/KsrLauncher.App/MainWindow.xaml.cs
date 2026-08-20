using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class MainWindow : Window
{
    private string? _kspRoot;
    private readonly ObservableCollection<CampaignListItem> _campaigns = [];
    private readonly KsrPlatformClient _platformClient = new();

    public MainWindow()
    {
        InitializeComponent();
        CampaignsList.ItemsSource = _campaigns;
        LauncherSession.ServerUrl = LauncherSettingsStore.LoadServerUrl() ?? LauncherSession.ServerUrl;
        _kspRoot = Environment.GetEnvironmentVariable("KSR_KSP_ROOT");
        UpdateSessionVisuals();
        RefreshCampaignState();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) => await RefreshServerStatusAsync();

    private void PlayerTab_Click(object sender, RoutedEventArgs e)
    {
        PlayerArea.Visibility = Visibility.Visible;
        AdminArea.Visibility = Visibility.Collapsed;
        PlayerTabButton.Foreground = (System.Windows.Media.Brush)FindResource("OrangeBrush");
        AdminTabButton.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private void AdminTab_Click(object sender, RoutedEventArgs e)
    {
        PlayerArea.Visibility = Visibility.Collapsed;
        AdminArea.Visibility = Visibility.Visible;
        PlayerTabButton.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        AdminTabButton.Foreground = (System.Windows.Media.Brush)FindResource("OrangeBrush");
    }

    private void CampaignsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyCampaignSelection(CampaignsList.SelectedItem as CampaignListItem);
    }

    private void ApplyCampaignSelection(CampaignListItem? campaign)
    {
        LauncherSession.CampaignCode = campaign?.CampaignCode;
        LauncherSession.CampaignName = campaign?.Name;
        ActiveCampaignText.Text = campaign?.Name?.ToUpperInvariant() ?? "NO CAMPAIGN SELECTED";
        SelectedCampaignText.Text = campaign is null
            ? "NO CAMPAIGN SELECTED"
            : $"{campaign.Name}  ·  {campaign.Nation}  ·  {campaign.Role}";
        SelectedCampaignText.Foreground = (System.Windows.Media.Brush)FindResource(campaign is null ? "MutedBrush" : "ForegroundBrush");
        OpenCampaignButton.IsEnabled = campaign is not null;
        DownloadMasterSaveButton.IsEnabled = campaign?.MasterSaveAvailable == true;
        MasterSaveStatusText.Text = campaign is null
            ? "Select a campaign to check its Master Save."
            : campaign.MasterSaveAvailable
                ? "The selected campaign Master Save is available and verified."
                : "No Master Save is currently available for the selected campaign.";
    }

    internal void ReplaceCampaigns(IEnumerable<CampaignListItem> campaigns)
    {
        CampaignsList.SelectedItem = null;
        _campaigns.Clear();
        foreach (var campaign in campaigns) _campaigns.Add(campaign);
        RefreshCampaignState();
    }

    private void RefreshCampaignState()
    {
        EmptyCampaignsPanel.Visibility = _campaigns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CampaignsList.Visibility = _campaigns.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_campaigns.Count == 0) ApplyCampaignSelection(null);
    }

    private void SendLog_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureKspRoot()) return;
        var log = Path.Combine(_kspRoot!, "KSP.log");
        if (!File.Exists(log))
        {
            MessageBox.Show("KSP.log was not found in the selected KSP folder.", "KSR Support", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OpenSupportDialog(SupportReportType.Log, log, null);
    }

    private void SendSave_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureKspRoot()) return;
        var picker = new OpenFolderDialog
        {
            Title = "Select the KSP save folder to send",
            InitialDirectory = Path.Combine(_kspRoot!, "saves"),
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(picker.FolderName, "persistent.sfs")))
        {
            MessageBox.Show("The selected folder is not a KSP save. persistent.sfs is missing.", "KSR Support", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OpenSupportDialog(SupportReportType.Save, picker.FolderName, Path.GetFileName(picker.FolderName));
    }

    private void OpenSupportDialog(SupportReportType type, string sourcePath, string? saveName)
    {
        var dialog = new SupportReportWindow(type, sourcePath, saveName)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void LaunchKsp_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureKspRoot()) return;
        var executable = Path.Combine(_kspRoot!, "KSP_x64.exe");
        Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = _kspRoot!, UseShellExecute = true });
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var username = LoginUsernameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(username) || LoginPasswordBox.Password.Length == 0)
        {
            MessageBox.Show("Enter your KSR username and password.", "KSR Account", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(LauncherSession.ServerUrl))
        {
            MessageBox.Show(
                "KSR server authentication is not connected yet. The account interface is ready and will be enabled when the server API is available.",
                "KSR Account", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetLoginBusy(true);
        try
        {
            var session = await _platformClient.LoginAsync(
                LauncherSession.ServerUrl,
                username,
                LoginPasswordBox.Password);
            LauncherSession.Username = session.User.Username;
            LauncherSession.AccessToken = session.AccessToken;
            LauncherSession.RefreshToken = session.RefreshToken;
            LauncherSession.AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresIn);
            LoginPasswordBox.Clear();

            var campaigns = await _platformClient.GetCampaignsAsync(LauncherSession.ServerUrl, session.AccessToken);
            ReplaceCampaigns(campaigns.Select(campaign => new CampaignListItem(
                campaign.CampaignCode,
                campaign.Name,
                campaign.Role.ToUpperInvariant(),
                campaign.NationId ?? "NOT SELECTED",
                campaign.Status.ToUpperInvariant(),
                !string.IsNullOrWhiteSpace(campaign.MasterSaveSha256))));
            UpdateSessionVisuals();
        }
        catch (KsrApiException exception)
        {
            MessageBox.Show(exception.Message, "KSR Sign In", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (HttpRequestException)
        {
            MessageBox.Show(
                "The KSR server could not be reached. Check the server URL and network connection.",
                "KSR Sign In", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "KSR Sign In", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetLoginBusy(false);
        }
    }

    private void SetLoginBusy(bool busy)
    {
        LoginUsernameTextBox.IsEnabled = !busy;
        LoginPasswordBox.IsEnabled = !busy;
        SignInButton.IsEnabled = !busy;
        SignInButton.Content = busy ? "SIGNING IN…" : "SIGN IN";
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        LauncherSession.AccessToken = null;
        LauncherSession.RefreshToken = null;
        LauncherSession.AccessTokenExpiresAtUtc = null;
        _campaigns.Clear();
        RefreshCampaignState();
        LoginPasswordBox.Clear();
        UpdateSessionVisuals();
    }

    private void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureServerConfigured()) return;
        var dialog = new CreateAccountWindow(_platformClient, LauncherSession.ServerUrl!) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.CreatedUsername))
        {
            LoginUsernameTextBox.Text = dialog.CreatedUsername;
            LoginPasswordBox.Focus();
        }
    }

    private void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureServerConfigured()) return;
        new PasswordRecoveryWindow(_platformClient, LauncherSession.ServerUrl!) { Owner = this }.ShowDialog();
    }

    private bool EnsureServerConfigured()
    {
        if (!string.IsNullOrWhiteSpace(LauncherSession.ServerUrl)) return true;
        MessageBox.Show(
            "Configure the KSR server URL in Settings before using account services.",
            "KSR Account", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ServerSettingsWindow(LauncherSession.ServerUrl) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        LauncherSession.ServerUrl = dialog.ServerUrl;
        UpdateSessionVisuals();
        await RefreshServerStatusAsync();
    }

    private void UpdateSessionVisuals()
    {
        var signedIn = LauncherSession.IsAuthenticated;
        AccountPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        LoginPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        CreateAccountButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountNameText.Text = LauncherSession.Username;

        var serverConfigured = !string.IsNullOrWhiteSpace(LauncherSession.ServerUrl);
        var statusBrush = (System.Windows.Media.Brush)FindResource("OrangeBrush");
        ServerStatusDot.Foreground = statusBrush;
        ServerStatusText.Foreground = statusBrush;
        ServerStatusText.Text = serverConfigured ? "CHECKING SERVER…" : "SERVER SETUP PENDING";
        LoginServerStatusDot.Foreground = statusBrush;
        LoginServerStatusText.Foreground = statusBrush;
        LoginServerStatusText.Text = serverConfigured ? "CHECKING…" : "SETUP PENDING";
    }

    private async Task RefreshServerStatusAsync()
    {
        if (string.IsNullOrWhiteSpace(LauncherSession.ServerUrl)) return;
        try
        {
            await _platformClient.GetHealthAsync(LauncherSession.ServerUrl);
            SetServerStatus("SERVER ONLINE", "ONLINE", "GreenBrush");
        }
        catch
        {
            SetServerStatus("SERVER OFFLINE", "OFFLINE", "MutedBrush");
        }
    }

    private void SetServerStatus(string headerText, string loginText, string brushKey)
    {
        var brush = (System.Windows.Media.Brush)FindResource(brushKey);
        ServerStatusDot.Foreground = brush;
        ServerStatusText.Foreground = brush;
        ServerStatusText.Text = headerText;
        LoginServerStatusDot.Foreground = brush;
        LoginServerStatusText.Foreground = brush;
        LoginServerStatusText.Text = loginText;
    }

    private bool EnsureKspRoot()
    {
        if (!string.IsNullOrWhiteSpace(_kspRoot) && File.Exists(Path.Combine(_kspRoot, "KSP_x64.exe"))) return true;
        var picker = new OpenFolderDialog { Title = "Select the Kerbal Space Program folder", Multiselect = false };
        if (picker.ShowDialog(this) != true) return false;
        if (!File.Exists(Path.Combine(picker.FolderName, "KSP_x64.exe")))
        {
            MessageBox.Show("KSP_x64.exe was not found in the selected folder.", "KSR Platform", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _kspRoot = picker.FolderName;
        return true;
    }
}

internal static class LauncherSession
{
    public static string Username { get; set; } = "PLAYER";
    public static string? ServerUrl { get; set; } =
        Environment.GetEnvironmentVariable("KSR_SERVER_URL") ?? KsrPlatformClient.ProductionServerUrl;
    public static string? AccessToken { get; set; }
    public static string? RefreshToken { get; set; }
    public static DateTimeOffset? AccessTokenExpiresAtUtc { get; set; }
    public static string? CampaignCode { get; set; }
    public static string? CampaignName { get; set; }
    public static bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken);
}

internal sealed record CampaignListItem(
    string CampaignCode,
    string Name,
    string Role,
    string Nation,
    string Status,
    bool MasterSaveAvailable)
{
    public string RoleColor => string.Equals(Role, "ADMIN", StringComparison.OrdinalIgnoreCase) ? "#FFF57C00" : "#FF4AB8F1";
    public string StatusColor => string.Equals(Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) ? "#FF80D420" : "#FF9BA0A3";
}
