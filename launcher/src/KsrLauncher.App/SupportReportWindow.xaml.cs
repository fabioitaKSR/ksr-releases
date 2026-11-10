using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class SupportReportWindow : Window
{
    private readonly SupportReportType _type;
    private readonly string _sourcePath;
    private readonly string? _saveName;
    private readonly bool _persistentOnly;
    private readonly bool _useSelectedCampaign;
    private readonly string? _csvKspRoot;
    private readonly string? _saveKspRoot;
    private readonly string? _playerLogPath;
    private bool _busy;

    public SupportReportWindow(SupportReportType type, string sourcePath, string? saveName,
        bool persistentOnly = false, bool useSelectedCampaign = true, string? csvKspRoot = null, string? saveKspRoot = null, string? playerLogPath = null)
    {
        if (playerLogPath is not null) PlayerLogSupport.Validate(playerLogPath, LauncherSession.Username);
        InitializeComponent();
        _playerLogPath = playerLogPath;
        _type = type;
        _csvKspRoot = csvKspRoot;
        _saveKspRoot = saveKspRoot;
        _sourcePath = sourcePath;
        _saveName = saveName;
        _persistentOnly = persistentOnly;
        _useSelectedCampaign = useSelectedCampaign;

        ReportTypeText.Text = playerLogPath is not null ? "SEND PLAYER.LOG" : type == SupportReportType.Csv ? "SEND LOGGER CSV" : type == SupportReportType.Log ? "SEND KSP LOG" : "SEND KSP SAVE";
        PlayerText.Text = LauncherSession.Username;
        CampaignText.Text = !useSelectedCampaign || string.IsNullOrWhiteSpace(LauncherSession.CampaignCode)
            ? "No campaign selected"
            : $"{LauncherSession.CampaignName ?? "Unnamed campaign"} · {LauncherSession.CampaignCode}";
        SourceText.Text = sourcePath;
        IncludedFilesText.Text = type == SupportReportType.Csv
            ? string.Join(" · ", LoggerCsvSupport.Files(csvKspRoot!, sourcePath).Select(Path.GetFileName)) + " · report.txt · manifest.json"
            : type == SupportReportType.Log
            ? "KSP.log · report.txt · manifest.json"
            : saveKspRoot is not null
                ? $"save/{Path.GetFileName(sourcePath)} (game: {saveName ?? "unnamed"}) · report.txt · manifest.json"
            : persistentOnly
                ? $"save/persistent.sfs (selected save: {saveName ?? "unnamed"}) · report.txt · manifest.json"
                : $"save/ (selected save: {saveName ?? "unnamed"}) · report.txt · manifest.json";
        SaveWarningText.Visibility = type is SupportReportType.Save or SupportReportType.Csv ? Visibility.Visible : Visibility.Collapsed;
        if (type == SupportReportType.Csv)
            SaveWarningText.Text = "Return to the KSP main menu before sending CSV files, so the logger has finished writing. Original files are kept unchanged.";
        if (playerLogPath is not null)
        {
            SourceText.Text = $"{sourcePath}\n{playerLogPath}\nPlayer.log last modified (local): {File.GetLastWriteTime(playerLogPath):yyyy-MM-dd HH:mm:ss}";
            IncludedFilesText.Text = "KSP.log · Player.log · report.txt · manifest.json";
            SaveWarningText.Visibility = Visibility.Visible;
            SaveWarningText.Text = "Player.log belongs to the last KSP session for this Windows user and is shared across installations. Close KSP and send it before starting the game again. Check the path and date above before sending. It may contain local paths and diagnostic details.";
        }
        DescriptionTextBox.Focus();
    }

    private void InputChanged(object sender, RoutedEventArgs e) => UpdateSendState();

    private void UpdateSendState()
    {
        if (SendButton is null || DescriptionTextBox is null || ConsentCheckBox is null || StatusText is null) return;
        SendButton.IsEnabled = !_busy
            && DescriptionTextBox.Text.Trim().Length >= 10
            && ConsentCheckBox.IsChecked == true;
        if (!_busy)
            StatusText.Text = SendButton.IsEnabled
                ? "Ready to create and send the support package."
                : "Enter at least 10 characters and confirm consent.";
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Creating a secure support package…");
        SupportReportPackage? package = null;

        try
        {
            var queue = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KSRLauncher", "SupportQueue");
            var request = new SupportReportRequest(
                _type,
                _sourcePath,
                DescriptionTextBox.Text,
                LauncherSession.Username,
                _useSelectedCampaign ? LauncherSession.CampaignCode : null,
                _useSelectedCampaign ? LauncherSession.CampaignName : null,
                _saveName,
                GetLauncherVersion(),
                DetectKspVersion(_sourcePath),
                _persistentOnly,
                _csvKspRoot,
                _saveKspRoot,
                _playerLogPath);

            package = await Task.Run(() => new SupportReportPackager().CreateAsync(request, queue));
            if (string.IsNullOrWhiteSpace(LauncherSession.ServerUrl) || string.IsNullOrWhiteSpace(LauncherSession.AccessToken))
            {
                MessageBox.Show(
                    $"Support package prepared and queued locally:\n\n{package.FileName}\n\nIt will be uploadable after KSR server sign-in is connected.",
                    "KSR Support", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                return;
            }

            SetBusy(true, "Uploading the support package to the KSR server…");
            var accessToken = LauncherSession.AccessToken;
            if (LauncherSession.AccessTokenExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
                accessToken = await RefreshAccessTokenAsync(CancellationToken.None);
            var result = await new SupportReportUploader().UploadAsync(
                LauncherSession.ServerUrl,
                accessToken!,
                package,
                RefreshAccessTokenAsync);
            File.Delete(package.FilePath);
            MessageBox.Show(
                $"Report received successfully.\n\nReport ID: {result.ReportId}",
                "KSR Support", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            var retained = package is { } retainedPackage && File.Exists(retainedPackage.FilePath)
                ? $"\n\nThe prepared package was retained for retry:\n{retainedPackage.FilePath}"
                : string.Empty;
            MessageBox.Show(
                $"The report could not be sent.\n\n{exception.Message}{retained}",
                "KSR Support", MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false, package is null ? "Correct the problem and try again." : "Package retained. You can try again.");
        }
    }

    private static async Task<string> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        var serverUrl = LauncherSession.ServerUrl;
        var refreshToken = LauncherSession.RefreshToken;
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException("Your KSR session has expired. Sign in again before sending the report.");

        var session = await new KsrPlatformClient().RefreshAsync(serverUrl, refreshToken, cancellationToken);
        LauncherSession.Username = session.User.Username;
        LauncherSession.AccessToken = session.AccessToken;
        LauncherSession.RefreshToken = session.RefreshToken;
        LauncherSession.AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresIn);

        try
        {
            var remembered = LauncherCredentialStore.Load();
            if (remembered is not null &&
                string.Equals(remembered.ServerUrl.TrimEnd('/'), serverUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                LauncherCredentialStore.Save(new RememberedSession(
                    session.User.Username, serverUrl, session.RefreshToken));
            }
        }
        catch
        {
            // The renewed in-memory session remains valid even if Windows Credential Manager is unavailable.
        }

        return session.AccessToken;
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        DescriptionTextBox.IsEnabled = !busy;
        ConsentCheckBox.IsEnabled = !busy;
        ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
        UpdateSendState();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) DialogResult = false;
    }

    private static string GetLauncherVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static string DetectKspVersion(string sourcePath)
    {
        var current = File.Exists(sourcePath) ? Path.GetDirectoryName(sourcePath) : sourcePath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var executable = Path.Combine(current, "KSP_x64.exe");
            if (File.Exists(executable))
            {
                var version = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
                return string.IsNullOrWhiteSpace(version) ? "Unknown" : version;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return "Unknown";
    }
}
