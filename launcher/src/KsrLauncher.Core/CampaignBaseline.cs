using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KsrLauncher.Core;

public sealed record KspCareerSave(string SavePath, string KspRoot, string SaveName);

public static class KspCareerSaveLocator
{
    public static KspCareerSave Resolve(string savePath)
    {
        var fullSavePath = Path.GetFullPath(savePath);
        var saveDirectory = new DirectoryInfo(fullSavePath);
        var savesDirectory = saveDirectory.Parent;
        var kspRoot = savesDirectory?.Parent;
        if (savesDirectory is null || kspRoot is null ||
            !string.Equals(savesDirectory.Name, "saves", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a save folder located directly inside a KSP 'saves' directory.");
        if (!File.Exists(Path.Combine(fullSavePath, "persistent.sfs")))
            throw new InvalidDataException("The selected folder does not contain persistent.sfs.");
        if (!File.Exists(Path.Combine(kspRoot.FullName, "KSP_x64.exe")) ||
            !Directory.Exists(Path.Combine(kspRoot.FullName, "GameData")))
            throw new InvalidDataException("The selected save does not belong to a valid KSP installation.");
        if (!KspConfigSnapshot.ContainsSupportedCampaignMode(Path.Combine(fullSavePath, "persistent.sfs")))
            throw new InvalidDataException("The selected save must use Career or Science mode. Sandbox games cannot start a KSR campaign.");
        SafePaths.RejectReparsePoints(kspRoot.FullName, fullSavePath);
        return new KspCareerSave(fullSavePath, kspRoot.FullName, saveDirectory.Name);
    }
}

public sealed class CampaignBaselineManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string CampaignName { get; set; } = "";
    public string SourceSaveName { get; set; } = "";
    public string KspVersion { get; set; } = "unknown";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string MasterSaveFile { get; set; } = "master-save.zip";
    public string MasterSaveSha256 { get; set; } = "";
    public long MasterSaveSize { get; set; }
    public List<string> IgnoredGameDataFolders { get; set; } = [];
    public List<BaselineFile> SaveFiles { get; set; } = [];
    public List<BaselineFile> GameDataFiles { get; set; } = [];
    public List<BaselineMod> GameDataMods { get; set; } = [];
    public List<BaselineSetting> Settings { get; set; } = [];
}

public sealed record BaselineFile(string Path, long Size, string Sha256);
public sealed record BaselineMod(string Folder, string? Version);
public sealed record BaselineSetting(string Source, string Key, string Value, string DisplayName);
public sealed record CampaignBaselinePackage(string Directory, string ManifestPath, string MasterSavePath, CampaignBaselineManifest Manifest);
public sealed record BaselineProgress(string Stage, int Completed, int Total, string? CurrentPath);

public enum BaselineDifferenceKind { Missing, Extra, Modified, ValueMismatch }
public enum BaselineDifferenceArea { Save, Difficulty, ModConfiguration, GameData }

public sealed record BaselineDifference(
    BaselineDifferenceArea Area,
    BaselineDifferenceKind Kind,
    string Path,
    string? Expected,
    string? Actual,
    string DisplayName);

public sealed record CampaignComplianceResult(IReadOnlyList<BaselineDifference> Differences)
{
    public bool ModsMatch => !Differences.Any(item => item.Area == BaselineDifferenceArea.GameData);
    public bool SettingsMatch => !Differences.Any(item =>
        item.Area is BaselineDifferenceArea.Difficulty or BaselineDifferenceArea.ModConfiguration);
    public bool ReadyToLaunch => ModsMatch && SettingsMatch;
}

public sealed class CampaignBaselineBuilder
{
    private static readonly HashSet<string> ProtectedGameDataFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Squad",
        "SquadExpansion",
        "KerbalSpaceRace",
        "KerbalSpaceRaceNationSelector",
        "KerbalSpaceRaceSuite",
        "ContractPacks",
        "KSRParameterLogger",
        "KSRDisableDBSUI"
    };

    public async Task<CampaignBaselinePackage> CreateAsync(
        string campaignName,
        string savePath,
        string outputRoot,
        IEnumerable<string>? ignoredGameDataFolders = null,
        IProgress<BaselineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(campaignName)) throw new ArgumentException("Enter a campaign name.");
        var selection = KspCareerSaveLocator.Resolve(savePath);
        var ignoredFolders = NormalizeIgnoredFolders(ignoredGameDataFolders);
        var created = DateTimeOffset.UtcNow;
        var safeName = string.Concat(campaignName.Trim().Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_')).Trim('_');
        if (safeName.Length == 0) safeName = "campaign";
        var directory = Path.Combine(outputRoot, $"{created:yyyyMMddTHHmmssZ}_{safeName}");
        Directory.CreateDirectory(directory);

        try
        {
            var masterSavePath = Path.Combine(directory, "master-save.zip");
            var saveFiles = await CreateMasterSaveAsync(selection.SavePath, masterSavePath, progress, cancellationToken);
            var gameDataMods = await InventoryGameDataAsync(
                selection.KspRoot, ignoredFolders, progress, cancellationToken);
            var settings = ReadSettings(selection.SavePath);
            var manifest = new CampaignBaselineManifest
            {
                CampaignName = campaignName.Trim(),
                SourceSaveName = selection.SaveName,
                KspVersion = ReadKspVersion(selection.KspRoot),
                CreatedAtUtc = created,
                MasterSaveSha256 = await PackageService.ComputeSha256Async(masterSavePath, cancellationToken),
                MasterSaveSize = new FileInfo(masterSavePath).Length,
                IgnoredGameDataFolders = ignoredFolders,
                SaveFiles = saveFiles,
                GameDataFiles = [],
                GameDataMods = gameDataMods,
                Settings = settings
            };
            var manifestPath = Path.Combine(directory, "baseline.json");
            await using (var stream = File.Create(manifestPath))
                await JsonSerializer.SerializeAsync(stream, manifest, ManifestService.JsonOptions, cancellationToken);
            progress?.Report(new BaselineProgress("Baseline ready", 1, 1, null));
            return new CampaignBaselinePackage(directory, manifestPath, masterSavePath, manifest);
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    public static async Task<CampaignBaselineManifest> LoadAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<CampaignBaselineManifest>(stream, ManifestService.JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The campaign baseline is empty.");
        if (manifest.SchemaVersion != 1 || manifest.SaveFiles.Count == 0)
            throw new InvalidDataException("The campaign baseline is incomplete or unsupported.");
        return manifest;
    }

    internal static async Task<List<BaselineMod>> InventoryGameDataAsync(
        string kspRoot,
        IReadOnlyCollection<string> ignoredFolders,
        IProgress<BaselineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var gameDataRoot = Path.Combine(kspRoot, "GameData");
        progress?.Report(new BaselineProgress("Discovering GameData mods", 0, 0, null));
        var roots = await Task.Run(() => EnumerateModRoots(gameDataRoot, ignoredFolders).ToList(), cancellationToken);
        var result = new List<BaselineMod>(roots.Count);
        for (var index = 0; index < roots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = roots[index];
            SafePaths.RejectReparsePoints(gameDataRoot, item.Path);
            progress?.Report(new BaselineProgress("Reading GameData mod versions", index, roots.Count, item.Folder));
            result.Add(new BaselineMod(item.Folder, ReadDeclaredModVersion(item.Path)));
        }
        progress?.Report(new BaselineProgress("GameData inventory complete", roots.Count, roots.Count, null));
        return result.OrderBy(item => item.Folder, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<(string Folder, string Path)> EnumerateModRoots(
        string gameDataRoot,
        IReadOnlyCollection<string> ignoredFolders)
    {
        foreach (var directory in Directory.EnumerateDirectories(gameDataRoot, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (IsStockGameDataFolder(name) || ignoredFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (name.Equals("ContractPacks", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pack in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                             .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                    yield return ($"ContractPacks/{Path.GetFileName(pack)}", pack);
                continue;
            }
            yield return (name, directory);
        }
    }

    internal static bool IsStockGameDataFolder(string folder) =>
        folder.Equals("Squad", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("SquadExpansion", StringComparison.OrdinalIgnoreCase);

    private static string? ReadDeclaredModVersion(string directory)
    {
        var declared = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.version", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!TryGetProperty(root, "VERSION", out var version)) continue;
                var text = VersionText(version);
                if (!string.IsNullOrWhiteSpace(text))
                    declared.Add($"{SafePaths.ManifestPath(Path.GetRelativePath(directory, path))}={text}");
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (declared.Count > 0) return string.Join(";", declared);

        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var version = AssemblyName.GetAssemblyName(path).Version;
                if (version is not null)
                    declared.Add($"{SafePaths.ManifestPath(Path.GetRelativePath(directory, path))}={version}");
            }
            catch (BadImageFormatException) { }
            catch (FileLoadException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return declared.Count == 0 ? null : string.Join(";", declared);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static string? VersionText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind != JsonValueKind.Object) return null;
        var values = new List<string>();
        foreach (var name in new[] { "MAJOR", "MINOR", "PATCH", "BUILD" })
        {
            if (!TryGetProperty(element, name, out var value))
            {
                if (name is "MAJOR" or "MINOR") return null;
                continue;
            }
            values.Add(value.ToString());
        }
        return values.Count >= 2 ? string.Join('.', values) : null;
    }

    public static List<string> NormalizeIgnoredFolders(IEnumerable<string>? folders)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in folders ?? [])
        {
            var folder = raw.Trim();
            if (folder.Length == 0) continue;
            if (folder is "." or ".." || Path.IsPathRooted(folder) || folder.Contains('/') || folder.Contains('\\') ||
                folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"Invalid ignored GameData folder name: '{raw}'. Enter only its direct folder name.");
            if (ProtectedGameDataFolders.Contains(folder))
                throw new InvalidDataException($"'{folder}' is a protected KSP/KSR folder and cannot be ignored.");
            result.Add(folder);
        }
        return result.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsProtectedGameDataFolder(string folderName) =>
        ProtectedGameDataFolders.Contains(folderName.Trim());

    internal static bool IsIncludedSaveFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        if (SafePaths.ManifestPath(relativePath).Split('/').Any(segment =>
                segment.Equals("KSR_Backups", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("KSR_CampaignData", StringComparison.OrdinalIgnoreCase))) return false;
        return !fileName.StartsWith("quicksave", StringComparison.OrdinalIgnoreCase) &&
               !fileName.StartsWith("autosave", StringComparison.OrdinalIgnoreCase) &&
               !fileName.StartsWith("KCT_Backup", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".loadmeta", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<BaselineFile>> CreateMasterSaveAsync(
        string saveRoot,
        string destination,
        IProgress<BaselineProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new BaselineProgress("Discovering Master Save files", 0, 0, null));
        var files = await Task.Run(() =>
            Directory.EnumerateFiles(saveRoot, "*", SearchOption.AllDirectories)
                .Select(path => (Full: path, Relative: SafePaths.ManifestPath(Path.GetRelativePath(saveRoot, path))))
                .Where(item => IsIncludedSaveFile(item.Relative))
                .OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)
                .ToList(), cancellationToken);
        var descriptions = new List<BaselineFile>(files.Count);
        await using var destinationStream = File.Create(destination);
        using var archive = new ZipArchive(destinationStream, ZipArchiveMode.Create, false);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = files[index];
            SafePaths.RejectReparsePoints(saveRoot, item.Full);
            progress?.Report(new BaselineProgress("Packaging Master Save", index, files.Count, item.Relative));
            var entry = archive.CreateEntry(item.Relative, CompressionLevel.Optimal);
            await using (var input = File.OpenRead(item.Full))
            await using (var output = entry.Open())
                await input.CopyToAsync(output, cancellationToken);
            descriptions.Add(new BaselineFile(item.Relative, new FileInfo(item.Full).Length,
                await PackageService.ComputeSha256Async(item.Full, cancellationToken)));
        }
        return descriptions;
    }

    private static List<BaselineSetting> ReadSettings(string saveRoot)
    {
        var settings = new List<BaselineSetting>();
        var persistent = Path.Combine(saveRoot, "persistent.sfs");
        settings.AddRange(KspConfigSnapshot.ReadValues(persistent, "persistent.sfs", true)
            .Select(item => new BaselineSetting("persistent.sfs", item.Key, item.Value, KspConfigSnapshot.DisplayName(item.Key))));
        foreach (var config in Directory.EnumerateFiles(saveRoot, "*.cfg", SearchOption.TopDirectoryOnly)
                     .Where(path => IsIncludedSaveFile(Path.GetFileName(path))))
        {
            var source = Path.GetFileName(config);
            settings.AddRange(KspConfigSnapshot.ReadValues(config, source, false)
                .Select(item => new BaselineSetting(source, item.Key, item.Value, KspConfigSnapshot.DisplayName(item.Key))));
        }
        return settings.OrderBy(item => item.Source).ThenBy(item => item.Key).ToList();
    }

    internal static string ReadKspVersion(string kspRoot)
    {
        foreach (var name in new[] { "buildID64.txt", "buildID.txt" })
        {
            var path = Path.Combine(kspRoot, name);
            if (!File.Exists(path)) continue;
            var build = File.ReadLines(path).FirstOrDefault(line =>
                line.TrimStart().StartsWith("build id", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(build)) return build.Trim();
        }
        return "unknown";
    }
}

public sealed class CampaignBaselineComparer
{
    public async Task<CampaignComplianceResult> CompareAsync(
        CampaignBaselineManifest baseline,
        string savePath,
        IProgress<BaselineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selection = KspCareerSaveLocator.Resolve(savePath);
        var ignoredFolders = CampaignBaselineBuilder.NormalizeIgnoredFolders(baseline.IgnoredGameDataFolders);
        var actualMods = await CampaignBaselineBuilder.InventoryGameDataAsync(
            selection.KspRoot, ignoredFolders, progress, cancellationToken);
        var expectedMods = baseline.GameDataMods.Count > 0
            ? baseline.GameDataMods.Where(item => !CampaignBaselineBuilder.IsStockGameDataFolder(item.Folder.Split('/', 2)[0])).ToList()
            : LegacyModInventory(baseline.GameDataFiles, ignoredFolders);
        var differences = CompareMods(expectedMods, actualMods);
        var actualKspVersion = CampaignBaselineBuilder.ReadKspVersion(selection.KspRoot);
        if (!string.Equals(baseline.KspVersion, "[config]", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(baseline.KspVersion, "unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(baseline.KspVersion, actualKspVersion, StringComparison.OrdinalIgnoreCase))
            differences.Add(new(BaselineDifferenceArea.GameData, BaselineDifferenceKind.ValueMismatch,
                "KSP version", baseline.KspVersion, actualKspVersion, "KSP version"));

        var actualSettings = new List<BaselineSetting>();
        var persistent = Path.Combine(savePath, "persistent.sfs");
        actualSettings.AddRange(KspConfigSnapshot.ReadValues(persistent, "persistent.sfs", true)
            .Select(item => new BaselineSetting("persistent.sfs", item.Key, item.Value, KspConfigSnapshot.DisplayName(item.Key))));
        foreach (var source in baseline.Settings.Select(item => item.Source).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(source => !source.Equals("persistent.sfs", StringComparison.OrdinalIgnoreCase)))
        {
            var path = Path.Combine(savePath, source);
            if (!File.Exists(path)) continue;
            actualSettings.AddRange(KspConfigSnapshot.ReadValues(path, source, false)
                .Select(item => new BaselineSetting(source, item.Key, item.Value, KspConfigSnapshot.DisplayName(item.Key))));
        }
        differences.AddRange(CompareSettings(baseline.Settings, actualSettings));
        return new CampaignComplianceResult(differences);
    }

    private static List<BaselineMod> LegacyModInventory(
        IEnumerable<BaselineFile> files,
        IReadOnlyCollection<string> ignoredFolders)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var parts = SafePaths.ManifestPath(file.Path).Split('/');
            if (parts.Length == 0 || CampaignBaselineBuilder.IsStockGameDataFolder(parts[0]) ||
                ignoredFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) continue;
            var folder = parts[0].Equals("ContractPacks", StringComparison.OrdinalIgnoreCase) && parts.Length > 1
                ? $"ContractPacks/{parts[1]}"
                : parts[0];
            folders.Add(folder);
        }
        return folders.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Select(folder => new BaselineMod(folder, null)).ToList();
    }

    private static List<BaselineDifference> CompareMods(
        IEnumerable<BaselineMod> expected,
        IEnumerable<BaselineMod> actual)
    {
        var expectedMap = expected.ToDictionary(item => item.Folder, StringComparer.OrdinalIgnoreCase);
        var actualMap = actual.ToDictionary(item => item.Folder, StringComparer.OrdinalIgnoreCase);
        var differences = new List<BaselineDifference>();
        foreach (var item in expectedMap.Values)
        {
            if (!actualMap.TryGetValue(item.Folder, out var current))
                differences.Add(new(BaselineDifferenceArea.GameData, BaselineDifferenceKind.Missing,
                    item.Folder, item.Version, null, item.Folder));
            else if (!string.IsNullOrWhiteSpace(item.Version) &&
                     !string.Equals(item.Version, current.Version, StringComparison.OrdinalIgnoreCase))
                differences.Add(new(BaselineDifferenceArea.GameData, BaselineDifferenceKind.ValueMismatch,
                    item.Folder, item.Version, current.Version ?? "version not declared", $"{item.Folder} version"));
        }
        foreach (var item in actualMap.Values.Where(item => !expectedMap.ContainsKey(item.Folder)))
            differences.Add(new(BaselineDifferenceArea.GameData, BaselineDifferenceKind.Extra,
                item.Folder, null, item.Version, item.Folder));
        return differences;
    }

    private static IEnumerable<BaselineDifference> CompareSettings(
        IEnumerable<BaselineSetting> expected,
        IEnumerable<BaselineSetting> actual)
    {
        static string Identity(BaselineSetting item) => $"{item.Source}|{item.Key}";
        var expectedMap = expected.ToDictionary(Identity, StringComparer.OrdinalIgnoreCase);
        var actualMap = actual.ToDictionary(Identity, StringComparer.OrdinalIgnoreCase);
        foreach (var item in expectedMap.Values)
        {
            var area = item.Source.Equals("persistent.sfs", StringComparison.OrdinalIgnoreCase)
                ? BaselineDifferenceArea.Difficulty : BaselineDifferenceArea.ModConfiguration;
            if (!actualMap.TryGetValue(Identity(item), out var current))
                yield return new(area, BaselineDifferenceKind.Missing, item.Source, item.Value, null, item.DisplayName);
            else if (!string.Equals(item.Value, current.Value, StringComparison.Ordinal))
                yield return new(area, BaselineDifferenceKind.ValueMismatch, item.Source, item.Value, current.Value, item.DisplayName);
        }
    }
}

public sealed record SettingsAlignmentResult(string BackupDirectory, int FilesUpdated);

public sealed class CampaignSettingsAligner
{
    public async Task<SettingsAlignmentResult> AlignAsync(
        CampaignBaselinePackage package,
        string targetSavePath,
        CampaignComplianceResult compliance,
        CancellationToken cancellationToken = default)
    {
        KspCareerSaveLocator.Resolve(targetSavePath);
        if (!compliance.ModsMatch)
            throw new InvalidOperationException("Gameplay settings cannot be aligned while GameData differs from the campaign baseline.");
        var sources = compliance.Differences
            .Where(item => item.Area is BaselineDifferenceArea.Difficulty or BaselineDifferenceArea.ModConfiguration)
            .Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sources.Count == 0) return new SettingsAlignmentResult(string.Empty, 0);

        var backupDirectory = Path.Combine(targetSavePath, "KSR_Backups", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ"));
        Directory.CreateDirectory(backupDirectory);
        var updated = 0;
        using var archive = ZipFile.OpenRead(package.MasterSavePath);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(source) != source)
                throw new InvalidDataException($"Unsafe campaign setting path: {source}");
            var entry = archive.GetEntry(SafePaths.ManifestPath(source))
                ?? throw new InvalidDataException($"The Master Save does not contain {source}.");
            var target = Path.Combine(targetSavePath, source);
            if (!File.Exists(target)) throw new FileNotFoundException($"The player save does not contain {source}.", target);
            File.Copy(target, Path.Combine(backupDirectory, source), true);

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
            var baselineText = await reader.ReadToEndAsync(cancellationToken);
            var replacement = source.Equals("persistent.sfs", StringComparison.OrdinalIgnoreCase)
                ? KspConfigSnapshot.ReplaceNamedNode(await File.ReadAllTextAsync(target, cancellationToken), baselineText, "PARAMETERS")
                : baselineText;
            var temporary = target + ".ksr-aligning";
            await File.WriteAllTextAsync(temporary, replacement, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, target, true);
            updated++;
        }
        return new SettingsAlignmentResult(backupDirectory, updated);
    }
}

internal static class KspConfigSnapshot
{
    public static bool ContainsSupportedCampaignMode(string path) =>
        File.ReadLines(path).Take(300).Any(line =>
        {
            var value = line.Trim();
            return string.Equals(value, "mode = CAREER", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "mode = SCIENCE_SANDBOX", StringComparison.OrdinalIgnoreCase);
        });

    public static IReadOnlyList<KeyValuePair<string, string>> ReadValues(string path, string source, bool parametersOnly)
    {
        var result = new List<KeyValuePair<string, string>>();
        var stack = new List<string>();
        string? pendingNode = null;
        var duplicateCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line == "{")
            {
                stack.Add(pendingNode ?? "NODE");
                pendingNode = null;
                continue;
            }
            if (line == "}")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                pendingNode = null;
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals < 0)
            {
                pendingNode = line;
                continue;
            }
            if (parametersOnly && !stack.Any(node => node.Equals("PARAMETERS", StringComparison.OrdinalIgnoreCase))) continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            var identity = $"{string.Join('/', stack)}/{key}".TrimStart('/');
            duplicateCounts.TryGetValue(identity, out var count);
            duplicateCounts[identity] = count + 1;
            if (count > 0) identity += $"[{count + 1}]";
            result.Add(new(identity, value));
        }
        return result;
    }

    public static string DisplayName(string key)
    {
        var name = key[(key.LastIndexOf('/') + 1)..];
        name = name.Replace('_', ' ');
        var builder = new StringBuilder();
        for (var index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index]) && char.IsLower(name[index - 1])) builder.Append(' ');
            builder.Append(name[index]);
        }
        return builder.ToString();
    }

    public static string ReplaceNamedNode(string targetText, string baselineText, string nodeName)
    {
        var targetLines = SplitLines(targetText);
        var baselineLines = SplitLines(baselineText);
        var targetRange = FindNodeRange(targetLines, nodeName);
        var baselineRange = FindNodeRange(baselineLines, nodeName);
        if (targetRange is null || baselineRange is null)
            throw new InvalidDataException($"The {nodeName} section could not be aligned safely.");
        var output = new List<string>(targetLines.Count - (targetRange.Value.End - targetRange.Value.Start + 1) +
                                      (baselineRange.Value.End - baselineRange.Value.Start + 1));
        output.AddRange(targetLines.Take(targetRange.Value.Start));
        output.AddRange(baselineLines.Skip(baselineRange.Value.Start)
            .Take(baselineRange.Value.End - baselineRange.Value.Start + 1));
        output.AddRange(targetLines.Skip(targetRange.Value.End + 1));
        return string.Join(Environment.NewLine, output) + Environment.NewLine;
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();

    private static (int Start, int End)? FindNodeRange(IReadOnlyList<string> lines, string nodeName)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (!string.Equals(lines[index].Trim(), nodeName, StringComparison.OrdinalIgnoreCase)) continue;
            var open = index + 1;
            while (open < lines.Count && string.IsNullOrWhiteSpace(lines[open])) open++;
            if (open >= lines.Count || lines[open].Trim() != "{") continue;
            var depth = 0;
            for (var current = open; current < lines.Count; current++)
            {
                var trimmed = lines[current].Trim();
                if (trimmed == "{") depth++;
                else if (trimmed == "}" && --depth == 0) return (index, current);
            }
        }
        return null;
    }
}
