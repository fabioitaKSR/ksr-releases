namespace KsrLauncher.Core;

internal static class FileTree
{
    public static void CopyDirectory(string source, string destination)
    {
        SafePaths.RejectReparsePoints(Path.GetPathRoot(Path.GetFullPath(source))!, source);
        SafePaths.RejectReparsePoints(Path.GetPathRoot(Path.GetFullPath(destination))!, destination);
        var entries = EnumerateSafely(source).ToArray();
        Directory.CreateDirectory(destination);
        foreach (var entry in entries)
        {
            var target = SafePaths.Under(destination, Path.GetRelativePath(source, entry));
            SafePaths.RejectReparsePoints(destination, target);
            if (Directory.Exists(entry)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(entry, target, true);
        }
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        SafePaths.RejectReparsePoints(Path.GetPathRoot(Path.GetFullPath(path))!, path);
        var entries = EnumerateSafely(path).ToArray();
        foreach (var file in entries.Where(File.Exists)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    private static IEnumerable<string> EnumerateSafely(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked installation entries cannot be updated or removed.");
            yield return entry;
            if (Directory.Exists(entry))
                foreach (var child in EnumerateSafely(entry)) yield return child;
        }
    }
}
