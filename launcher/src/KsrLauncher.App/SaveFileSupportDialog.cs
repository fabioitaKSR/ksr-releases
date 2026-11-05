using System.IO;
using System.Windows;
using System.Windows.Controls;
using KsrLauncher.Core;
using Microsoft.Win32;

namespace KsrLauncher.App;

internal static class SaveFileSupportDialog
{
    public static void Open(Window owner, string kspRoot, string? preferredSave)
    {
        try
        {
            var saves = TestGameSupportPaths.SavesRoot(kspRoot);
            var initial = preferredSave is null ? saves : SafePaths.Under(saves, preferredSave);
            if (!Directory.Exists(initial)) initial = saves;
            var choice = new SaveSelectionWindow { Owner = owner };
            if (choice.ShowDialog() != true) return;
            string selected;
            if (choice.UseFolder)
            {
                var picker = new OpenFolderDialog
                {
                    Title = "Select a game folder to send its persistent.sfs",
                    InitialDirectory = initial,
                    Multiselect = false
                };
                if (picker.ShowDialog(owner) != true) return;
                selected = Path.Combine(TestGameSupportPaths.SaveFolder(kspRoot, picker.FolderName), "persistent.sfs");
            }
            else
            {
                var picker = new OpenFileDialog
                {
                    Title = "Select the KSP save file to send",
                    InitialDirectory = initial,
                    Filter = "KSP save files (*.sfs)|*.sfs",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (picker.ShowDialog(owner) != true) return;
                selected = picker.FileName;
            }
            var file = TestGameSupportPaths.SaveFile(kspRoot, selected);
            var saveName = Path.GetFileName(Path.GetDirectoryName(file));
            var associateCampaign = preferredSave is not null
                && string.Equals(saveName, preferredSave, StringComparison.OrdinalIgnoreCase);
            new SupportReportWindow(SupportReportType.Save, file, saveName,
                useSelectedCampaign: associateCampaign, saveKspRoot: kspRoot) { Owner = owner }.ShowDialog();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(owner, exception.Message, "KSR Support", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

internal sealed class SaveSelectionWindow : Window
{
    public bool UseFolder { get; private set; }

    public SaveSelectionWindow()
    {
        Title = "Send KSP Save";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush");
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("ForegroundBrush");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "CHOOSE WHAT TO SEND",
            Style = (Style)Application.Current.FindResource("SectionTitle")
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Choose a game folder to automatically send its persistent.sfs, the main saved game. "
                + "Choose a save file to send a specific quicksave or manual .sfs save instead. "
                + "The next screen shows the exact file and asks you to describe the problem before sending.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 20)
        });
        var folder = new Button { Content = "CHOOSE GAME FOLDER", Margin = new Thickness(0, 0, 0, 10) };
        folder.Click += (_, _) => { UseFolder = true; DialogResult = true; };
        var file = new Button { Content = "CHOOSE SAVE FILE", Margin = new Thickness(0, 0, 0, 16) };
        file.Click += (_, _) => { UseFolder = false; DialogResult = true; };
        var cancel = new Button { Content = "CANCEL", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(folder);
        panel.Children.Add(file);
        panel.Children.Add(cancel);
        Content = panel;
    }
}
