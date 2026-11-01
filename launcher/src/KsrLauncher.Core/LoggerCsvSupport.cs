namespace KsrLauncher.Core;

public sealed record LoggerCsvSave(string SaveName, string DirectoryPath, IReadOnlyList<string> Files);

public static class LoggerCsvSupport
{
    public const long MaxBytes = 25 * 1024 * 1024;
    public const int MaxFiles = 16;
    private static readonly string[] Names = ["parameter-events.csv", "gael-heaviest-orbit.csv", "kerbal-mission-times.csv", "spaceage-achievements.csv"];

    public static bool IsLoggerFile(string name) => Names.Any(suffix =>
        name.Equals(suffix, StringComparison.OrdinalIgnoreCase) || name.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase));

    private static string Root(string kspRoot)
    {
        UpdatePlanner.ValidateKspRoot(kspRoot);
        var root = SafePaths.Under(kspRoot, "GameData/KSRParameterLogger/PluginData");
        SafePaths.RejectReparsePoints(kspRoot, root);
        return root;
    }

    public static IReadOnlyList<LoggerCsvSave> Discover(string kspRoot)
    {
        var root = Root(kspRoot);
        if (!Directory.Exists(root)) return [];
        var directories = Directory.EnumerateDirectories(root).Take(257).ToArray();
        if (directories.Length > 256) throw new InvalidDataException("Too many logger save folders. Contact KSR support.");
        var saves = new List<LoggerCsvSave>();
        foreach (var directory in directories)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var files = Files(kspRoot, directory);
            if (files.Count > 0) saves.Add(new LoggerCsvSave(Path.GetFileName(directory), directory, files));
        }
        return saves.OrderBy(save => save.SaveName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> Files(string kspRoot, string directory)
    {
        var root = Root(kspRoot);
        var selected = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(selected), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CSV files must belong to the selected installation's logger folder.");
        SafePaths.RejectReparsePoints(kspRoot, selected);
        if (!Directory.Exists(selected)) throw new DirectoryNotFoundException("The logger save folder was not found.");
        var entries = Directory.EnumerateFiles(selected).Take(1025).ToArray();
        if (entries.Length > 1024) throw new InvalidDataException("Too many files in the logger save folder.");
        var files = entries.Where(file => IsLoggerFile(Path.GetFileName(file))).OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > MaxFiles) throw new InvalidDataException("Too many current logger CSV files.");
        long total = 0;
        foreach (var file in files)
        {
            SafePaths.RejectReparsePoints(kspRoot, file);
            total += new FileInfo(file).Length;
            if (total > MaxBytes) throw new InvalidDataException("The logger CSV files exceed the 25 MB support limit.");
        }
        return files;
    }

    public static async Task CopyAsync(string kspRoot, string directory, string destination, CancellationToken cancellationToken)
    {
        var files = Files(kspRoot, directory);
        if (files.Count == 0) throw new FileNotFoundException("No current logger CSV files were found for this save.");
        Directory.CreateDirectory(destination);
        long total = 0;
        foreach (var file in files)
        {
            SafePaths.RejectReparsePoints(kspRoot, file);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var remaining = input.Length;
            total += remaining;
            if (total > MaxBytes) throw new InvalidDataException("The logger CSV files exceed the 25 MB support limit.");
            await using var output = File.Create(Path.Combine(destination, Path.GetFileName(file)));
            var buffer = new byte[81920];
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new IOException("A logger CSV changed while it was being copied. Return to the KSP main menu and try again.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
    }
}
