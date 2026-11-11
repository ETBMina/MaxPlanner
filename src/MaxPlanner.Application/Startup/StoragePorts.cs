using MaxPlanner.Application.Common;

namespace MaxPlanner.Application.Startup;

/// <summary>What opening or creating the dataset found (T-09, AC-15).</summary>
public enum DatasetOpenOutcome
{
    /// <summary>The dataset is open and at the supported schema version.</summary>
    Opened,

    /// <summary><c>data/data.db</c> does not exist. Nothing was created.</summary>
    Missing,

    /// <summary>Creation was refused because database files already exist; nothing was overwritten.</summary>
    AlreadyExists,

    /// <summary>The file exists but could not be opened: see <see cref="DatasetOpenAttempt.Error"/>. It was not replaced.</summary>
    Failed,
}

/// <summary>An open dataset, handed from <see cref="IDatasetStorage"/> to <see cref="IDatasetSessionFactory"/>, which owns it from then on.</summary>
public interface IOpenedDataset : IAsyncDisposable
{
    Guid Id { get; }

    int SchemaVersion { get; }
}

/// <param name="Error">
/// For <see cref="DatasetOpenOutcome.Failed"/>: <see cref="StorageFailureError"/>, <see cref="UnsupportedSchemaError"/>,
/// or <see cref="MigrationFailedError"/>.
/// </param>
/// <param name="SafetyBackupDirectory">The migration safety backup, when an upgrade took one (including a failed upgrade).</param>
public sealed record DatasetOpenAttempt(DatasetOpenOutcome Outcome, IOpenedDataset? Dataset, AppError? Error, string? SafetyBackupDirectory = null);

/// <summary>
/// Opens and creates the portable dataset (ADR 0009). Opening never creates a database; only the explicit
/// <see cref="CreateNewAsync"/> does, and it refuses when database files exist.
/// </summary>
public interface IDatasetStorage
{
    /// <summary>ADR 0015: whether <c>restore.pending.json</c> exists. Performs file I/O; call off the UI thread.</summary>
    bool HasRestoreMarker();

    Task<DatasetOpenAttempt> OpenExistingAsync();

    Task<DatasetOpenAttempt> CreateNewAsync();
}

/// <param name="Kind">The check, for example <c>Checksums</c>.</param>
/// <param name="Detail">A non-sensitive detail, for example the schema versions found and supported.</param>
public sealed record RestoreCheckResult(string Kind, bool Passed, string? Detail);

/// <summary>A backup staged and validated for restore (ADR 0015 steps 1-3). Nothing in <c>data/</c> has changed yet.</summary>
public interface IStagedRestore
{
    bool IsValid { get; }

    IReadOnlyList<RestoreCheckResult> Checks { get; }

    DateTimeOffset? BackupCreatedAtUtc { get; }

    string? BackupKind { get; }
}

/// <summary>How an interrupted restore found at startup was resolved (ADR 0015).</summary>
public enum InterruptedRestoreOutcome
{
    /// <summary>The restored database is in place; the marker was removed.</summary>
    RolledForward,

    /// <summary>The previous database was put back; the marker was removed.</summary>
    RolledBack,

    /// <summary>Neither copy could be verified; the marker is kept and nothing was guessed (AC-15).</summary>
    Unresolved,
}

/// <summary>
/// The ADR 0015 restore protocol over the data folder. Every member performs file I/O; call off the UI thread.
/// </summary>
public interface IRestoreOperations
{
    /// <summary>Stages <paramref name="backupFolder"/> under <c>data/temp</c> and runs every check on the copy.</summary>
    IStagedRestore Validate(string backupFolder, CancellationToken cancellationToken);

    /// <summary>Removes a staged restore that will not be applied.</summary>
    void Discard(IStagedRestore staged);

    /// <summary>A raw, checksummed copy of the current database files, for a restore when the dataset cannot be opened.</summary>
    /// <returns>The safety folder, or null when there was no database file to keep.</returns>
    string? CreateRawSafetyCopy(DateTimeOffset utcNow);

    /// <summary>Replaces the database with the staged copy under the restore marker. The dataset must be closed.</summary>
    /// <returns>True when the restored database is in place; false when the swap was rolled back (marker removed).</returns>
    /// <exception cref="IOException">The swap could neither finish nor roll back; the marker stays for startup recovery.</exception>
    bool Swap(IStagedRestore staged);

    /// <summary>Finishes or undoes an interrupted swap from its marker. Never substitutes an empty database.</summary>
    InterruptedRestoreOutcome RecoverInterrupted();
}

/// <summary>The backup kinds the app writes on request (ADR 0015); daily backups belong to <see cref="IDatasetBackups.RunDailyAsync"/>.</summary>
public enum BackupKind
{
    Manual,
    ImportSafety,
    RestoreSafety,
}

/// <param name="Kind">The ADR 0015 kind, for example <c>manual</c> or <c>restore-safety</c>.</param>
/// <param name="ManifestState"><c>Valid</c>, <c>Missing</c>, or <c>Invalid</c>.</param>
public sealed record BackupSummary(string Path, string Name, DateTimeOffset CreatedAtUtc, string Kind, long SizeBytes, string ManifestState, bool IsManual);

/// <param name="FailureKind">Why the backup failed, when <paramref name="IsFailed"/>.</param>
public sealed record DailyBackupReport(bool IsFailed, StorageFailureKind? FailureKind = null);

/// <summary>The backups of the open dataset (T-09, ADR 0015).</summary>
public interface IDatasetBackups
{
    string BackupsDirectory { get; }

    /// <summary>Writes a backup with the online backup API. Performs file I/O; call off the UI thread.</summary>
    /// <returns>The new backup folder, or a <see cref="StorageFailureError"/>.</returns>
    AppResult<string> Create(BackupKind kind, DateTimeOffset utcNow);

    /// <summary>The automatic backup of the first successful open of each local day, with retention. Storage failures are reported, not thrown.</summary>
    /// <exception cref="OperationCanceledException">Cancelled before the copy started.</exception>
    Task<DailyBackupReport> RunDailyAsync(CancellationToken cancellationToken);

    /// <summary>Every backup folder, newest first. Performs file I/O.</summary>
    IReadOnlyList<BackupSummary> List();

    /// <summary>Deletes one manual backup; any other kind or location is refused (false). Performs file I/O.</summary>
    bool DeleteManual(string backupFolder);
}
