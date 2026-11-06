using System.Diagnostics;
using System.IO;
using System.Text;

namespace KsrLauncher.App;

internal static class LauncherSelfUpdateCoordinator
{
    public static bool TrySchedule(string downloadedExecutable, string expectedSha256)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)) return false;
        var source = Path.GetFullPath(downloadedExecutable);
        var destination = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(destination) || !File.Exists(source)) return false;
        destination = Path.GetFullPath(destination);
        if (!destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(destination).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return false;

        var workDirectory = Path.GetDirectoryName(source)!;
        var script = Path.Combine(workDirectory, "apply-launcher-update.ps1");
        File.WriteAllText(script, UpdateScript, new UTF8Encoding(false));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-Source");
        startInfo.ArgumentList.Add(source);
        startInfo.ArgumentList.Add("-Destination");
        startInfo.ArgumentList.Add(destination);
        startInfo.ArgumentList.Add("-ExpectedSha256");
        startInfo.ArgumentList.Add(expectedSha256);
        Process.Start(startInfo);
        return true;
    }

    private const string UpdateScript = """
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination,
    [Parameter(Mandatory=$true)][string]$ExpectedSha256
)
$ErrorActionPreference = 'Stop'
$sourcePath = [System.IO.Path]::GetFullPath($Source)
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
if (-not [System.IO.File]::Exists($sourcePath)) { exit 2 }
$deadline = [DateTime]::UtcNow.AddMinutes(2)
while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 250
}
if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { exit 3 }
if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $ExpectedSha256) { exit 4 }
Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
if ((Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash -ne $ExpectedSha256) { exit 5 }
$started = Start-Process -FilePath $destinationPath -WorkingDirectory ([System.IO.Path]::GetDirectoryName($destinationPath)) -PassThru
try {
    if (-not $started.WaitForInputIdle(30000)) { exit 6 }
    if ($started.WaitForExit(3000)) { exit 7 }
    $started.Refresh()
    if ($started.HasExited -or $started.MainWindowHandle -eq 0) { exit 7 }
    $updatesRoot = [IO.Path]::GetFullPath([IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'KSRLauncher', 'Updates'))
    $folder = [IO.Path]::GetDirectoryName($sourcePath)
    $tag = [IO.Path]::GetFileName($folder)
    if ([IO.Path]::GetDirectoryName($folder) -ne $updatesRoot -or $tag -notmatch '^v[0-9]+[.][0-9]+[.][0-9]+$') { exit 8 }
    if ([IO.Path]::GetFileName($sourcePath) -ne ('KSR-Launcher-' + $tag + '-win-x64.exe') -or
        [IO.Path]::GetDirectoryName($PSCommandPath) -ne $folder -or
        [IO.Path]::GetFileName($PSCommandPath) -ne 'apply-launcher-update.ps1') { exit 8 }
    $path = $sourcePath
    while ($path) {
        if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 9 }
        $path = [IO.Path]::GetDirectoryName($path)
    }
    Remove-Item -LiteralPath $sourcePath -Force
    Remove-Item -LiteralPath $PSCommandPath -Force
    if (-not (Get-ChildItem -LiteralPath $folder -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $folder
    }
} catch {
    # A verified replacement may still run when cleanup is unavailable. Retry old downloads at the next startup.
}
""";
}
