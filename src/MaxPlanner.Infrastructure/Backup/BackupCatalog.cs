using System.Globalization;
using System.Text.RegularExpressions;

namespace MaxPlanner.Infrastructure.Backup;

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

    [GeneratedRegex(@"^(?<stamp>\d{8}T\d{6}Z)-(?<kind>[a-z]+(?:-[a-z]+)*)(?:-\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FolderName();
}
