using System.Windows;
using KsrLauncher.Core;

namespace KsrLauncher.App;

internal static class InstallationWarningDialog
{
    public static bool Review(LauncherLocations locations, IReadOnlyList<string> warnings)
    {
        var text = "Existing mods need your attention.\n\nKSP folder: " + locations.KspRoot +
            "\n\n" + string.Join("\n\n", warnings.Take(12)) +
            (warnings.Count > 12 ? $"\n\n{warnings.Count - 12} additional warning(s)." : "") +
            "\n\nYou are responsible for resolving mod version conflicts and duplicate installations. " +
            "The launcher does not automatically resolve these conflicts. Continuing applies the selected packages and may overwrite existing files at the same paths. " +
            "Unlisted files are preserved; this does not guarantee compatibility.\n\n" +
            "Choose No to manage your mods first, or Yes to continue.";
        return MessageBox.Show(text, "Mod Installation Warning", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
