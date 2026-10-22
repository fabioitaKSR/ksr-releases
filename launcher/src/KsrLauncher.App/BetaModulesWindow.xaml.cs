using System.Windows;
using System.Windows.Controls;

namespace KsrLauncher.App;

public partial class BetaModulesWindow : Window
{
    public BetaModulesWindow()
    {
        InitializeComponent();
        ModulesList.ItemsSource = BetaAccess.Modules;
        BetaAccess.Changed += AccessChanged;
        Closed += (_, _) => BetaAccess.Changed -= AccessChanged;
        Activated += async (_, _) => await BetaAccess.RefreshAsync();
    }

    private void AccessChanged()
    {
        ModulesList.ItemsSource = BetaAccess.Modules;
        if (BetaAccess.Modules.Count == 0) Close();
    }

    private async void OpenModule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var module = await BetaAccess.RequireAsync(id);
        if (module is null || !IsVisible) return;
        new ServerSettingsWindow(LauncherSession.ServerUrl, module) { Owner = this }.ShowDialog();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await BetaAccess.RefreshAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
