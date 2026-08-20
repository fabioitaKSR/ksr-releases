namespace KsrLauncher.Core;

public static class SafePaths
{
    public static string Under(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("La cartella radice e obbligatoria.", nameof(root));
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Percorso relativo non valido: '{relativePath}'.");

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, Normalize(relativePath)));
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Il percorso esce dalla radice consentita: '{relativePath}'.");
        return fullPath;
    }

    public static string Normalize(string path) => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    public static string ManifestPath(string path) => path.Replace('\\', '/').TrimStart('/');

    public static void RejectReparsePoints(string root, string target)
    {
        var fullRoot = Path.GetFullPath(root);
        var current = Path.GetFullPath(target);
        while (current.Length >= fullRoot.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Percorso con collegamento/reparse point non consentito: {current}");
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                break;
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            current = parent;
        }
    }
}
