using System.IO.Compression;
using System.Text.Json;
using KsrLauncher.Core;

if (args.Length != 4) throw new ArgumentException("Expected previous manifest, new manifest, assets directory, test output root.");
var oldManifest = await ManifestService.LoadAsync(args[0]);
var newManifest = await ManifestService.LoadAsync(args[1]);
if (oldManifest.Version != newManifest.Version) throw new Exception("Global version change would force unrelated reinstalls.");
var differences = newManifest.Components.Where(component =>
    JsonSerializer.Serialize(component, ManifestService.JsonOptions) !=
    JsonSerializer.Serialize(oldManifest.Components.Single(old => old.Id == component.Id), ManifestService.JsonOptions)).ToArray();
if (differences.Length != 1 || differences[0].Id != "parameter-logger") throw new Exception("Manifest must change only the logger component.");
Console.WriteLine("PASS only Parameter Logger component changed");

var logger = newManifest.Components.Single(component => component.Id == "parameter-logger");
string zipPath = Path.Combine(args[2], logger.Asset);
if (await PackageService.ComputeSha256Async(zipPath) != logger.Sha256 || new FileInfo(zipPath).Length != logger.Size)
    throw new Exception("Package digest/length mismatch.");
using (var zip = ZipFile.OpenRead(zipPath))
{
    var files = zip.Entries.Where(entry => entry.Name.Length != 0).Select(entry => entry.FullName.Replace('\\','/')).Order().ToArray();
    string[] allowed = ["GameData/KSRParameterLogger/PERFORMANCE_README.txt", "GameData/KSRParameterLogger/Plugins/KSRParameterLogger.dll", "GameData/KSRParameterLogger/Plugins/KerbalSpaceRace.LoggerPerformance.dll"];
    if (!files.SequenceEqual(allowed.Order())) throw new Exception("Unexpected files in package.");
}
Console.WriteLine("PASS package contains only two DLLs and public documentation");

foreach (string mode in new[] {"ksr-legacy", "launch-and-logs"})
{
    var locations = new LauncherLocations(Path.GetFullPath(Path.Combine(args[3], mode, "ksp")), Path.GetFullPath(Path.Combine(args[3], mode, "launcher-data")));
    Directory.CreateDirectory(Path.Combine(locations.KspRoot, "GameData"));
    File.WriteAllText(Path.Combine(locations.KspRoot,"KSP_x64.exe"), "test marker, not executable");
    var state = new InstalledState();
    foreach (var component in oldManifest.Components)
    {
        string target = SafePaths.Under(UpdatePlanner.GetTargetRoot(component, locations), component.Target);
        Directory.CreateDirectory(target);
        foreach (var required in component.RequiredFiles)
        {
            string file = SafePaths.Under(target,required);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file,"existing component marker");
        }
        state.Components[component.Id] = new InstalledComponent {Version=oldManifest.Version, Sha256=component.Sha256, Target=component.Target, TargetKind=component.TargetKind};
    }
    string preserved = Path.Combine(locations.KspRoot,"GameData","KSRParameterLogger","PluginData","campaign-data.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(preserved)!); File.WriteAllText(preserved,"preserve existing player data");
    await StateStore.SaveAsync(locations.LauncherDataRoot,state);
    var plan = UpdatePlanner.Create(newManifest,state,locations);
    if (plan.Components.Count(item=>item.NeedsUpdate) != 1 || !plan.Components.Single(item=>item.NeedsUpdate).Component.Id.Equals("parameter-logger"))
        throw new Exception("Unrelated components would update.");
    Console.WriteLine($"PASS {mode}: only Parameter Logger scheduled for update");
    // Only this offline fixture changes the URL to use the local verified ZIP.
    string publishedUrl = logger.AssetUrl; logger.AssetUrl="";
    UpdateResult result;
    try { result=await new UpdateEngine().RunAsync(newManifest,locations,args[2],true); }
    finally { logger.AssetUrl=publishedUrl; }
    if (!result.Applied || File.ReadAllText(preserved)!="preserve existing player data") throw new Exception("Update lost player data.");
    string installed = Path.Combine(locations.KspRoot,"GameData","KSRParameterLogger","Plugins","KerbalSpaceRace.LoggerPerformance.dll");
    if (!File.Exists(installed)) throw new Exception("Companion DLL missing after update.");
    Console.WriteLine($"PASS {mode}: package installed and PluginData preserved");
    var installedState=await StateStore.LoadAsync(locations.LauncherDataRoot);
    if (UpdatePlanner.Create(newManifest,installedState,locations).HasUpdates) throw new Exception("Repeated update required after install.");
    Console.WriteLine($"PASS {mode}: subsequent update is a no-op");
    await UpdateEngine.RollbackAsync(result.BackupDirectory!,locations,true);
    if(File.Exists(installed) || File.ReadAllText(preserved)!="preserve existing player data") throw new Exception("Rollback failed.");
    Console.WriteLine($"PASS {mode}: rollback restores previous files and retains PluginData");
}
Console.WriteLine("ALL RELEASE INTEGRATION CHECKS PASSED");
