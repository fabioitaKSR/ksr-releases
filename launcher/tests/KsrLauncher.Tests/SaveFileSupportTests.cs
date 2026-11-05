using System.IO.Compression;
using KsrLauncher.Core;

internal static class SaveFileSupportTests
{
    public static async Task Package()
    {
        var root = Path.Combine(Path.GetTempPath(), "save-file-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, "saves", "My campaign");
            Directory.CreateDirectory(folder);
            foreach (var name in new[] { "persistent.sfs", "quicksave.sfs", "Manual save 1.sfs" })
                await File.WriteAllTextAsync(Path.Combine(folder, name), "GAME { title = " + name + " }");
            await File.WriteAllTextAsync(Path.Combine(folder, "private.txt"), "excluded");
            foreach (var name in new[] { "persistent.sfs", "quicksave.sfs", "Manual save 1.sfs" })
            {
                var file = Path.Combine(folder, name);
                var original = await File.ReadAllBytesAsync(file);
                var request = Request(root, file);
                var package = await new SupportReportPackager().CreateAsync(request, Path.Combine(root, Guid.NewGuid().ToString("N")));
                using var zip = ZipFile.OpenRead(package.FilePath);
                var entries = zip.Entries.Select(entry => entry.FullName).Order().ToArray();
                if (!entries.SequenceEqual(new[] { "manifest.json", "report.txt", "save/" + name }))
                    throw new Exception("Package did not contain exactly the selected save and report metadata.");
                using var reader = new StreamReader(zip.GetEntry("save/" + name)!.Open());
                if (await reader.ReadToEndAsync() != System.Text.Encoding.UTF8.GetString(original))
                    throw new Exception("Selected save content changed.");
                if (!original.SequenceEqual(await File.ReadAllBytesAsync(file))) throw new Exception("Original save changed.");
            }
            File.Delete(Path.Combine(folder, "persistent.sfs"));
            TestGameSupportPaths.SaveFile(root, Path.Combine(folder, "quicksave.sfs"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static async Task Boundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "save-file-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, "saves", "Game");
            Directory.CreateDirectory(Path.Combine(folder, "Backup"));
            var selected = Path.Combine(folder, "manual.sfs");
            await File.WriteAllTextAsync(selected, "save");
            var foreign = Path.Combine(root, "other-installation", "saves", "Game", "manual.sfs");
            Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
            await File.WriteAllTextAsync(foreign, "foreign");
            var nested = Path.Combine(folder, "Backup", "manual.sfs");
            await File.WriteAllTextAsync(nested, "nested");
            var text = Path.Combine(folder, "private.txt");
            await File.WriteAllTextAsync(text, "private");
            var linkedFolder = Path.Combine(root, "saves", "LinkedGame");
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{linkedFolder}' -Target '{Path.GetDirectoryName(foreign)}' -ErrorAction Stop | Out-Null");
            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new Exception("Could not prepare linked-save fixture.");
            }
            var link = Path.Combine(linkedFolder, "manual.sfs");
            foreach (var invalid in new[] { foreign, nested, text, link, Path.Combine(folder, "missing.sfs") })
            {
                var queue = Path.Combine(root, "rejected-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await new SupportReportPackager().CreateAsync(Request(root, invalid), queue);
                    throw new Exception("Unsafe save accepted: " + invalid);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException) { }
                if (Directory.Exists(queue)) throw new Exception("Rejected save created a queue.");
            }
        }
        finally
        {
            var junction = Path.Combine(root, "saves", "LinkedGame");
            if (Directory.Exists(junction)) Directory.Delete(junction);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static SupportReportRequest Request(string root, string file) => new(
        SupportReportType.Save, file, "The vessel disappears after loading.", "Player", null, null,
        "My campaign", "0.7.28", "1.12.5", SaveKspRoot: root);
}
