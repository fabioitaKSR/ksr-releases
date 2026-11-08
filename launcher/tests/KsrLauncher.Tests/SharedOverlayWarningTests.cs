using System.IO.Compression;
using KsrLauncher.Core;

internal static class SharedOverlayWarningTests
{
    public static async Task ManagedAndUnknownCollisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "ksr-shared-overlay-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game"); var data = Path.Combine(root, "data"); var assets = Path.Combine(root, "assets");
        var target = Path.Combine(game, "GameData", "Shared");
        Directory.CreateDirectory(Path.Combine(target, "Plugins")); Directory.CreateDirectory(Path.Combine(target, "PluginData")); Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(game, "KSP_x64.exe"), "synthetic");
        File.WriteAllText(Path.Combine(target, "Plugins", "logger.dll"), "old");
        File.WriteAllText(Path.Combine(target, "PluginData", "private.cfg"), "keep private data");
        File.WriteAllText(Path.Combine(target, "unlisted-part.cfg"), "keep installed content");
        var state = new InstalledState();
        state.Components["base"] = new InstalledComponent { Version = "1.0.0", Target = "GameData/Shared", Sha256 = new string('a',64), ExpectedFiles = ["Plugins/logger.dll"] };
        await StateStore.SaveAsync(data, state);
        var component = new ComponentManifest { Id = "new-overlay", InstallMode = "overlay", Source = "payload", Target = "GameData/Shared", Asset = "update.zip", Required = true, RequiredFiles = ["Plugins/logger.dll", "new-addon.cfg"] };
        var manifest = new ReleaseManifest { SchemaVersion = 1, Product = "KerbalSpaceRace", Version = "1.1.0", Components = [component] };
        var locations = new LauncherLocations(game, data);
        async Task Package(bool unknownCollision)
        {
            var file = Path.Combine(assets, component.Asset);
            if (File.Exists(file)) File.Delete(file);
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
                foreach (var name in unknownCollision ? new[] { "Plugins/logger.dll", "new-addon.cfg", "Plugins/manual.dll" } : new[] { "Plugins/logger.dll", "new-addon.cfg" })
                { using var writer = new StreamWriter(zip.CreateEntry("payload/" + name).Open()); writer.Write("updated " + name); }
            component.Sha256 = await PackageService.ComputeSha256Async(file); component.Size = new FileInfo(file).Length;
        }
        await Package(false);
        var plan = UpdatePlanner.Create(manifest, state, locations, UpdatePolicy.InstallOrRepair);
        if (!plan.HasUpdates || InstallationWarnings.Inspect(plan, state).Count != 0)
            throw new Exception("A new managed overlay/addon was mistaken for an unregistered mod.");
        int reviews = 0;
        var engine = new UpdateEngine(reviewInstallation: (_, warnings) => { reviews++; return false; });
        var result = await engine.RunAsync(manifest, locations, assets, true, UpdatePolicy.InstallOrRepair);
        if (!result.Applied || reviews != 0 || File.ReadAllText(Path.Combine(target,"Plugins/logger.dll")) != "updated Plugins/logger.dll" ||
            File.ReadAllText(Path.Combine(target,"PluginData/private.cfg")) != "keep private data" || File.ReadAllText(Path.Combine(target,"unlisted-part.cfg")) != "keep installed content")
            throw new Exception("Managed overlay installation, review suppression or preservation failed.");
        var installed = await StateStore.LoadAsync(data);
        if (!installed.Components.ContainsKey("base") || !installed.Components.ContainsKey(component.Id) ||
            (await engine.RunAsync(manifest,locations,assets,true,UpdatePolicy.InstallOrRepair)).Applied)
            throw new Exception("Overlay did not retain base registration or become a no-op.");

        // An undeclared existing collision in the signed payload must still prompt.
        component.Id = "next-overlay";
        File.WriteAllText(Path.Combine(target,"Plugins/manual.dll"),"manual mod");
        await Package(true);
        var beforeState = await File.ReadAllBytesAsync(StateStore.GetStatePath(data));
        var before = Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).ToDictionary(x=>x,File.ReadAllBytes);
        var rejected = new UpdateEngine(reviewInstallation: (_, warnings) =>
        {
            reviews++;
            if (!warnings.Any(w => w.Contains("Plugins/manual.dll"))) throw new Exception("Unknown payload collision not identified.");
            return false;
        });
        try { await rejected.RunAsync(manifest,locations,assets,true,UpdatePolicy.InstallOrRepair); throw new Exception("Declined collision applied."); }
        catch (IOException e) when (e.Message.StartsWith("Installation cancelled.")) { }
        if (reviews != 1 || !beforeState.SequenceEqual(await File.ReadAllBytesAsync(StateStore.GetStatePath(data))) ||
            before.Any(x=>!File.Exists(x.Key)||!x.Value.SequenceEqual(File.ReadAllBytes(x.Key))))
            throw new Exception("Declined payload collision changed game files or state.");
    }

    public static Task BoundariesRemainWarnings()
    {
        var root = Path.Combine(Path.GetTempPath(),"ksr-overlay-boundaries-"+Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root,"GameData/Shared/Plugins"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target,"known.dll"),"old");
        var c = new ComponentManifest { Id="new", Target="GameData/Shared/Plugins",InstallMode="overlay",RequiredFiles=["known.dll"] };
        var state = new InstalledState(); state.Components["base"] = new InstalledComponent {Target="GameData/Shared",ExpectedFiles=["Plugins/known.dll"]};
        UpdatePlan Plan() => new(new ReleaseManifest { Components=[c] }, [new ComponentPlan(c,null,true,true,"fixture",target)]);
        void Warning(bool expected)
        { if (InstallationWarnings.Inspect(Plan(),state).Any(w=>w.Contains("not registered")) != expected) throw new Exception("Overlay boundary warning mismatch."); }
        Warning(false); // Parent ownership correctly maps to a nested target.
        state.Components["base"].Target="GameData/SharedOther"; Warning(true);
        state.Components["base"].Target="GameData/Shared"; state.Components["base"].TargetKind="launcherData"; Warning(true);
        state.Components["base"].TargetKind="ksp"; state.Components["base"].ExpectedFiles=[]; Warning(true);
        state.Components["base"].ExpectedFiles=["../../escape.dll"]; Warning(true);
        state.Components["base"].ExpectedFiles=["Plugins/known.dll"]; c.InstallMode="replace"; Warning(true);
        state.Components.Clear(); c.InstallMode="overlay"; Warning(true);
        return Task.CompletedTask;
    }
}
