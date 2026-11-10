using System.IO;
using System.Windows;
using KsrLauncher.Core;

namespace KsrLauncher.App;

internal static class PlayerLogSupportDialog
{
    public static void Open(Window owner, string kspRoot, bool useSelectedCampaign)
    {
        if (!LauncherSession.IsAuthenticated || !PlayerLogSupport.IsAllowed(LauncherSession.Username)) return;
        try
        {
            var log = TestGameSupportPaths.LogFile(kspRoot);
            var playerLog = PlayerLogSupport.CurrentPath;
            PlayerLogSupport.Validate(playerLog, LauncherSession.Username);
            new SupportReportWindow(SupportReportType.Log, log, null,
                useSelectedCampaign: useSelectedCampaign, playerLogPath: playerLog) { Owner = owner }.ShowDialog();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(owner, exception.Message, "KSR Support", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
