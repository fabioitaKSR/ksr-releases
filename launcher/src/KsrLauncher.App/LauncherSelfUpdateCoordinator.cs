using System.Diagnostics;
using System.IO;
using System.Text;

namespace KsrLauncher.App;

internal static class LauncherSelfUpdateCoordinator
{
    public static bool TrySchedule(string downloadedExecutable)
    {
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
        Process.Start(startInfo);
        return true;
    }

    private const string UpdateScript = """
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
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
Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
Start-Process -FilePath $destinationPath -WorkingDirectory ([System.IO.Path]::GetDirectoryName($destinationPath))
""";
}
