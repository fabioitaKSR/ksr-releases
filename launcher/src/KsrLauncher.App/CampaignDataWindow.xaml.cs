using System.Diagnostics;
using System.Windows;

namespace KsrLauncher.App;

public partial class CampaignDataWindow : Window
{
    private const string PublicArchivesUrl = "https://play.kerbalspacerace.net/public-records";

    public CampaignDataWindow() => InitializeComponent();

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
