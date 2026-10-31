using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Migrations;

/// <summary>
/// A consistent copy of the open database taken with the SQLite online backup API, never a file copy
/// that could miss the WAL (T-09, ADR 0015).
/// </summary>
/// <remarks>
/// Only the database is copied here. The full backup format with <c>configuration.json</c> and a
/// checksummed manifest belongs to the backup service.
/// </remarks>
internal static class SafetyBackup
{
    /// <returns>The new backup directory, <c>backups/&lt;yyyyMMddTHHmmssZ&gt;-&lt;kind&gt;</c>.</returns>
    public static string Create(SqliteConnection source, string backupsDirectory, DateTimeOffset utcNow, string kind)
    {
        var stamp = utcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var directory = Path.Combine(backupsDirectory, $"{stamp}-{kind}");
        for (var suffix = 2; Directory.Exists(directory); suffix++)
        {
            directory = Path.Combine(backupsDirectory, string.Create(CultureInfo.InvariantCulture, $"{stamp}-{kind}-{suffix}"));
        }

        Directory.CreateDirectory(directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "data.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using var destination = new SqliteConnection(connectionString);
        destination.Open();
        source.BackupDatabase(destination);
        return directory;
    }
}
