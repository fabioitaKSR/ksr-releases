using System.Windows;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class RemoveCampaignMemberWindow : Window
{
    public CampaignMemberDataDisposition SelectedDisposition =>
        DeleteRecordsOption.IsChecked == true
            ? CampaignMemberDataDisposition.Delete
            : CampaignMemberDataDisposition.Retain;

    public RemoveCampaignMemberWindow(string username, string campaignName)
    {
        InitializeComponent();
        ParticipantText.Text = $"{username}  ·  {campaignName}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
