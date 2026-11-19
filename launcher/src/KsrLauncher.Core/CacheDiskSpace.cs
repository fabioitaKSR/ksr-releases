using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KsrLauncher.Core;

public sealed class CacheSpaceException(string message, bool diskFull = false, Exception? inner = null)
    : IOException(message, inner)
{
    public bool DiskFull { get; } = diskFull;
}

public sealed class CacheDiskSpace(string folder, Func<string, long>? getAvailableBytes = null)
{
    public void Require(long bytes, bool downloadStarted = false)
    {
        if (bytes <= 0) return;
        var available = (getAvailableBytes ?? GetAvailableBytes)(folder);
        if (available >= bytes) return;
        if (downloadStarted && available == 0)
            throw new CacheSpaceException("The download cache drive ran out of space.", diskFull: true);

        throw new CacheSpaceException(
            $"The download cache needs {FormatSize(bytes)} free. {FormatSize(available)} is available. " +
            $"Free up space or choose another folder in Settings.\n\nCache folder: {folder}");
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 * 1024 ? $"{bytes:N0} bytes" : $"{bytes / 1048576d:N1} MiB";

    private static long GetAvailableBytes(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;

        var directory = Path.TrimEndingDirectorySeparator(path);
        if (!Path.EndsInDirectorySeparator(directory)) directory += Path.DirectorySeparatorChar;
        if (!GetDiskFreeSpaceEx(directory, out var available, out _, out _))
            throw new CacheSpaceException(
                $"Free space could not be checked. Make sure the cache drive is available or choose another folder in Settings.\n\nCache folder: {path}",
                inner: new Win32Exception(Marshal.GetLastWin32Error()));
        return (long)Math.Min(available, (ulong)long.MaxValue);
    }

    public static bool IsDiskFull(IOException exception) =>
        exception.HResult is unchecked((int)0x80070027) or unchecked((int)0x80070070)
            or unchecked((int)0x8007050F);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available,
        out ulong total, out ulong free);
}
