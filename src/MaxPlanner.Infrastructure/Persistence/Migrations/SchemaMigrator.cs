using System.Globalization;
using MaxPlanner.Application.Common;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Infrastructure.Persistence.Migrations;

/// <param name="Error">Null when the schema is now current.</param>
/// <param name="SafetyBackupDirectory">The backup taken before upgrading an existing dataset, if one was taken.</param>
internal readonly record struct MigrationResult(AppError? Error, string? SafetyBackupDirectory);

/// <summary>
/// Brings a dataset to the supported schema version (T-09). The version lives in <c>PRAGMA user_version</c>
/// and in <c>app_metadata.schema_version</c>, both written in the same transaction as each step.
/// </summary>
internal sealed class SchemaMigrator
{
    private readonly IReadOnlyList<SchemaMigration> _migrations;
    private readonly ILogger _logger;

    public SchemaMigrator(IReadOnlyList<SchemaMigration> migrations, ILogger logger)
    {
        if (migrations.Count == 0 || migrations.Select((m, i) => m.Version != i + 1).Any(gap => gap))
        {
            throw new ArgumentException("Migrations must be numbered consecutively from 1.", nameof(migrations));
        }

        _migrations = migrations;
        _logger = logger;
    }

    public int SupportedVersion => _migrations[^1].Version;

    public static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Applies every migration above <paramref name="currentVersion"/>, each in its own transaction. An
    /// existing dataset (version ≥ 1) is first copied with the online backup API into
    /// <c>backups/&lt;utc&gt;-migration-safety/</c>; a failed step rolls back and leaves that copy.
    /// </summary>
    /// <param name="currentVersion">Must not exceed <see cref="SupportedVersion"/>; the caller refuses newer files first.</param>
    public MigrationResult Migrate(SqliteConnection writer, int currentVersion, DataPaths paths, DateTimeOffset utcNow)
    {
        if (currentVersion > SupportedVersion)
        {
            return new(new UnsupportedSchemaError(currentVersion, SupportedVersion), null);
        }

        if (currentVersion == SupportedVersion)
        {
            return new(null, null);
        }

        string? backup = null;
        if (currentVersion > 0)
        {
            try
            {
                backup = SafetyBackup.Create(writer, paths.BackupsDirectory, utcNow, "migration-safety");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                // T-09: never migrate without the safety copy.
                PersistenceLog.SafetyBackupFailed(_logger, currentVersion, ex);
                return new(new StorageFailureError(SqliteFailures.Classify(ex) ?? StorageFailureKind.Io), null);
            }
        }

        foreach (var migration in _migrations.Where(m => m.Version > currentVersion))
        {
            try
            {
                RunStep(writer, migration, utcNow);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PersistenceLog.MigrationFailed(_logger, migration.Version, currentVersion, ex);
                return new(new MigrationFailedError(currentVersion, migration.Version), backup);
            }

            PersistenceLog.MigrationApplied(_logger, migration.Version, migration.Name);
            currentVersion = migration.Version;
        }

        return new(null, backup);
    }

    private static void RunStep(SqliteConnection writer, SchemaMigration migration, DateTimeOffset utcNow)
    {
        using var transaction = writer.BeginTransaction(deferred: false);
        migration.Apply(writer, transaction, utcNow);

        string history;
        using (var read = writer.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT migration_history_json FROM app_metadata WHERE id = 1;";
            history = read.ExecuteScalar() as string ?? throw new CorruptStoredDataException("app_metadata", "migration_history_json", "row missing");
        }

        var entries = StorageJson.ReadHistory(history);
        entries.Add(new MigrationHistoryEntry(migration.Version, migration.Name, utcNow));
        using (var update = writer.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE app_metadata SET schema_version = $version, migration_history_json = $history WHERE id = 1;";
            update.Add("$version", migration.Version);
            update.Add("$history", StorageJson.WriteHistory(entries));
            update.ExecuteNonQuery();
        }

        using (var version = writer.CreateCommand())
        {
            // PRAGMA takes no parameters; the value is an integer from this assembly, never user input.
            version.Transaction = transaction;
            version.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Version};");
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
