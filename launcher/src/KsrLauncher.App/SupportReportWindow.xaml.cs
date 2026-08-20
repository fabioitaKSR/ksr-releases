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
    private bool _busy;

    public SupportReportWindow(SupportReportType type, string sourcePath, string? saveName)
    {
        InitializeComponent();
        _type = type;
        _sourcePath = sourcePath;
        _saveName = saveName;

        ReportTypeText.Text = type == SupportReportType.Log ? "SEND KSP LOG" : "SEND KSP SAVE";
        PlayerText.Text = LauncherSession.Username;
        CampaignText.Text = string.IsNullOrWhiteSpace(LauncherSession.CampaignCode)
            ? "No campaign selected"
            : $"{LauncherSession.CampaignName ?? "Unnamed campaign"} · {LauncherSession.CampaignCode}";
        SourceText.Text = sourcePath;
        IncludedFilesText.Text = type == SupportReportType.Log
            ? "KSP.log · report.txt · manifest.json"
            : $"save/ (selected save: {saveName ?? "unnamed"}) · report.txt · manifest.json";
        SaveWarningText.Visibility = type == SupportReportType.Save ? Visibility.Visible : Visibility.Collapsed;
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
                LauncherSession.CampaignCode,
                LauncherSession.CampaignName,
                _saveName,
                GetLauncherVersion(),
                DetectKspVersion(_sourcePath));

            package = await new SupportReportPackager().CreateAsync(request, queue);
            if (string.IsNullOrWhiteSpace(LauncherSession.ServerUrl) || string.IsNullOrWhiteSpace(LauncherSession.AccessToken))
            {
                MessageBox.Show(
                    $"Support package prepared and queued locally:\n\n{package.FileName}\n\nIt will be uploadable after KSR server sign-in is connected.",
                    "KSR Support", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                return;
            }

            SetBusy(true, "Uploading the support package to the KSR server…");
            var result = await new SupportReportUploader().UploadAsync(
                LauncherSession.ServerUrl,
                LauncherSession.AccessToken,
                package);
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
