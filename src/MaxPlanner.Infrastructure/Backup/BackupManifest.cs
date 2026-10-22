using System.Text.Json.Serialization;

namespace MaxPlanner.Infrastructure.Backup;

/// <summary>The backup kinds of ADR 0015; each is the suffix of its folder name.</summary>
public static class BackupKinds
{
    public const string Daily = "daily";
    public const string Manual = "manual";
    public const string MigrationSafety = "migration-safety";
    public const string RestoreSafety = "restore-safety";
    public const string ImportSafety = "import-safety";
}

/// <summary><c>manifest.json</c> of one backup folder (ADR 0015). It does not list itself.</summary>
/// <param name="CreatedAtUtc">When the database copy was taken, in UTC.</param>
public sealed record BackupManifest(
    int FormatVersion,
    string AppVersion,
    int SchemaVersion,
    Guid DatasetId,
    string Kind,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<BackupManifestFile> Files)
{
    public const int CurrentFormatVersion = 1;

    public const string FileName = "manifest.json";
}

/// <param name="Sha256">Lowercase hexadecimal SHA-256 of the file's bytes.</param>
public sealed record BackupManifestFile(string Name, long Length, string Sha256);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
