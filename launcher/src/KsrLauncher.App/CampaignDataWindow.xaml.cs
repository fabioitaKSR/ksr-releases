using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class CampaignDataWindow : Window
{
    private const string PublicArchivesUrl = "https://play.kerbalspacerace.net/public-records";

    private readonly KsrPlatformClient _client = new();
    private readonly string _serverUrl;
    private readonly string? _accessToken;
    private bool _busy;
    private readonly string[] _gameRoots;

    public CampaignDataWindow(IEnumerable<string>? gameRoots = null)
    {
        _gameRoots = gameRoots?.Where(root => !GameLoggingPrivacy.IsLegacyInstallation(root)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
        _serverUrl = LauncherSession.ServerUrl ?? KsrPlatformClient.ProductionServerUrl;
        _accessToken = LauncherSession.AccessToken;
        InitializeComponent();
        Loaded += async (_, _) => await LoadCampaignsAsync();
    }

    private async Task LoadCampaignsAsync(string? selectCode = null)
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            SavedChoiceText.Text = "Sign in to view your campaign data choices.";
            return;
        }
        _busy = true;
        CampaignSelector.IsEnabled = ChangeChoiceButton.IsEnabled = false;
        selectCode ??= (CampaignSelector.SelectedItem as KsrCampaign)?.CampaignCode;
        ChoiceStatusText.Text = "Loading…";
        try
        {
            var campaigns = await _client.GetDataCampaignsAsync(_serverUrl, _accessToken);
            CampaignSelector.ItemsSource = campaigns;
            CampaignSelector.SelectedItem = campaigns.FirstOrDefault(c => c.CampaignCode == selectCode) ?? campaigns.FirstOrDefault();
            ChoiceStatusText.Text = campaigns.Count == 0 ? "You are not participating in any campaigns." : "";
            if (campaigns.Count == 0) SavedChoiceText.Text = "No campaign selected.";
        }
        catch (Exception)
        {
            CampaignSelector.ItemsSource = null;
            SavedChoiceText.Text = "Your saved choices could not be loaded.";
            ChoiceStatusText.Text = "Check your connection and sign-in, then refresh.";
        }
        finally
        {
            _busy = false;
            CampaignSelector.IsEnabled = CampaignSelector.Items.Count > 0;
            ShowCampaignChoice();
        }
    }

    private void Campaign_Changed(object sender, SelectionChangedEventArgs e) => ShowCampaignChoice();

    private void ShowCampaignChoice()
    {
        if (CampaignSelector.SelectedItem is not KsrCampaign campaign)
        {
            StopLoggingButton.IsEnabled = ChangeChoiceButton.IsEnabled = false;
            return;
        }
        SavedChoiceText.Text = !campaign.DataChoiceSupported
            ? "Data choices are not available for this campaign yet."
            : campaign.EndDataChoice switch
        {
            "delete" => "Saved choice: Delete my data at the end of this campaign.",
            "retain" => "Saved choice: Keep my data as anonymous race records at the end of this campaign.",
            _ => "No data choice has been recorded for this campaign."
        };
        StopLoggingButton.IsEnabled = !_busy && campaign.Status == "active" && !string.Equals(campaign.CampaignCode,"test1",StringComparison.OrdinalIgnoreCase) && _gameRoots.Length > 0 &&
            !_gameRoots.All(root => GameLoggingPrivacy.IsExitConfirmed(root,campaign.CampaignCode,LauncherSession.UserId));
        ChangeChoiceButton.IsEnabled = !_busy && campaign.Status == "active" && campaign.DataChoiceSupported;
    }

    private async void ChangeChoice_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _accessToken is null || CampaignSelector.SelectedItem is not KsrCampaign campaign) return;
        var dialog = new CampaignDataChoiceWindow(_serverUrl, campaign.EndDataChoice) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _busy = true;
        CampaignSelector.IsEnabled = ChangeChoiceButton.IsEnabled = false;
        ChoiceStatusText.Text = "Saving…";
        try
        {
            var saved = await _client.SetCampaignDataChoiceAsync(_serverUrl, _accessToken, campaign.CampaignCode, dialog.Choice!);
            var campaigns = CampaignSelector.Items.Cast<KsrCampaign>().Select(c => c.CampaignCode == saved.CampaignCode ? saved : c).ToList();
            CampaignSelector.ItemsSource = campaigns;
            CampaignSelector.SelectedItem = saved;
            ChoiceStatusText.Text = "Choice saved.";
        }
        catch (Exception)
        {
            ChoiceStatusText.Text = "The server did not confirm your change. Refresh to check the saved choice before trying again.";
        }
        finally
        {
            _busy = false;
            CampaignSelector.IsEnabled = true;
            ShowCampaignChoice();
        }
    }

    private async void StopLogging_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || CampaignSelector.SelectedItem is not KsrCampaign campaign || _gameRoots.Length == 0 || string.Equals(campaign.CampaignCode,"test1",StringComparison.OrdinalIgnoreCase)) return;
        var message = $"Stop gameplay collection and queued uploads, and leave {campaign.Name}?\n\n" +
            "This stops only your participation. If you administer this campaign, it remains open and you keep your administration role. Your previously received data keeps its saved end-of-campaign choice.\n\n" +
            "If offline, logging stays blocked locally and the server exit is retried after reconnecting. KSP must have the updated Logging Privacy component loaded.";
        if (MessageBox.Show(this,message,"Stop logging and leave",MessageBoxButton.OKCancel,MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _busy = true; StopLoggingButton.IsEnabled = ChangeChoiceButton.IsEnabled = false;
        try
        {
            GameLoggingPrivacy.Stop(_gameRoots,campaign.CampaignCode,LauncherSession.UserId,_serverUrl);
            ChoiceStatusText.Text = "Logging stopped locally. Confirming server exit…";
            var pending = await GameLoggingPrivacy.DeliverPendingAsync(_gameRoots,LauncherSession.UserId,_accessToken);
            ChoiceStatusText.Text = pending == 0 ? "Logging stopped. Server exit confirmed." :
                "Logging stopped locally. Server exit pending; reconnect in the launcher or game to retry.";
        }
        catch
        {
            ChoiceStatusText.Text = "The stop could not be fully saved. Keep KSP closed and retry; contact support if the problem continues.";
        }
        finally { _busy = false; ShowCampaignChoice(); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadCampaignsAsync();
    private void Privacy_Click(object sender, RoutedEventArgs e) => CampaignDataChoiceWindow.OpenPrivacy(this, _serverUrl);

    private void ViewArchives_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(PublicArchivesUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show(this,
                "The browser could not be opened. Visit https://play.kerbalspacerace.net/public-records to view the archives.",
                "Race Archives", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
