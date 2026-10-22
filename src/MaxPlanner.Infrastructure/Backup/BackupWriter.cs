using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MaxPlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Backup;

/// <summary>Writes one complete backup folder in the ADR 0015 format.</summary>
internal static class BackupWriter
{
    public const string DatabaseFileName = "data.db";
    public const string ConfigurationFileName = "configuration.json";

    /// <summary>
    /// Copies the open dataset with the SQLite online backup API, plus <c>configuration.json</c> when present,
    /// and a checksummed manifest, into <c>backups/&lt;yyyyMMddTHHmmssZ&gt;-&lt;kind&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The folder is assembled under <c>data/temp</c> and moved into <c>backups/</c> only when complete, so
    /// a crash or failure never leaves a partial folder that looks like a backup. The copy reads one WAL
    /// snapshot on a read-only connection, so concurrent commits are neither blocked nor half-copied.
    /// </remarks>
    /// <returns>The full path of the new backup folder.</returns>
    public static string Create(SqliteDataset dataset, string kind, DateTimeOffset utcNow, string appVersion)
    {
        var paths = dataset.Paths;
        var staging = Path.Combine(paths.TempDirectory, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var databaseCopy = Path.Combine(staging, DatabaseFileName);
            CopyDatabase(dataset, databaseCopy);
            var files = new List<BackupManifestFile> { Describe(databaseCopy) };
            if (File.Exists(paths.ConfigurationFile))
            {
                var configurationCopy = Path.Combine(staging, ConfigurationFileName);
                File.Copy(paths.ConfigurationFile, configurationCopy);
                files.Add(Describe(configurationCopy));
            }

            var manifest = new BackupManifest(
                BackupManifest.CurrentFormatVersion, appVersion, dataset.SchemaVersion, dataset.Id.Value, kind, utcNow.ToUniversalTime(), files);
            File.WriteAllBytes(Path.Combine(staging, BackupManifest.FileName), JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJsonContext.Default.BackupManifest));

            Directory.CreateDirectory(paths.BackupsDirectory);
            var destination = UniqueFolder(paths.BackupsDirectory, kind, utcNow);
            Directory.Move(staging, destination);
            return destination;
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>The folder-name timestamp, which sorts chronologically as text.</summary>
    public static string Stamp(DateTimeOffset utcNow) =>
        utcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static void CopyDatabase(SqliteDataset dataset, string destinationFile)
    {
        var destinationConnection = new SqliteConnectionStringBuilder
        {
            DataSource = destinationFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using (var source = dataset.Connections.OpenReader())
        using (var destination = new SqliteConnection(destinationConnection))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }

        // The copied header still says WAL. Reopening and switching to a rollback journal makes the backup a
        // single self-contained file with no -wal/-shm side files.
        using var reopened = new SqliteConnection(destinationConnection);
        reopened.Open();
        using var command = reopened.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = DELETE;";
        command.ExecuteNonQuery();
    }

    private static BackupManifestFile Describe(string file)
    {
        using var stream = File.OpenRead(file);
        var hash = SHA256.HashData(stream);
        return new BackupManifestFile(Path.GetFileName(file), stream.Length, Convert.ToHexStringLower(hash));
    }

    private static string UniqueFolder(string backupsDirectory, string kind, DateTimeOffset utcNow)
    {
        var stamp = Stamp(utcNow);
        var folder = Path.Combine(backupsDirectory, $"{stamp}-{kind}");
        for (var suffix = 2; Directory.Exists(folder); suffix++)
        {
            folder = Path.Combine(backupsDirectory, string.Create(CultureInfo.InvariantCulture, $"{stamp}-{kind}-{suffix}"));
        }

        return folder;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging lives under data/temp, which is app scratch space; a leftover never counts as a backup.
        }
    }
}
