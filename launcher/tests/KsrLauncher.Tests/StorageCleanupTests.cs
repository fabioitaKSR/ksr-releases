using System.Text.Json;
using KsrLauncher.Core;

internal static class StorageCleanupTests
{
    public static async Task Retroactive()
    {
        var root = Path.Combine(Path.GetTempPath(), "storage-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), "settings-preserved");
            var queue = Path.Combine(root, "SupportQueue"); Directory.CreateDirectory(queue);
            File.WriteAllText(Path.Combine(queue, "failed-report.zip"), "pending report");
            var roots = new[] { root, Path.Combine(root, "test-installations", new string('A',64)), Path.Combine(root, "beta", "future", new string('B',16)) };
            foreach (var data in roots)
            {
                await StateStore.SaveAsync(data, new InstalledState { UpdatedAtUtc = DateTimeOffset.UtcNow });
                await Backup(data, "completed", DateTimeOffset.UtcNow.AddDays(-1));
                await Backup(data, "newer-than-state", DateTimeOffset.UtcNow.AddDays(1));
                var incomplete = Path.Combine(data, ".ksr-launcher", "backups", "incomplete");
                Directory.CreateDirectory(incomplete);
                File.WriteAllText(Path.Combine(incomplete, "recovery.dll"), "needed for recovery");
                Directory.CreateDirectory(Path.Combine(data, ".ksr-launcher", "backups", "empty"));
            }
            var updates = Path.Combine(root, "Updates");
            foreach(var version in new[] { "v0.7.27", "v0.7.29", "v0.7.30" })
            {
                var folder = Path.Combine(updates, version); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"KSR-Launcher-{version}-win-x64.exe"), "exe");
            }
            var values = new List<StorageCleanupProgress>();
            var result = await LauncherStorageCleanup.CleanApplicationAsync(root, new Version(0,7,29), Path.Combine(root,"running.exe"), new Capture(values));
            foreach (var data in roots)
            {
                if (Directory.Exists(Path.Combine(data, ".ksr-launcher", "backups", "completed"))) throw new Exception("Completed backup retained");
                if (!Directory.Exists(Path.Combine(data, ".ksr-launcher", "backups", "incomplete"))) throw new Exception("Incomplete backup removed");
                if (!Directory.Exists(Path.Combine(data, ".ksr-launcher", "backups", "newer-than-state"))) throw new Exception("Uncommitted backup removed");
                if (Directory.Exists(Path.Combine(data, ".ksr-launcher", "backups", "empty"))) throw new Exception("Empty backup retained");
                if (!File.Exists(StateStore.GetStatePath(data))) throw new Exception("Installed state removed");
            }
            if (Directory.Exists(Path.Combine(updates,"v0.7.27"))) throw new Exception("Old download retained");
            if (!Directory.Exists(Path.Combine(updates,"v0.7.29")) || !Directory.Exists(Path.Combine(updates,"v0.7.30"))) throw new Exception("Pending/current download removed");
            if (File.ReadAllText(Path.Combine(root,"settings.json")) != "settings-preserved" || !File.Exists(Path.Combine(queue,"failed-report.zip"))) throw new Exception("User settings/report removed");
            if(values.Count<2 || values[^1].Completed != values[^1].Total) throw new Exception("Missing completion progress");
            await Backup(root, "interrupted-cleanup", DateTimeOffset.UtcNow.AddDays(-1));
            var interrupted = Path.Combine(root, ".ksr-launcher", "backups", "interrupted-cleanup");
            var marker = Path.Combine(root, ".ksr-launcher", "cleanup", "interrupted-cleanup.json");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.Move(Path.Combine(interrupted, "backup-manifest.json"), marker);
            var resumed = await LauncherStorageCleanup.CleanApplicationAsync(root,new Version(0,7,29),Path.Combine(root,"running.exe"));
            if (Directory.Exists(interrupted) || File.Exists(marker) || resumed.Deleted.Count != 1) throw new Exception("Interrupted approved cleanup did not resume");
            var again = await LauncherStorageCleanup.CleanApplicationAsync(root,new Version(0,7,29),Path.Combine(root,"running.exe"));
            if(again.Deleted.Count != 0) throw new Exception("Cleanup is not idempotent");
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }

    public static async Task LocksAndLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), "storage-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            await StateStore.SaveAsync(root,new InstalledState { UpdatedAtUtc=DateTimeOffset.UtcNow });
            await Backup(root,"completed",DateTimeOffset.UtcNow.AddDays(-1));
            using(var updateLock = LauncherStorageCleanup.AcquireUpdateLock(root))
            {
                var busy = await LauncherStorageCleanup.CleanApplicationAsync(root,new Version(0,7,29),Path.Combine(root,"running.exe"));
                if(busy.Deleted.Count!=0 || busy.Retained.Count==0) throw new Exception("Active update was not protected");
                try { using var conflicting = LauncherStorageCleanup.AcquireUpdateLock(root); throw new Exception("Concurrent lock acquired"); }
                catch(IOException) { }
            }
            var outside=Path.Combine(root,"outside"); Directory.CreateDirectory(outside);
            var original=Path.Combine(outside,"keep.txt"); File.WriteAllText(original,"keep");
            var linked=Path.Combine(root,".ksr-launcher","backups","completed","linked");
            var start=new System.Diagnostics.ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true};
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{linked}' -Target '{outside}' -ErrorAction Stop | Out-Null");
            using(var p=System.Diagnostics.Process.Start(start)!){await p.WaitForExitAsync(); if(p.ExitCode!=0)throw new Exception("Junction fixture failed");}
            var result=await LauncherStorageCleanup.CleanApplicationAsync(root,new Version(0,7,29),Path.Combine(root,"running.exe"));
            if(result.Deleted.Count!=0 || File.ReadAllText(original)!="keep")throw new Exception("Linked cache cleanup escaped its root");
            Directory.Delete(linked);
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }

    private static async Task Backup(string root,string name,DateTimeOffset date)
    {
        var folder=Path.Combine(root,".ksr-launcher","backups",name); Directory.CreateDirectory(folder);
        var journal=new BackupJournal{CreatedAtUtc=date,LauncherDataRoot=root,KspRoot=Path.Combine(root,"game")};
        await File.WriteAllTextAsync(Path.Combine(folder,"backup-manifest.json"),JsonSerializer.Serialize(journal,ManifestService.JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(folder,"old-mod.dll"),"old");
    }
    private sealed class Capture(List<StorageCleanupProgress> values) : IProgress<StorageCleanupProgress>
    { public void Report(StorageCleanupProgress value) => values.Add(value); }
}
