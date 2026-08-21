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
    private string? _referenceSavePath;
    private readonly ObservableCollection<CampaignListItem> _campaigns = [];
    private readonly KsrPlatformClient _platformClient = new();
    private readonly Dictionary<string, CampaignBaselinePackage> _localBaselines = new(StringComparer.OrdinalIgnoreCase);
    private CampaignComplianceResult? _lastCompliance;
    private RememberedSession? _rememberedSession;

    public MainWindow()
    {
        InitializeComponent();
        CampaignsList.ItemsSource = _campaigns;
        LauncherSession.ServerUrl = LauncherSettingsStore.LoadServerUrl() ?? LauncherSession.ServerUrl;
        try
        {
            _rememberedSession = LauncherCredentialStore.Load();
            if (_rememberedSession is not null)
            {
                LoginUsernameTextBox.Text = _rememberedSession.Username;
                RememberMeCheckBox.IsChecked = true;
            }
        }
        catch
        {
            _rememberedSession = null;
        }
        _kspRoot = Environment.GetEnvironmentVariable("KSR_KSP_ROOT");
        UpdateSessionVisuals();
        RefreshCampaignState();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshServerStatusAsync();
        await TryRestoreSessionAsync();
    }

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

    private void BrowseReferenceSave_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Select the Career or Science save to use as the campaign starting point",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(_kspRoot)) picker.InitialDirectory = Path.Combine(_kspRoot, "saves");
        if (picker.ShowDialog(this) != true) return;

        KspCareerSave selection;
        try
        {
            selection = KspCareerSaveLocator.Resolve(picker.FolderName);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _referenceSavePath = null;
            ReferenceSavePathTextBox.Text = "No valid Career or Science save selected";
            ResolvedKspPathText.Text = "Not resolved";
            CampaignCreationStatusText.Text = exception.Message;
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            UpdateCreateRaceState();
            return;
        }

        _referenceSavePath = selection.SavePath;
        _kspRoot = selection.KspRoot;
        ReferenceSavePathTextBox.Text = _referenceSavePath;
        ReferenceSavePathTextBox.Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush");
        ResolvedKspPathText.Text = _kspRoot;
        ResolvedKspPathText.Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush");
        CampaignCreationStatusText.Text = "Career or Science save validated. Enter a campaign name to continue.";
        CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
        UpdateCreateRaceState();
    }

    private void CampaignCreationInput_Changed(object sender, TextChangedEventArgs e) => UpdateCreateRaceState();

    private void UpdateCreateRaceState()
    {
        if (!IsInitialized) return;
        CreateRaceButton.IsEnabled = LauncherSession.IsAuthenticated &&
            !string.IsNullOrWhiteSpace(_referenceSavePath) &&
            !string.IsNullOrWhiteSpace(CampaignNameTextBox.Text);
    }

    private async void CreateRace_Click(object sender, RoutedEventArgs e)
    {
        if (_referenceSavePath is null) return;
        CreateRaceButton.IsEnabled = false;
        CampaignNameTextBox.IsEnabled = false;
        CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("OrangeBrush");
        var progress = new Progress<BaselineProgress>(value =>
            CampaignCreationStatusText.Text = value.Total > 0
                ? $"{value.Stage}: {value.Completed}/{value.Total}  {value.CurrentPath}"
                : value.Stage);
        try
        {
            var drafts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KSRLauncher", "CampaignDrafts");
            var package = await new CampaignBaselineBuilder().CreateAsync(
                CampaignNameTextBox.Text,
                _referenceSavePath,
                drafts,
                IgnoredGameDataFoldersTextBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
                progress);
            var draftCode = $"DRAFT-{package.Manifest.CreatedAtUtc:yyyyMMdd-HHmmss}";
            _localBaselines[draftCode] = package;
            var item = new CampaignListItem(draftCode, package.Manifest.CampaignName, "ADMIN", "NOT SELECTED", "DRAFT", true);
            _campaigns.Add(item);
            RefreshCampaignState();
            CampaignsList.SelectedItem = item;
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
            CampaignCreationStatusText.Text = $"Baseline ready: {package.Manifest.GameDataFiles.Count} files captured; {package.Manifest.IgnoredGameDataFolders.Count} GameData folder(s) ignored.";
            MessageBox.Show(
                $"The local campaign baseline is ready.\n\nMaster Save: {package.MasterSavePath}\nBaseline: {package.ManifestPath}\n\nIt will be uploaded when the server snapshot endpoint is connected.",
                "KSR Race Baseline", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            CampaignCreationStatusText.Text = exception.Message;
        }
        finally
        {
            CampaignNameTextBox.IsEnabled = true;
            UpdateCreateRaceState();
        }
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
        var hasBaseline = campaign is not null && _localBaselines.ContainsKey(campaign.CampaignCode);
        CheckInstallationButton.IsEnabled = hasBaseline;
        ViewIgnoredModsButton.IsEnabled = hasBaseline;
        CampaignComplianceStatusText.Text = hasBaseline
            ? "Baseline available. Run the installation check before launching the campaign."
            : "The campaign baseline has not been downloaded yet.";
        CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        _lastCompliance = null;
        LaunchKspButton.IsEnabled = !hasBaseline;
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
        if (CampaignsList.SelectedItem is CampaignListItem campaign &&
            _localBaselines.ContainsKey(campaign.CampaignCode) && _lastCompliance?.ReadyToLaunch != true)
        {
            MessageBox.Show("Check this campaign installation before launching KSP.",
                "KSR Campaign", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!EnsureKspRoot()) return;
        var executable = Path.Combine(_kspRoot!, "KSP_x64.exe");
        Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = _kspRoot!, UseShellExecute = true });
    }

    private async void CheckInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (CampaignsList.SelectedItem is not CampaignListItem campaign ||
            !_localBaselines.TryGetValue(campaign.CampaignCode, out var package)) return;
        if (!EnsureKspRoot()) return;
        var picker = new OpenFolderDialog
        {
            Title = "Select the installed campaign save to verify",
            InitialDirectory = Path.Combine(_kspRoot!, "saves"),
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;

        CheckInstallationButton.IsEnabled = false;
        CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("OrangeBrush");
        var progress = new Progress<BaselineProgress>(value =>
            CampaignComplianceStatusText.Text = $"{value.Stage}: {value.Completed}/{value.Total}");
        try
        {
            _lastCompliance = await new CampaignBaselineComparer().CompareAsync(
                package.Manifest, picker.FolderName, progress);
            _kspRoot = KspCareerSaveLocator.Resolve(picker.FolderName).KspRoot;
            if (_lastCompliance.ReadyToLaunch)
            {
                CampaignComplianceStatusText.Text = "READY TO LAUNCH — save settings and GameData match the campaign baseline.";
                CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
                LaunchKspButton.IsEnabled = true;
            }
            else
            {
                var modDifferences = _lastCompliance.Differences.Count(item => item.Area == BaselineDifferenceArea.GameData);
                var settingDifferences = _lastCompliance.Differences.Count - modDifferences;
                CampaignComplianceStatusText.Text = $"CAMPAIGN NOT READY — {modDifferences} mod/file differences, {settingDifferences} setting differences.";
                CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
                LaunchKspButton.IsEnabled = false;
                ShowComplianceDifferences(_lastCompliance);
                if (modDifferences == 0 && settingDifferences > 0 && MessageBox.Show(
                        "Campaign difficulty or mod settings differ. Align them with the official baseline?\n\nA backup will be created first. Your progress, vessels and contracts will not be replaced.",
                        "Align Campaign Settings", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    var aligned = await new CampaignSettingsAligner().AlignAsync(package, picker.FolderName, _lastCompliance);
                    _lastCompliance = await new CampaignBaselineComparer().CompareAsync(
                        package.Manifest, picker.FolderName, progress);
                    if (_lastCompliance.ReadyToLaunch)
                    {
                        CampaignComplianceStatusText.Text = $"READY TO LAUNCH — {aligned.FilesUpdated} setting file(s) aligned. Backup: {aligned.BackupDirectory}";
                        CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
                        LaunchKspButton.IsEnabled = true;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            CampaignComplianceStatusText.Text = exception.Message;
            CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            LaunchKspButton.IsEnabled = false;
        }
        finally
        {
            CheckInstallationButton.IsEnabled = true;
        }
    }

    private static void ShowComplianceDifferences(CampaignComplianceResult result)
    {
        var lines = result.Differences.Take(18).Select(item => item.Kind == BaselineDifferenceKind.ValueMismatch
            ? $"{item.DisplayName}: player '{item.Actual}' / campaign '{item.Expected}'"
            : $"{item.Kind.ToString().ToUpperInvariant()}: {item.Path}");
        var more = result.Differences.Count > 18 ? $"\n…and {result.Differences.Count - 18} more differences." : string.Empty;
        MessageBox.Show(string.Join("\n", lines) + more, "Campaign Installation Differences",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ViewIgnoredMods_Click(object sender, RoutedEventArgs e)
    {
        if (CampaignsList.SelectedItem is not CampaignListItem campaign ||
            !_localBaselines.TryGetValue(campaign.CampaignCode, out var package)) return;
        var folders = package.Manifest.IgnoredGameDataFolders;
        var text = folders.Count == 0
            ? "This campaign does not ignore any GameData mod folders."
            : "These exact GameData folders are ignored by campaign installation checks:\n\n" +
              string.Join("\n", folders.Select(folder => $"• {folder}"));
        MessageBox.Show(text, "Campaign Ignored Mods — Read Only", MessageBoxButton.OK, MessageBoxImage.Information);
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
            ShowAuthFeedback("Enter your KSR username or email and password.");
            return;
        }
        if (string.IsNullOrWhiteSpace(LauncherSession.ServerUrl))
        {
            ShowAuthFeedback("The KSR server has not been configured.");
            return;
        }

        HideAuthFeedback();
        SetLoginBusy(true);
        try
        {
            var session = await _platformClient.LoginAsync(
                LauncherSession.ServerUrl,
                username,
                LoginPasswordBox.Password);
            LoginPasswordBox.Clear();
            await ApplySessionAsync(session);
            if (RememberMeCheckBox.IsChecked == true)
            {
                _rememberedSession = new RememberedSession(session.User.Username, LauncherSession.ServerUrl, session.RefreshToken);
                LauncherCredentialStore.Save(_rememberedSession);
            }
            else
            {
                _rememberedSession = null;
                LauncherCredentialStore.Clear();
            }
        }
        catch (KsrApiException exception)
        {
            ShowAuthFeedback(ApiErrorMessages.ForAuthentication(exception));
        }
        catch (HttpRequestException)
        {
            ShowAuthFeedback("The KSR server is currently unreachable. Check your connection and try again.");
        }
        catch (Exception exception)
        {
            ShowAuthFeedback(exception.Message);
        }
        finally
        {
            SetLoginBusy(false);
        }
    }

    private async Task TryRestoreSessionAsync()
    {
        if (_rememberedSession is null ||
            string.IsNullOrWhiteSpace(LauncherSession.ServerUrl) ||
            !string.Equals(_rememberedSession.ServerUrl.TrimEnd('/'), LauncherSession.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return;

        HideAuthFeedback();
        SetLoginBusy(true, "RESTORING…");
        try
        {
            KsrLoginSession session;
            try
            {
                session = await _platformClient.RefreshAsync(LauncherSession.ServerUrl, _rememberedSession.RefreshToken);
            }
            catch (KsrApiException exception)
            {
                _rememberedSession = null;
                RememberMeCheckBox.IsChecked = false;
                TryClearRememberedSession();
                ShowAuthFeedback(exception.StatusCode == 401
                    ? "Your saved session has expired. Sign in again."
                    : ApiErrorMessages.ForAuthentication(exception));
                return;
            }

            // The API rotates refresh tokens: replace the old credential immediately.
            _rememberedSession = new RememberedSession(session.User.Username, LauncherSession.ServerUrl, session.RefreshToken);
            LauncherCredentialStore.Save(_rememberedSession);
            await ApplySessionAsync(session);
        }
        catch (HttpRequestException)
        {
            ShowAuthFeedback("Your saved session could not be restored because the KSR server is unreachable.");
        }
        catch (Exception exception)
        {
            ShowAuthFeedback($"Your saved session could not be restored: {exception.Message}");
        }
        finally
        {
            SetLoginBusy(false);
        }
    }

    private async Task ApplySessionAsync(KsrLoginSession session)
    {
        var serverUrl = LauncherSession.ServerUrl ?? throw new InvalidOperationException("The KSR server has not been configured.");
        var campaigns = await _platformClient.GetCampaignsAsync(serverUrl, session.AccessToken);
        LauncherSession.Username = session.User.Username;
        LauncherSession.AccessToken = session.AccessToken;
        LauncherSession.RefreshToken = session.RefreshToken;
        LauncherSession.AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresIn);
        ReplaceCampaigns(campaigns.Select(campaign => new CampaignListItem(
            campaign.CampaignCode,
            campaign.Name,
            campaign.Role.ToUpperInvariant(),
            campaign.NationId ?? "NOT SELECTED",
            campaign.Status.ToUpperInvariant(),
            !string.IsNullOrWhiteSpace(campaign.MasterSaveSha256))));
        UpdateSessionVisuals();
        await RefreshServerStatusAsync();
    }

    private void SetLoginBusy(bool busy, string busyText = "SIGNING IN…")
    {
        LoginUsernameTextBox.IsEnabled = !busy;
        LoginPasswordBox.IsEnabled = !busy;
        RememberMeCheckBox.IsEnabled = !busy;
        SignInButton.IsEnabled = !busy;
        SignInButton.Content = busy ? busyText : "SIGN IN";
    }

    private void ShowAuthFeedback(string message)
    {
        AuthFeedbackText.Text = message;
        AuthFeedbackText.Visibility = Visibility.Visible;
    }

    private void HideAuthFeedback()
    {
        AuthFeedbackText.Text = string.Empty;
        AuthFeedbackText.Visibility = Visibility.Collapsed;
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        _rememberedSession = null;
        RememberMeCheckBox.IsChecked = false;
        TryClearRememberedSession();
        LauncherSession.AccessToken = null;
        LauncherSession.RefreshToken = null;
        LauncherSession.AccessTokenExpiresAtUtc = null;
        _campaigns.Clear();
        RefreshCampaignState();
        LoginPasswordBox.Clear();
        HideAuthFeedback();
        UpdateSessionVisuals();
    }

    private static void TryClearRememberedSession()
    {
        try { LauncherCredentialStore.Clear(); }
        catch { /* Signing out must still clear the in-memory session. */ }
    }

    private void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureServerConfigured()) return;
        var dialog = new CreateAccountWindow(_platformClient, LauncherSession.ServerUrl!) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.CreatedUsername))
        {
            LoginUsernameTextBox.Text = dialog.CreatedUsername;
            ShowAuthFeedback("Account created. Verify your email before signing in.");
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
        UpdateCreateRaceState();

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
