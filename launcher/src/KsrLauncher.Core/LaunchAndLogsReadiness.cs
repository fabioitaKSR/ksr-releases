namespace KsrLauncher.Core;

public static class LaunchAndLogsReadiness
{
    public static readonly string[] RequiredFiles =
    [
        "GameData/KSRLite/Plugins/KSRRemoteLogger.dll",
        "GameData/KerbalSpaceRaceNationSelector/Plugins/KerbalSpaceRace.NationSelector.dll",
        "GameData/KSRParameterLogger/Plugins/KSRParameterLogger.dll",
        "GameData/Achievements/Plugins/Achievements.dll",
        "GameData/000_Harmony/0Harmony.dll",
        "GameData/000_Harmony/Harmony.version",
        "GameData/000_Harmony/HarmonyInstallChecker.dll",
        "GameData/000_ClickThroughBlocker/Plugins/ClickThroughBlocker.dll",
        "GameData/001_ToolbarControl/Plugins/ToolbarControl.dll",
        "GameData/SpaceTuxLibrary/Plugins/SpaceTuxUtility.dll",
        "GameData/TarsierSpaceTech/Plugins/TarsierSpaceTech.dll"
    ];

    public static IReadOnlyList<string> MissingFiles(string kspRoot) =>
        RequiredFiles.Where(path => !File.Exists(Path.Combine(kspRoot,
            path.Replace('/', Path.DirectorySeparatorChar)))).ToArray();

    public static bool IsInstalled(string kspRoot) => MissingFiles(kspRoot).Count == 0;
}
