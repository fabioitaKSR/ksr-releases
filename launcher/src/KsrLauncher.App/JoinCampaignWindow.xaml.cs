using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class JoinCampaignWindow : Window
{
    private readonly KsrPlatformClient _client;
    private readonly string _serverUrl;
    private readonly string _accessToken;

    public KsrCampaign? JoinedCampaign { get; private set; }

    public JoinCampaignWindow(KsrPlatformClient client, string serverUrl, string accessToken)
    {
        InitializeComponent();
        _client = client;
        _serverUrl = serverUrl;
        _accessToken = accessToken;
        CampaignIdTextBox.Focus();
    }

    private void CampaignIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var value = CampaignIdTextBox.Text.Trim();
        JoinButton.IsEnabled = value.Length >= 5 && value.StartsWith("KSR-", StringComparison.OrdinalIgnoreCase);
        StatusText.Text = string.Empty;
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            JoinedCampaign = await _client.JoinCampaignAsync(
                _serverUrl, _accessToken, CampaignIdTextBox.Text);
            MessageBox.Show(
                $"You joined {JoinedCampaign.Name}.\n\nCampaign ID: {JoinedCampaign.CampaignCode}\nRole: {JoinedCampaign.Role.ToUpperInvariant()}",
                "Campaign Joined", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (KsrApiException exception) { ShowError(ApiErrorMessages.ForCampaignJoin(exception)); }
        catch (HttpRequestException) { ShowError("The KSR server is unreachable. Try again when your connection is restored."); }
        catch (Exception exception) { ShowError(exception.Message); }
        finally { SetBusy(false); }
    }

    private void ShowError(string message) => StatusText.Text = message;

    private void SetBusy(bool busy)
    {
        CampaignIdTextBox.IsEnabled = !busy;
        var value = CampaignIdTextBox.Text.Trim();
        JoinButton.IsEnabled = !busy && value.Length >= 5 &&
            value.StartsWith("KSR-", StringComparison.OrdinalIgnoreCase);
        JoinButton.Content = busy ? "JOINING…" : "JOIN CAMPAIGN";
        if (busy) StatusText.Text = "Verifying the Campaign ID with the KSR server…";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
