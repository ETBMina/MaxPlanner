using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using MaxPlanner.Infrastructure.Persistence.Migrations;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaxPlanner.Infrastructure.Persistence;

/// <summary>Services and settings for opening a dataset.</summary>
/// <param name="Time">Clock for audit timestamps and local "today"; the system clock when null.</param>
/// <param name="LoggerFactory">Null disables logging.</param>
/// <param name="WriteQueueCapacity">Bound of the ordered writer queue; callers wait when it is full.</param>
public sealed record SqliteDatasetOptions(TimeProvider? Time = null, ILoggerFactory? LoggerFactory = null, int WriteQueueCapacity = 256)
{
    /// <summary>Overrides the migration list (tests of the migration runner only).</summary>
    internal IReadOnlyList<SchemaMigration>? Migrations { get; init; }
}

public enum DatasetOpenStatus
{
    /// <summary>The dataset is open and at the supported schema version.</summary>
    Opened,

    /// <summary><c>data/data.db</c> does not exist. Nothing was created (T-09, AC-15).</summary>
    Missing,

    /// <summary>Creation was refused because database files already exist; nothing was overwritten.</summary>
    AlreadyExists,

    /// <summary>The file exists but could not be opened: see <see cref="DatasetOpenResult.Error"/>. It was not replaced.</summary>
    Failed,
}

/// <param name="Error">
/// For <see cref="DatasetOpenStatus.Failed"/>: <see cref="StorageFailureError"/> (Corrupt for a damaged or
/// foreign file), <see cref="UnsupportedSchemaError"/>, or <see cref="MigrationFailedError"/>.
/// </param>
/// <param name="SafetyBackupDirectory">The migration safety backup, when an upgrade took one (including a failed upgrade).</param>
public sealed record DatasetOpenResult(DatasetOpenStatus Status, SqliteDataset? Dataset, AppError? Error, string? SafetyBackupDirectory = null);

/// <summary>
/// One open portable dataset: the ordered writer with its change feed, and the snapshot reader (ADR 0009).
/// </summary>
/// <remarks>
/// Opening never creates a database: an empty dataset is made only by the explicit <see cref="CreateNewAsync"/>
/// (T-09, AC-15). A file that cannot be read, fails <c>quick_check</c>, or is not a Max Planner dataset is
/// reported and left untouched. The caller holds the dataset lock (<see cref="DatasetLock"/>) first.
/// </remarks>
public sealed class SqliteDataset : IAsyncDisposable
{
    private readonly SqliteConnectionFactory _connections;

    private SqliteDataset(DataPaths paths, SqliteConnectionFactory connections, DatasetId id, int schemaVersion, SqliteMutationCoordinator coordinator, ILoggerFactory loggers)
    {
        Paths = paths;
        _connections = connections;
        Id = id;
        SchemaVersion = schemaVersion;
        Coordinator = coordinator;
        Snapshots = new SqliteReadSnapshotSource(connections, loggers.CreateLogger<SqliteReadSnapshotSource>());
    }

    public DataPaths Paths { get; }

    public DatasetId Id { get; }

    public int SchemaVersion { get; }

    /// <summary>The ordered writer; also the dataset's <see cref="IChangeFeed"/> and <see cref="IOperationStatusQuery"/>.</summary>
    public SqliteMutationCoordinator Coordinator { get; }

    public IReadSnapshotSource Snapshots { get; }

    /// <summary>Read-only connections for consistent online backups alongside the writer (ADR 0015).</summary>
    internal SqliteConnectionFactory Connections => _connections;

    /// <summary>The schema version this app version reads and writes.</summary>
    public static int SupportedSchemaVersion => SchemaMigrations.All[^1].Version;

    /// <summary>Opens an existing <c>data/data.db</c>, upgrading an older schema after a safety backup.</summary>
    public static Task<DatasetOpenResult> OpenExistingAsync(DataPaths paths, SqliteDatasetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Task.Run(() => OpenExisting(paths, options ?? new SqliteDatasetOptions()));
    }

    /// <summary>
    /// Creates a new dataset with the B-04 defaults. Only the explicit first-run path calls this (slice 1f
    /// decides when); it refuses when database files already exist.
    /// </summary>
    public static Task<DatasetOpenResult> CreateNewAsync(DataPaths paths, SqliteDatasetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Task.Run(() => CreateNew(paths, options ?? new SqliteDatasetOptions()));
    }

    /// <summary>Drains queued writes, checkpoints and closes the writer, and releases pooled readers (T-06.6).</summary>
    public async ValueTask DisposeAsync()
    {
        // Idle readers are released first so the writer is normally the last connection to close; its
        // TRUNCATE checkpoint and close then remove the WAL. A reader still loading is released when it returns.
        _connections.ClearReaderPool();
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        _connections.ClearReaderPool();
    }

    private static DatasetOpenResult OpenExisting(DataPaths paths, SqliteDatasetOptions options)
    {
        if (!File.Exists(paths.DatabaseFile))
        {
            return new(DatasetOpenStatus.Missing, null, null);
        }

        var connections = new SqliteConnectionFactory(paths.DatabaseFile);
        return Open(paths, connections, options, isNew: false);
    }

    private static DatasetOpenResult CreateNew(DataPaths paths, SqliteDatasetOptions options)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        if (File.Exists(paths.DatabaseFile) || File.Exists(paths.DatabaseFile + "-wal"))
        {
            return new(DatasetOpenStatus.AlreadyExists, null, null);
        }

        var connections = new SqliteConnectionFactory(paths.DatabaseFile);
        var result = Open(paths, connections, options, isNew: true);
        if (result.Status != DatasetOpenStatus.Opened)
        {
            // Nothing but this call's own empty file can be lost here; leaving it would make the next
            // launch report a foreign database instead of offering creation again.
            connections.ClearReaderPool();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                File.Delete(paths.DatabaseFile + suffix);
            }
        }

        return result;
    }

    private static DatasetOpenResult Open(DataPaths paths, SqliteConnectionFactory connections, SqliteDatasetOptions options, bool isNew)
    {
        var loggers = options.LoggerFactory ?? NullLoggerFactory.Instance;
        var logger = loggers.CreateLogger<SqliteDataset>();
        var time = options.Time ?? TimeProvider.System;
        var migrator = new SchemaMigrator(options.Migrations ?? SchemaMigrations.All, logger);
        SqliteConnection? writer = null;
        try
        {
            writer = connections.OpenWriterConnection(createIfMissing: isNew);

            // Inspect the header before anything writes to the file: a foreign, damaged, or newer file must
            // be reported unchanged (T-09, T-03.8).
            var version = SchemaMigrator.ReadUserVersion(writer);
            if (version > migrator.SupportedVersion)
            {
                return Failed(new UnsupportedSchemaError(version, migrator.SupportedVersion));
            }

            if (!isNew && (version == 0 || !QuickCheckPasses(writer)))
            {
                // Version 0 means the file is not a Max Planner dataset; it is never initialized silently.
                return Failed(new StorageFailureError(StorageFailureKind.Corrupt));
            }

            SqliteConnectionFactory.ConfigureWriter(writer);
            var migration = migrator.Migrate(writer, version, paths, time.GetUtcNow());
            if (migration.Error is { } migrationError)
            {
                return Failed(migrationError) with { SafetyBackupDirectory = migration.SafetyBackupDirectory };
            }

            var (datasetId, schemaVersion, sequence) = ReadMetadata(writer);
            if (schemaVersion != SchemaMigrator.ReadUserVersion(writer))
            {
                return Failed(new StorageFailureError(StorageFailureKind.Corrupt));
            }

            var coordinator = new SqliteMutationCoordinator(
                writer, sequence, time, loggers.CreateLogger<SqliteMutationCoordinator>(), options.WriteQueueCapacity);
            writer = null;
            var dataset = new SqliteDataset(paths, connections, datasetId, schemaVersion, coordinator, loggers);
            return new(DatasetOpenStatus.Opened, dataset, null, migration.SafetyBackupDirectory);
        }
        catch (Exception ex) when (SqliteFailures.Classify(ex) is { } kind)
        {
            PersistenceLog.OpenFailed(logger, kind, ex);
            return Failed(new StorageFailureError(kind));
        }
        catch (SqliteException ex)
        {
            // Any other SQL error here (for example a missing app_metadata table) means the file is not a
            // readable Max Planner dataset of a known version.
            PersistenceLog.OpenFailed(logger, StorageFailureKind.Corrupt, ex);
            return Failed(new StorageFailureError(StorageFailureKind.Corrupt));
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private static DatasetOpenResult Failed(AppError error) => new(DatasetOpenStatus.Failed, null, error);

    private static bool QuickCheckPasses(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetString(0) == "ok" && !reader.Read();
    }

    private static (DatasetId Id, int SchemaVersion, long CommitSequence) ReadMetadata(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dataset_id, schema_version, commit_sequence FROM app_metadata WHERE id = 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (new DatasetId(reader.GetGuid(0, "app_metadata", "dataset_id")), reader.GetInt32(1), reader.GetInt64(2))
            : throw new CorruptStoredDataException("app_metadata", "id", "row missing");
    }
}
