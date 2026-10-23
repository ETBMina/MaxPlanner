using MaxPlanner.Application.Common;
using MaxPlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Backup;

/// <summary>User-requested backup operations of Settings › Data (T-09, ADR 0015).</summary>
public static class BackupMaintenance
{
    /// <summary>Writes a backup of the open dataset with the online backup API. Manual backups are never pruned.</summary>
    /// <param name="kind">One of <see cref="BackupKinds"/>; the Data page uses manual and import-safety.</param>
    /// <returns>The new backup folder, or a <see cref="StorageFailureError"/>; a failure leaves no partial folder in <c>backups/</c>.</returns>
    public static AppResult<string> Create(SqliteDataset dataset, string kind, DateTimeOffset utcNow, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        try
        {
            return AppResult.Success(BackupWriter.Create(dataset, kind, utcNow, appVersion));
        }
        catch (Exception ex) when (Classify(ex) is { } failure)
        {
            return AppResult.Failure<string>(new StorageFailureError(failure));
        }
    }

    private static StorageFailureKind? Classify(Exception exception) =>
        SqliteFailures.Classify(exception) ?? exception switch
        {
            // ERROR_HANDLE_DISK_FULL (39) and ERROR_DISK_FULL (112).
            IOException io when (io.HResult & 0xFFFF) is 39 or 112 => StorageFailureKind.DiskFull,
            IOException or SqliteException => StorageFailureKind.Io,
            UnauthorizedAccessException => StorageFailureKind.Unavailable,
            _ => null,
        };

    /// <summary>
    /// Deletes one manual backup after the user confirmed it. Daily and safety backups are refused: retention owns them,
    /// and a safety backup may be the only copy from before a restore, import, or upgrade.
    /// </summary>
    /// <returns>False when the folder is not a manual backup directly under <paramref name="backupsDirectory"/>.</returns>
    public static bool DeleteManual(string backupsDirectory, string backupFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFolder);
        var root = Path.GetFullPath(backupsDirectory);
        var folder = Path.GetFullPath(backupFolder);
        if (!string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase)
            || BackupCatalog.List(root).FirstOrDefault(e => string.Equals(Path.GetFullPath(e.Path), folder, StringComparison.OrdinalIgnoreCase)) is not { IsManual: true })
        {
            return false;
        }

        Directory.Delete(folder, recursive: true);
        return true;
    }
}
