using System.Diagnostics;
using System.Windows;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class CampaignDataChoiceWindow : Window
{
    private readonly string _serverUrl;
    public string? Choice { get; private set; }

    public CampaignDataChoiceWindow(string serverUrl, string? currentChoice = null)
    {
        _serverUrl = serverUrl;
        InitializeComponent();
        DeleteChoice.IsChecked = currentChoice == "delete";
        RetainChoice.IsChecked = currentChoice == "retain";
    }

    private void ChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (ConfirmButton is not null)
            ConfirmButton.IsEnabled = DeleteChoice.IsChecked == true || RetainChoice.IsChecked == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteChoice.IsChecked == true ? "delete" : RetainChoice.IsChecked == true ? "retain" : null;
        if (Choice is not null) DialogResult = true;
    }

    internal static void OpenPrivacy(Window owner, string serverUrl)
    {
        try
        {
            var url = KsrPlatformClient.BuildPrivacyNoticeUri(serverUrl);
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show(owner, "The privacy notice could not be opened. Please try again.",
                "Privacy notice", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Privacy_Click(object sender, RoutedEventArgs e) => OpenPrivacy(this, _serverUrl);
}
