using MaxPlanner.Application.Common;

namespace MaxPlanner.Application.Startup;

/// <summary>The result of a restore started from Settings › Data (T-09).</summary>
public enum RestoreRunResult
{
    /// <summary>The dataset was replaced and reopened.</summary>
    Restored,

    /// <summary>The swap failed and the previous data was put back, or the marker was kept for recovery.</summary>
    RolledBack,

    /// <summary>Nothing was replaced (no dataset open, or the restore-safety backup failed).</summary>
    NotStarted,
}

/// <summary>What the session needs from the coordinator.</summary>
/// <param name="Restore">Runs a validated restore; the coordinator owns the session it closes and reopens.</param>
/// <param name="OnFirstReady">Called once, on the UI thread, when the session's first view is usable.</param>
public sealed record DatasetSessionHooks(Func<IStagedRestore, Task<RestoreRunResult>> Restore, Action OnFirstReady);

/// <summary>The services and pages of the one open dataset, shown in the shell.</summary>
public interface IDatasetSession : IAsyncDisposable
{
    IDatasetBackups Backups { get; }

    /// <summary>Starts the first projection load; <see cref="DatasetSessionHooks.OnFirstReady"/> follows when it is ready.</summary>
    void Start();
}

/// <summary>Builds the per-dataset services over an opened dataset and attaches its pages to the shell.</summary>
public interface IDatasetSessionFactory
{
    /// <summary>Takes ownership of <paramref name="dataset"/>; disposing the session closes it (T-06.6). Does not start loading.</summary>
    IDatasetSession Create(IOpenedDataset dataset, DatasetSessionHooks hooks);
}

/// <summary>Why the dataset was not opened at startup.</summary>
public enum StartupRecoveryKind
{
    /// <summary>ADR 0015: <c>restore.pending.json</c> exists; the dataset is not opened.</summary>
    InterruptedRestore,

    /// <summary><c>data.db</c> is missing although the folder was used before (T-09, AC-15).</summary>
    DatabaseMissing,

    /// <summary>The file exists but could not be opened; see <see cref="StartupRecovery.Error"/>.</summary>
    OpenFailed,
}

/// <param name="Error">For <see cref="StartupRecoveryKind.OpenFailed"/>: the typed failure of opening.</param>
/// <param name="SafetyBackupDirectory">For <see cref="StartupRecoveryKind.OpenFailed"/>: the migration safety backup, if one was taken.</param>
public sealed record StartupRecovery(StartupRecoveryKind Kind, AppError? Error = null, string? SafetyBackupDirectory = null);

/// <summary>An app-wide restore outcome the shell reports (design §2.7), because the page that asked has closed.</summary>
public enum DataNotice
{
    /// <summary>Settings › Data restore finished; the safety backup folder is given.</summary>
    RestoreDone,

    /// <summary>Settings › Data restore was rolled back; the safety backup folder is given. An error.</summary>
    RestoreRolledBack,

    /// <summary>The interrupted restore was finished (rolled forward).</summary>
    InterruptedRestoreFinished,

    /// <summary>The interrupted restore was undone (rolled back).</summary>
    InterruptedRestoreUndone,
}

/// <summary>
/// What the startup coordinator shows and asks in the window. Implemented by the Desktop host over the shell; every
/// member is called on the UI thread.
/// </summary>
public interface IStartupShell
{
    /// <summary>The shell shows a failure (an unexpected startup failure or a failed projection load).</summary>
    bool HasFailed { get; }

    void ShowStarting();

    /// <summary>Shows the recovery page, whose actions call back into the coordinator.</summary>
    void ShowRecovery(StartupRecovery recovery);

    /// <summary>An unexpected startup failure with no automatic retry.</summary>
    void ShowUnexpectedFailure();

    /// <summary>A restore closes the dataset: edits stop and the shell shows the starting state until it reopens.</summary>
    void BeginDatasetReplacement();

    /// <summary>Closing has begun: block edits from now on.</summary>
    void BeginShutdown();

    /// <summary>The dataset is closing; its pages are about to be disposed and must no longer be shown.</summary>
    void DetachWorkspace();

    /// <param name="safetyBackupDirectory">The restore-safety backup, for <see cref="DataNotice.RestoreDone"/> and <see cref="DataNotice.RestoreRolledBack"/>.</param>
    void ReportDataNotice(DataNotice notice, string? safetyBackupDirectory = null);

    void ReportBackupFailed(StorageFailureKind? kind);

    /// <summary>Asks the user for a backup folder to restore from.</summary>
    /// <returns>The chosen folder, or null when the user cancelled.</returns>
    Task<string?> PickBackupFolderAsync();
}

/// <summary>The facts about this launch that the coordinator needs.</summary>
/// <param name="HasPriorUse">The data folder was used before this launch (decides first-run creation).</param>
/// <param name="AppVersion">Written into backup manifests and the open log.</param>
/// <param name="ElapsedMilliseconds">Milliseconds since the process started (T-11 startup timing).</param>
public sealed record StartupEnvironment(bool HasPriorUse, string AppVersion, Func<long> ElapsedMilliseconds);

public enum CreateDatasetResult
{
    Created,

    /// <summary>A database file appeared in the meantime; nothing was overwritten.</summary>
    AlreadyExists,

    Failed,
}

/// <summary>The result of a restore started from the recovery page (ADR 0015).</summary>
public enum RecoveryRestoreResult
{
    /// <summary>The restored or recovered dataset is in place and was opened.</summary>
    Completed,

    /// <summary>An interrupted restore was undone; the previous data is back and was opened.</summary>
    RolledBack,

    /// <summary>Neither copy could be verified; nothing was guessed and the marker is kept (AC-15).</summary>
    Unresolved,

    /// <summary>The user cancelled the folder picker.</summary>
    Cancelled,

    /// <summary>The chosen backup failed validation; nothing was replaced.</summary>
    Invalid,

    Failed,
}

/// <summary>Why the process exits in the current state (ADR 0003 maps each to an exit code).</summary>
public enum StartupExit
{
    Ok,

    /// <summary>The recovery page was showing a dataset written by a newer app version.</summary>
    NewerSchema,

    /// <summary>The recovery page was showing, or startup failed before the first usable view.</summary>
    StartupFailure,
}
