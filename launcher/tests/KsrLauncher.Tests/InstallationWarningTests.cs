using KsrLauncher.Core;

internal static class InstallationWarningTests
{
    public static async Task ReviewBeforeChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "ksr-warning-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game");
        var data = Path.Combine(root, "data");
        var target = Path.Combine(game, "GameData", "Example");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(game, "KSP_x64.exe"), "fixture");
        File.WriteAllText(Path.Combine(target, "example.dll"), "original");
        var component = new ComponentManifest { Id = "example", Asset = "example.zip", Source = "Example",
            Target = "GameData/Example", Sha256 = new string('a', 64), RequiredFiles = ["example.dll"] };
        var manifest = new ReleaseManifest { SchemaVersion = 1, Product = "KerbalSpaceRace", Version = "1.2.0", Components = [component] };
        var locations = new LauncherLocations(game, data);
        var state = new InstalledState();
        var plan = UpdatePlanner.Create(manifest, state, locations);
        if (!InstallationWarnings.Inspect(plan, state).Any(x => x.Contains("not registered")))
            throw new Exception("Manual installation was not reported.");
        var reviews = 0;
        var engine = new UpdateEngine(reviewInstallation: (where, warnings) =>
        {
            if (where != locations || warnings.Count == 0) throw new Exception("Invalid review context.");
            reviews++;
            return false;
        });
        try
        {
            await engine.RunAsync(manifest, locations, "missing-assets", true);
            throw new Exception("Declined operation continued.");
        }
        catch (IOException e) when (e.Message.StartsWith("Installation cancelled.")) { }
        if (reviews != 1 || Directory.Exists(data) || File.ReadAllText(Path.Combine(target, "example.dll")) != "original")
            throw new Exception("Declined review wrote files or reached package preparation.");
        await engine.RunAsync(manifest, locations, "", false);
        if (reviews != 1) throw new Exception("Dry run prompted.");

        state.Components[component.Id] = new InstalledComponent { Version = manifest.Version, Sha256 = component.Sha256,
            Target = component.Target, TargetKind = component.TargetKind, ExpectedFiles = ["example.dll"] };
        await StateStore.SaveAsync(data, state);
        await engine.RunAsync(manifest, locations, "", true);
        if (reviews != 1) throw new Exception("Matching installation prompted.");

        var installed = state.Components[component.Id];
        installed.Version = "1.1.0";
        await StateStore.SaveAsync(data, state);
        var stateBefore = await File.ReadAllBytesAsync(StateStore.GetStatePath(data));
        foreach (var policy in new[] { UpdatePolicy.ExistingOnly, UpdatePolicy.VerifyAllFiles })
        {
            var unchanged = UpdatePlanner.Create(manifest, state, locations, policy);
            if (unchanged.HasUpdates || InstallationWarnings.Inspect(unchanged, state).Count != 0)
                throw new Exception("A catalog-only change prompted or scheduled an identical package.");
            var result = await engine.RunAsync(manifest, locations, "", true, policy);
            if (result.Applied || reviews != 1 || !stateBefore.SequenceEqual(await File.ReadAllBytesAsync(StateStore.GetStatePath(data))))
                throw new Exception("A catalog-only change downloaded, prompted or changed installed state.");
        }
        if (!UpdatePlanner.Create(manifest, state, locations, UpdatePolicy.ReinstallAll).HasUpdates)
            throw new Exception("Explicit repair stopped reinstalling identical packages.");
        installed.Target = "GameData/PreviousLocation";
        var moved = UpdatePlanner.Create(manifest, state, locations);
        if (!moved.HasUpdates || !InstallationWarnings.Inspect(moved, state).Any(x => x.Contains("location differs")))
            throw new Exception("A real location change was ignored.");
        installed.Target = component.Target;
        installed.Sha256 = new string('b', 64);
        if (!UpdatePlanner.Create(manifest, state, locations).HasUpdates)
            throw new Exception("A changed package was ignored.");
        installed.ExpectedFiles.Add("missing.cfg");
        var warnings = InstallationWarnings.Inspect(UpdatePlanner.Create(manifest, state, locations), state);
        if (!warnings.Any(x => x.Contains("differs")) || !warnings.Any(x => x.Contains("missing.cfg")))
            throw new Exception("Package/inventory inconsistencies were not reported.");
        installed.Sha256 = component.Sha256;
        var missingOnly = UpdatePlanner.Create(manifest, state, locations, UpdatePolicy.VerifyAllFiles);
        if (!missingOnly.HasUpdates || !InstallationWarnings.Inspect(missingOnly, state).Any(x => x.Contains("missing.cfg")))
            throw new Exception("Matching package with missing files was not repairable.");
        installed.Version = "1.3.0";
        await StateStore.SaveAsync(data, state);
        try
        {
            await engine.RunAsync(manifest, locations, "missing-assets", true);
            throw new Exception("Downgrade guard was bypassed.");
        }
        catch (InvalidDataException e) when (e.Message.Contains("older component catalog")) { }
        if (File.ReadAllText(Path.Combine(target, "example.dll")) != "original")
            throw new Exception("Downgrade changed original files.");
    }
}
