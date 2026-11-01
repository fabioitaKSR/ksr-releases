using System.IO;
using System.Windows;
using System.Windows.Controls;
using KsrLauncher.Core;

namespace KsrLauncher.App;

internal static class LoggerCsvSupportDialog
{
    public static void Open(Window owner, string kspRoot, string? preferredSave)
    {
        try
        {
            var saves = LoggerCsvSupport.Discover(kspRoot);
            if (saves.Count == 0)
            {
                MessageBox.Show(owner, "No current logger CSV files were found in this installation. Play a save with the KSR logger first.",
                    "KSR Support", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var selected = saves.FirstOrDefault(save => string.Equals(save.SaveName, preferredSave, StringComparison.OrdinalIgnoreCase));
            if (selected is null && saves.Count == 1) selected = saves[0];
            if (selected is null)
            {
                var picker = new LoggerCsvSaveWindow(saves) { Owner = owner };
                if (picker.ShowDialog() != true) return;
                selected = picker.SelectedSave;
            }
            if (selected is null) return;
            var associateCampaign = preferredSave is not null && string.Equals(selected.SaveName, preferredSave, StringComparison.OrdinalIgnoreCase);
            new SupportReportWindow(SupportReportType.Csv, selected.DirectoryPath, selected.SaveName,
                useSelectedCampaign: associateCampaign, csvKspRoot: kspRoot) { Owner = owner }.ShowDialog();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(owner, exception.Message, "KSR Support", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

internal sealed class LoggerCsvSaveWindow : Window
{
    private readonly ComboBox _saves;
    public LoggerCsvSave? SelectedSave => _saves.SelectedItem as LoggerCsvSave;

    public LoggerCsvSaveWindow(IReadOnlyList<LoggerCsvSave> saves)
    {
        Title = "Send Logger CSV"; Width = 560; Height = 260; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("ForegroundBrush");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "SELECT A LOGGER SAVE", Style = (Style)Application.Current.FindResource("SectionTitle") });
        panel.Children.Add(new TextBlock { Text = "Its current logger CSV files will be collected automatically.", Margin = new Thickness(0, 8, 0, 14) });
        _saves = new ComboBox { ItemsSource = saves, DisplayMemberPath = nameof(LoggerCsvSave.SaveName), SelectedIndex = 0 };
        panel.Children.Add(_saves);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "CANCEL", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var select = new Button { Content = "CONTINUE", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        select.Click += (_, _) => { if (SelectedSave is not null) DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(select); panel.Children.Add(actions); Content = panel;
    }
}
