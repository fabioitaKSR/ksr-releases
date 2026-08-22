using System.Text.Json.Serialization;

namespace KsrLauncher.Core;

public sealed class ReleaseManifest
{
    public int SchemaVersion { get; set; }
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public string MinimumLauncherVersion { get; set; } = "1.0.0";
    public List<ComponentManifest> Components { get; set; } = [];
    public List<string> Preserve { get; set; } = [];
    public List<string> ManagedFilesInsidePreservedPaths { get; set; } = [];
    public List<string> Delete { get; set; } = [];
    public KspCompatibility Ksp { get; set; } = new();
}

public sealed class ComponentManifest
{
    public string Id { get; set; } = "";
    public string TransactionGroup { get; set; } = "ksp-client";
    public string Asset { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Source { get; set; } = "";
    public string TargetKind { get; set; } = "ksp";
    public string Target { get; set; } = "";
    public bool Required { get; set; }
    public List<string> RequiredFiles { get; set; } = [];
}

public sealed class KspCompatibility
{
    public string MinimumVersion { get; set; } = "1.12.0";
    public string MaximumVersion { get; set; } = "1.12.99";
}

public sealed class InstalledState
{
    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = "KerbalSpaceRace";
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Dictionary<string, InstalledComponent> Components { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InstalledComponent
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string TargetKind { get; set; } = "ksp";
    public string Target { get; set; } = "";
}

public sealed record LauncherLocations(string KspRoot, string LauncherDataRoot);

public enum UpdatePolicy
{
    ExistingOnly,
    InstallOrRepair
}

public sealed record ComponentPlan(
    ComponentManifest Component,
    string? InstalledVersion,
    bool IsPresent,
    bool NeedsUpdate,
    string Reason,
    string TargetPath);

public sealed record UpdatePlan(ReleaseManifest Manifest, IReadOnlyList<ComponentPlan> Components)
{
    public bool HasUpdates => Components.Any(item => item.NeedsUpdate);
}

public sealed class BackupJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string ReleaseVersion { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string KspRoot { get; set; } = "";
    public string LauncherDataRoot { get; set; } = "";
    public List<BackupEntry> Entries { get; set; } = [];
}

public sealed class BackupEntry
{
    public string ComponentId { get; set; } = "";
    public string TargetKind { get; set; } = "ksp";
    public string Target { get; set; } = "";
    public string BackupRelativePath { get; set; } = "";
    public bool HadOriginal { get; set; }
}

public sealed record UpdateResult(UpdatePlan Plan, bool Applied, string? BackupDirectory);

public sealed record UpdateProgress(
    string ComponentId,
    long BytesDownloaded,
    long TotalBytes,
    int ComponentsCompleted,
    int TotalComponents);
