using System.Globalization;
using System.Text.RegularExpressions;

namespace MaxPlanner.Infrastructure.Backup;

/// <summary>Whether a backup folder's manifest is present, parsable, and matches the files it lists (by name and size).</summary>
public enum BackupManifestState
{
    Valid,
    Missing,
    Invalid,
}

/// <summary>One backup folder as Manage backups lists it (T-09): kind, date, size, and manifest state.</summary>
public sealed record BackupEntry(string Path, string Name, DateTimeOffset CreatedAtUtc, string Kind, long SizeBytes, BackupManifestState Manifest)
{
    /// <summary>Only user-created backups can be deleted from the app; daily and safety backups follow retention (ADR 0015).</summary>
    public bool IsManual => Kind == BackupKinds.Manual;
}

/// <summary>A complete backup folder as Settings › Data reports it (T-09).</summary>
/// <param name="Kind">One of <see cref="BackupKinds"/>.</param>
public sealed record BackupFolderInfo(string Path, DateTimeOffset CreatedAtUtc, string Kind);

/// <summary>
/// Reads the backup folders under <c>data/backups</c> by their ADR 0015 names
/// (<c>yyyyMMddTHHmmssZ-kind[-n]</c>). Only complete folders (with a manifest) count; nothing is opened or changed.
/// </summary>
public static partial class BackupCatalog
{
    /// <summary>The newest complete backup of any kind, or null when there is none. Performs file I/O.</summary>
    public static BackupFolderInfo? FindLatest(string backupsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        if (!Directory.Exists(backupsDirectory))
        {
            return null;
        }

        BackupFolderInfo? latest = null;
        foreach (var path in Directory.EnumerateDirectories(backupsDirectory))
        {
            var match = FolderName().Match(System.IO.Path.GetFileName(path));
            if (!match.Success
                || !DateTimeOffset.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
                || !File.Exists(System.IO.Path.Combine(path, BackupManifest.FileName)))
            {
                continue;
            }

            if (latest is null || created > latest.CreatedAtUtc)
            {
                latest = new BackupFolderInfo(path, created, match.Groups["kind"].Value);
            }
        }

        return latest;
    }

    /// <summary>Every backup folder by its ADR 0015 name, newest first, with its manifest state. Performs file I/O.</summary>
    public static IReadOnlyList<BackupEntry> List(string backupsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        if (!Directory.Exists(backupsDirectory))
        {
            return [];
        }

        var entries = new List<BackupEntry>();
        foreach (var path in Directory.EnumerateDirectories(backupsDirectory))
        {
            var name = System.IO.Path.GetFileName(path);
            var match = FolderName().Match(name);
            if (!match.Success
                || !DateTimeOffset.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created))
            {
                continue;
            }

            var size = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            entries.Add(new BackupEntry(path, name, created, match.Groups["kind"].Value, size, ManifestState(path)));
        }

        return [.. entries.OrderByDescending(e => e.CreatedAtUtc).ThenByDescending(e => e.Name, StringComparer.Ordinal)];
    }

    /// <summary>Reads a folder's manifest; null when it is missing or cannot be parsed.</summary>
    public static BackupManifest? ReadManifest(string folder)
    {
        var file = System.IO.Path.Combine(folder, BackupManifest.FileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(file), BackupJsonContext.Default.BackupManifest);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static BackupManifestState ManifestState(string folder)
    {
        if (!File.Exists(System.IO.Path.Combine(folder, BackupManifest.FileName)))
        {
            return BackupManifestState.Missing;
        }

        // A cheap check for the list; restore verifies every SHA-256 before anything is replaced.
        return ReadManifest(folder) is { } manifest
            && manifest.Files.All(f => new FileInfo(System.IO.Path.Combine(folder, f.Name)) is { Exists: true } info && info.Length == f.Length)
            ? BackupManifestState.Valid
            : BackupManifestState.Invalid;
    }

    [GeneratedRegex(@"^(?<stamp>\d{8}T\d{6}Z)-(?<kind>[a-z]+(?:-[a-z]+)*)(?:-\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FolderName();
}
