using System.Text.Json;
using System.Text.RegularExpressions;

namespace KsrLauncher.Core;

public static partial class ManifestService
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<ReleaseManifest> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<ReleaseManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Manifest vuoto o non valido.");
        Validate(manifest);
        return manifest;
    }

    public static void Validate(ReleaseManifest manifest)
    {
        var errors = new List<string>();
        if (manifest.SchemaVersion != 1) errors.Add($"schemaVersion {manifest.SchemaVersion} non supportato (atteso 1)");
        if (!string.Equals(manifest.Product, "KerbalSpaceRace", StringComparison.Ordinal)) errors.Add("product deve essere KerbalSpaceRace");
        if (!Version.TryParse(manifest.Version, out _)) errors.Add("version non e una versione valida");
        if (!Version.TryParse(manifest.MinimumLauncherVersion, out _)) errors.Add("minimumLauncherVersion non e valida");
        if (manifest.Components.Count == 0) errors.Add("components e vuoto");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in manifest.Components)
        {
            if (string.IsNullOrWhiteSpace(component.Id) || !ids.Add(component.Id)) errors.Add($"id componente vuoto o duplicato: '{component.Id}'");
            if (string.IsNullOrWhiteSpace(component.TransactionGroup)) errors.Add($"{component.Id}: transactionGroup mancante");
            if (Path.GetFileName(component.Asset) != component.Asset || !component.Asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) errors.Add($"{component.Id}: asset deve essere un solo nome file ZIP");
            if (!Sha256Regex().IsMatch(component.Sha256)) errors.Add($"{component.Id}: sha256 deve contenere 64 caratteri esadecimali");
            ValidateRelative(component.Source, $"{component.Id}: source", errors);
            ValidateRelative(component.Target, $"{component.Id}: target", errors);
            if (!string.Equals(component.TargetKind, "ksp", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(component.TargetKind, "launcherData", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{component.Id}: targetKind deve essere ksp o launcherData");
            foreach (var required in component.RequiredFiles) ValidateRelative(required, $"{component.Id}: requiredFiles", errors);
        }

        foreach (var path in manifest.Preserve.Concat(manifest.ManagedFilesInsidePreservedPaths).Concat(manifest.Delete))
            ValidateRelative(path.Replace("**", "x").Replace("*", "x"), "percorso globale", errors);

        if (errors.Count > 0) throw new InvalidDataException("Manifest non valido:\n- " + string.Join("\n- ", errors));
    }

    private static void ValidateRelative(string value, string label, List<string> errors)
    {
        try { _ = SafePaths.Under(Path.GetTempPath(), value); }
        catch { errors.Add($"{label} non e un percorso relativo sicuro: '{value}'"); }
    }

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
