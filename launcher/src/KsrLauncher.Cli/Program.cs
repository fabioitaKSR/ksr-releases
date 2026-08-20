using KsrLauncher.Core;

return await LauncherCli.RunAsync(args);

internal static class LauncherCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { PrintHelp(); return 0; }
            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(1).ToArray());
            switch (command)
            {
                case "validate-manifest":
                    var checkedManifest = await ManifestService.LoadAsync(Required(options, "manifest"));
                    Console.WriteLine($"Manifest valido: {checkedManifest.Product} {checkedManifest.Version}, {checkedManifest.Components.Count} componenti.");
                    return 0;
                case "plan":
                case "update":
                    var source = await ResolveSourceAsync(options);
                    var apply = command == "update" && options.ContainsKey("apply");
                    var policy = options.ContainsKey("install-missing") ? UpdatePolicy.InstallOrRepair : UpdatePolicy.ExistingOnly;
                    var assetsBase = options.GetValueOrDefault("assets-base", source.AssetsBase);
                    var result = await new UpdateEngine().RunAsync(source.Manifest, Locations(options), assetsBase, apply, policy);
                    PrintPlan(result.Plan);
                    if (apply && result.Applied) Console.WriteLine($"Aggiornamento completato. Backup: {result.BackupDirectory}");
                    else if (apply) Console.WriteLine("Nessun aggiornamento necessario.");
                    else Console.WriteLine("Simulazione: nessun file e stato modificato.");
                    return 0;
                case "rollback":
                    var rollbackApply = options.ContainsKey("apply");
                    await UpdateEngine.RollbackAsync(Required(options, "backup"), Locations(options), rollbackApply);
                    Console.WriteLine(rollbackApply ? "Rollback completato." : "Backup valido. Simulazione: nessun file e stato modificato.");
                    return 0;
                default:
                    throw new ArgumentException($"Comando sconosciuto: {command}");
            }
        }
        catch (Exception exception) { Console.Error.WriteLine("ERRORE: " + exception.Message); return 1; }
    }

    private static LauncherLocations Locations(Dictionary<string, string> options) =>
        new(Path.GetFullPath(Required(options, "ksp")), Path.GetFullPath(Required(options, "launcher-data")));

    private static async Task<ManifestSource> ResolveSourceAsync(Dictionary<string, string> options)
    {
        if (options.TryGetValue("repo", out var repository))
        {
            var release = await new GitHubReleaseClient().ResolveAsync(repository, options.GetValueOrDefault("channel", "stable").ToLowerInvariant());
            Console.WriteLine($"GitHub Release selezionata: {release.Tag}");
            return new ManifestSource(release.Manifest, release.AssetsBaseUrl);
        }
        return new ManifestSource(await ManifestService.LoadAsync(Required(options, "manifest")), "");
    }

    private static void PrintPlan(UpdatePlan plan)
    {
        Console.WriteLine($"Release {plan.Manifest.Version} ({plan.Manifest.Channel})");
        foreach (var item in plan.Components)
        {
            var status = item.NeedsUpdate ? "UPDATE" : item.IsPresent ? "OK" : "SKIP";
            Console.WriteLine($"[{status}] {item.Component.Id,-24} {item.Reason} -> {item.TargetPath}");
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Opzione non valida: {args[index]}");
            var key = args[index][2..];
            if (key is "apply" or "install-missing") result[key] = "true";
            else { if (++index >= args.Length) throw new ArgumentException($"Valore mancante per --{key}"); result[key] = args[index]; }
        }
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Opzione obbligatoria: --{name}");

    private static void PrintHelp() => Console.WriteLine("""
        KSR Launcher Core - prototipo CLI

        validate-manifest --manifest <ksr-release.json>
        plan   (--repo <owner/repo> [--channel stable|beta] | --manifest <file>) --ksp <cartella KSP> --launcher-data <cartella>
        update (--repo <owner/repo> [--channel stable|beta] | --manifest <file> --assets-base <URL o cartella>) --ksp <cartella KSP> --launcher-data <cartella> [--apply] [--install-missing]
        rollback --backup <cartella backup> --ksp <cartella KSP> --launcher-data <cartella> [--apply]

        L'update ordinario ignora i componenti KSR non installati.
        --install-missing abilita esplicitamente installazione/riparazione dei componenti assenti.
        Senza --apply, update e rollback sono sempre simulazioni.
        """);

    private sealed record ManifestSource(ReleaseManifest Manifest, string AssetsBase);
}
