namespace KsrLauncher.Core;

public static class PlayerLogSupport
{
    public const long MaximumBytes = 25 * 1024 * 1024;

    public static bool IsAllowed(string? username) =>
        string.Equals(username, "HansLanda", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(username, "ale202", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(username, "fabioita", StringComparison.OrdinalIgnoreCase);

    public static string CurrentPath => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "..", "LocalLow", "Squad", "Kerbal Space Program", "Player.log"));

    public static void Validate(string path, string username)
    {
        if (!IsAllowed(username)) throw new UnauthorizedAccessException("Player.log support is unavailable for this account.");
        if (!Path.GetFileName(path).Equals("Player.log", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only Player.log can be attached.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Player.log was not found. Run KSP before using this button.", fullPath);
        SafePaths.RejectReparsePoints(Path.GetPathRoot(fullPath)!, fullPath);
        var length = new FileInfo(fullPath).Length;
        if (length <= 0 || length > MaximumBytes)
            throw new InvalidDataException("Player.log must contain between 1 byte and 25 MiB.");
    }

    public static async Task CopyAsync(string source, string destination, string username, CancellationToken cancellationToken)
    {
        Validate(source, username);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
        var remaining = input.Length;
        if (remaining <= 0 || remaining > MaximumBytes) throw new InvalidDataException("Player.log size changed. Try again after closing KSP.");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (count == 0) throw new IOException("Player.log changed while it was being collected. Close KSP and try again.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
    }
}
