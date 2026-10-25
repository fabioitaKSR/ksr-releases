using System.Diagnostics;
using System.Windows;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class DeleteAccountWindow : Window
{
    private readonly KsrAccountDeletionPreview _preview;
    private readonly string _serverUrl;
    private readonly string _accessToken;
    private bool _busy;
    public bool AccountDeleted { get; private set; }

    public DeleteAccountWindow(KsrAccountDeletionPreview preview, string serverUrl, string accessToken)
    {
        _preview = preview; _serverUrl = serverUrl; _accessToken = accessToken;
        InitializeComponent();
        SummaryText.Text = $"Account: {preview.Username}\nCampaign participations: {preview.ParticipationCount}\n" +
            $"Linked gameplay records: {preview.LinkedRecordCount}\nSupport reports: {preview.SupportReportCount}\n" +
            $"Campaigns to close: {(preview.CampaignsToClose.Length == 0 ? "None" : string.Join(", ", preview.CampaignsToClose))}";
        StatusText.Text = preview.CanDelete ? "This preview expires in 10 minutes. Close KSP before continuing." : string.Join("\n", preview.Blockers);
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) => CurrentPasswordBox.Clear();
    }

    private void Confirmation_Changed(object sender, RoutedEventArgs e)
    {
        if (DeleteButton is null || UsernameTextBox is null || CurrentPasswordBox is null || UnderstandCheckBox is null) return;
        DeleteButton.IsEnabled = !_busy && _preview.CanDelete && UnderstandCheckBox.IsChecked == true &&
            UsernameTextBox.Text == _preview.Username && CurrentPasswordBox.Password.Length > 0;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!DeleteButton.IsEnabled || _busy) return;
        if (Process.GetProcessesByName("KSP_x64").Length > 0 || Process.GetProcessesByName("KSP").Length > 0)
        {
            StatusText.Text = "Close KSP before deleting your account."; return;
        }
        _busy = true; DeleteButton.IsEnabled = CancelButton.IsEnabled = UsernameTextBox.IsEnabled =
            CurrentPasswordBox.IsEnabled = UnderstandCheckBox.IsEnabled = false;
        StatusText.Text = "Processing account deletion. Keep this window open…";
        try
        {
            var result = await new KsrPlatformClient().DeleteAccountAsync(_serverUrl, _accessToken, _preview,
                UsernameTextBox.Text, CurrentPasswordBox.Password);
            AccountDeleted = true;
            _busy = false;
            MessageBox.Show(this, result.ProcessingComplete
                ? "Your account has been deleted. A confirmation email has been queued."
                : "Your account access has ended. Attachment cleanup is pending; an email will be queued when it completes.",
                "KSR account", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (KsrApiException exception)
        {
            StatusText.Text = exception.Code switch {
                "reauthentication_failed" => "Username or current password did not match. Your account was not deleted.",
                "preview_expired_or_changed" => "Your account details changed or this preview expired. Close this window and load a new preview.",
                _ => "Deletion could not be completed. Reload the preview or contact support@kerbalspacerace.net." };
        }
        catch (Exception)
        {
            StatusText.Text = "The outcome could not be confirmed. Check your email before retrying, or contact support@kerbalspacerace.net.";
        }
        finally
        {
            CurrentPasswordBox.Clear(); _busy = false;
            CancelButton.IsEnabled = UsernameTextBox.IsEnabled = CurrentPasswordBox.IsEnabled = UnderstandCheckBox.IsEnabled = true;
            Confirmation_Changed(this, new RoutedEventArgs());
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
