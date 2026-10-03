using System.Security.Cryptography;
using System.Text;

namespace KsrLauncher.Core;

public static class TestInstallationPaths
{
    public static bool IsSameKspRoot(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);

    public static string GetLauncherDataRoot(string testKspRoot, string? mainKspRoot, string launcherDataRoot)
    {
        var normalizedTestRoot = Normalize(testKspRoot);
        if (IsSameKspRoot(normalizedTestRoot, mainKspRoot)) return Path.GetFullPath(launcherDataRoot);

        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedTestRoot.ToUpperInvariant())));
        return Path.Combine(Path.GetFullPath(launcherDataRoot), "test-installations", identity);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
