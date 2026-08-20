using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class MainWindow : Window
{
    private string? _kspRoot;

    public MainWindow()
    {
        InitializeComponent();
        _kspRoot = Environment.GetEnvironmentVariable("KSR_KSP_ROOT");
        UpdateSessionVisuals();
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

    private void SignIn_Click(object sender, RoutedEventArgs e)
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
        MessageBox.Show(
            "The server URL is configured, but the authentication endpoint has not been connected to this build yet.",
            "KSR Account", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        LauncherSession.AccessToken = null;
        LoginPasswordBox.Clear();
        UpdateSessionVisuals();
    }

    private void CreateAccount_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            "Account creation will become available when the KSR server API is connected.",
            "KSR Account", MessageBoxButton.OK, MessageBoxImage.Information);

    private void UpdateSessionVisuals()
    {
        var signedIn = LauncherSession.IsAuthenticated;
        AccountPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        LoginPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        CreateAccountButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountNameText.Text = LauncherSession.Username;

        var serverConfigured = !string.IsNullOrWhiteSpace(LauncherSession.ServerUrl);
        var statusBrush = (System.Windows.Media.Brush)FindResource(serverConfigured ? "GreenBrush" : "OrangeBrush");
        ServerStatusDot.Foreground = statusBrush;
        ServerStatusText.Foreground = statusBrush;
        ServerStatusText.Text = serverConfigured ? "SERVER CONFIGURED" : "SERVER SETUP PENDING";
        LoginServerStatusDot.Foreground = statusBrush;
        LoginServerStatusText.Foreground = statusBrush;
        LoginServerStatusText.Text = serverConfigured ? "CONFIGURED" : "SETUP PENDING";
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
    public static string? ServerUrl { get; set; }
    public static string? AccessToken { get; set; }
    public static string? CampaignCode { get; set; } = "KSR-000042-20260819-0015";
    public static string? CampaignName { get; set; } = "Space Race 2026";
    public static bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken);
}
