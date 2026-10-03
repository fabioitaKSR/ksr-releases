namespace KsrLauncher.Core;

public static class TestGameSupportPaths
{
    public static string SavesRoot(string kspRoot)
    {
        var saves = SafePaths.Under(kspRoot, "saves");
        if (!Directory.Exists(saves)) throw new DirectoryNotFoundException("No saves folder was found in the selected test installation.");
        SafePaths.RejectReparsePoints(kspRoot, saves);
        return saves;
    }

    public static string SaveFolder(string kspRoot, string selectedFolder)
    {
        var saves = SavesRoot(kspRoot);
        var selected = Path.GetFullPath(selectedFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(selected), saves, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a save folder inside the selected test installation.");
        var persistent = SafePaths.Under(selected, "persistent.sfs");
        if (!File.Exists(persistent)) throw new InvalidDataException("The selected save has no persistent.sfs file.");
        SafePaths.RejectReparsePoints(saves, persistent);
        return selected;
    }

    public static string LogFile(string kspRoot)
    {
        var log = SafePaths.Under(kspRoot, "KSP.log");
        if (!File.Exists(log)) throw new FileNotFoundException("KSP.log was not found in the selected test installation.", log);
        SafePaths.RejectReparsePoints(kspRoot, log);
        return log;
    }
}
