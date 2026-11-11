using MaxPlanner.Application.Common;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Application.Startup;

/// <summary>
/// Opens the dataset after the window is shown and decides between normal use, first-run creation, and the
/// recovery page (T-09, AC-15); runs the restore protocol's app side (ADR 0015) and the daily backup; and, on close,
/// the T-06.6 shutdown. Runs on the UI thread; file and database work is awaited off it.
/// </summary>
/// <remarks>
/// <para>
/// Decision table for <see cref="IDatasetStorage.OpenExistingAsync"/>:
/// a restore marker → recovery (InterruptedRestore) without opening; Opened → load; Missing in a folder
/// with no prior use → create the first dataset; Missing otherwise → recovery offering a confirmed create;
/// Failed → recovery for the specific error. An empty dataset is never substituted automatically.
/// </para>
/// <para>
/// Awaits deliberately keep the caller's synchronization context (no <c>ConfigureAwait(false)</c>): every
/// <see cref="IStartupShell"/> call must run on the UI thread.
/// </para>
/// </remarks>
public sealed partial class StartupCoordinator : IDisposable
{
    private readonly IDatasetStorage _storage;
    private readonly IRestoreOperations _restores;
    private readonly IDatasetSessionFactory _sessions;
    private readonly IStartupShell _shell;
    private readonly TimeProvider _time;
    private readonly StartupEnvironment _environment;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private IDatasetSession? _session;
    private StartupRecovery? _recovery;
    private Task _backupTask = Task.CompletedTask;
    private bool _hasReachedReady;

    public StartupCoordinator(
        IDatasetStorage storage,
        IRestoreOperations restores,
        IDatasetSessionFactory sessions,
        IStartupShell shell,
        TimeProvider time,
        StartupEnvironment environment,
        ILogger<StartupCoordinator> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _restores = restores ?? throw new ArgumentNullException(nameof(restores));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The recovery the shell shows now, or null when none is shown.</summary>
    public StartupRecovery? Recovery => _recovery;

    /// <summary>Why the process would exit if it closed in the current state (ADR 0003).</summary>
    public StartupExit Exit => _recovery is { } recovery
        ? recovery is { Kind: StartupRecoveryKind.OpenFailed, Error: UnsupportedSchemaError } ? StartupExit.NewerSchema : StartupExit.StartupFailure
        : _shell.HasFailed && !_hasReachedReady ? StartupExit.StartupFailure : StartupExit.Ok;

    public Task RunAsync() => GuardAsync(() => OpenAsync(allowFirstRun: true));

    /// <summary>Creates a new empty dataset; called only after the user confirmed it on the recovery page (T-09, AC-15).</summary>
    public async Task<CreateDatasetResult> CreateNewDatasetAsync()
    {
        Log.CreateConfirmed(_logger);
        var outcome = CreateDatasetResult.Failed;
        await GuardAsync(async () =>
        {
            var result = await _storage.CreateNewAsync();
            switch (result.Outcome)
            {
                case DatasetOpenOutcome.Opened:
                    StartSession(result.Dataset!);
                    outcome = CreateDatasetResult.Created;
                    break;
                case DatasetOpenOutcome.AlreadyExists:
                    Log.CreateRefused(_logger);
                    outcome = CreateDatasetResult.AlreadyExists;
                    break;
                default:
                    Log.CreateFailed(_logger, result.Error?.Code);
                    break;
            }
        });
        return outcome;
    }

    public Task RetryOpenAsync() => GuardAsync(() => OpenAsync(allowFirstRun: false));

    /// <summary>Called by the app container after <see cref="ShutdownAsync"/> has finished.</summary>
    public void Dispose() => _lifetime.Dispose();

    /// <summary>
    /// T-06.6: stops edits, waits for the daily backup, disposes the pages and store, then drains and closes the
    /// dataset (WAL checkpoint). Awaited from the closing window without blocking the UI thread.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _shell.BeginShutdown();
        await _lifetime.CancelAsync();
        try
        {
            await _backupTask;
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the copy started; nothing was written.
        }

        _shell.DetachWorkspace();
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    /// <summary>ADR 0015: finishes or undoes an interrupted restore from its marker, then opens the dataset.</summary>
    public async Task<RecoveryRestoreResult> FinishInterruptedRestoreAsync()
    {
        var outcome = await Task.Run(_restores.RecoverInterrupted);
        Log.RestoreRecovered(_logger, outcome.ToString());
        if (outcome == InterruptedRestoreOutcome.Unresolved)
        {
            return RecoveryRestoreResult.Unresolved;
        }

        await GuardAsync(() => OpenAsync(allowFirstRun: false));
        _shell.ReportDataNotice(outcome == InterruptedRestoreOutcome.RolledForward ? DataNotice.InterruptedRestoreFinished : DataNotice.InterruptedRestoreUndone);
        return outcome == InterruptedRestoreOutcome.RolledForward ? RecoveryRestoreResult.Completed : RecoveryRestoreResult.RolledBack;
    }

    /// <summary>
    /// T-09 from the recovery page (no dataset is open): validate the chosen backup in staging, keep a raw copy of the
    /// current database files, swap under the marker, and open. Never creates an empty database.
    /// </summary>
    public async Task<RecoveryRestoreResult> RestoreFromBackupAsync()
    {
        if (await _shell.PickBackupFolderAsync() is not { } folder)
        {
            return RecoveryRestoreResult.Cancelled;
        }

        try
        {
            var staged = await Task.Run(() => _restores.Validate(folder, CancellationToken.None));
            if (!staged.IsValid)
            {
                _restores.Discard(staged);
                return RecoveryRestoreResult.Invalid;
            }

            var safety = await Task.Run(() => _restores.CreateRawSafetyCopy(_time.GetUtcNow()));
            Log.RestoreStarting(_logger, safety is null ? "none" : Path.GetFileName(safety));
            var swapped = await Task.Run(() => _restores.Swap(staged));
            Log.RestoreFinished(_logger, swapped);
            await GuardAsync(() => OpenAsync(allowFirstRun: false));
            return swapped ? RecoveryRestoreResult.Completed : RecoveryRestoreResult.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.RestoreSwapFailed(_logger, ex);
            await GuardAsync(() => OpenAsync(allowFirstRun: false));
            return RecoveryRestoreResult.Failed;
        }
    }

    /// <summary>
    /// T-09 / ADR 0015 restore from Settings › Data: a restore-safety backup of the open dataset, then the T-06.6 close
    /// (pages, store, drained writer), the marker-guarded swap, and a normal reopen with a full projection load. The
    /// outcome is reported in the shell, because the page that asked belonged to the closed session.
    /// </summary>
    private async Task<RestoreRunResult> RestoreAsync(IStagedRestore staged)
    {
        if (_session is not { } session)
        {
            return RestoreRunResult.NotStarted;
        }

        var safety = await Task.Run(() => session.Backups.Create(BackupKind.RestoreSafety, _time.GetUtcNow()));
        if (!safety.IsSuccess)
        {
            // No safety backup, no restore: the current data stays open and untouched.
            Log.RestoreSafetyFailed(_logger, (safety.Error as StorageFailureError)?.Kind);
            return RestoreRunResult.NotStarted;
        }

        Log.RestoreStarting(_logger, Path.GetFileName(safety.Value));
        _shell.BeginDatasetReplacement();
        await CloseSessionAsync();
        bool swapped;
        try
        {
            swapped = await Task.Run(() => _restores.Swap(staged));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The marker stays; opening shows the interrupted-restore recovery, which finishes or undoes the swap.
            Log.RestoreSwapFailed(_logger, ex);
            await GuardAsync(() => OpenAsync(allowFirstRun: false));
            return RestoreRunResult.RolledBack;
        }

        Log.RestoreFinished(_logger, swapped);
        await GuardAsync(() => OpenAsync(allowFirstRun: false));
        _shell.ReportDataNotice(swapped ? DataNotice.RestoreDone : DataNotice.RestoreRolledBack, safety.Value);
        return swapped ? RestoreRunResult.Restored : RestoreRunResult.RolledBack;
    }

    private async Task CloseSessionAsync()
    {
        try
        {
            await _backupTask;
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the copy started; nothing was written.
        }

        _shell.DetachWorkspace();
        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync();
        }
    }

    // Boundary of every startup step started from the UI: anything unexpected is logged and shown in the
    // shell instead of escaping into a command or the dispatcher.
    private async Task GuardAsync(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            Log.StartupCrashed(_logger, ex);
            _recovery = null;
            _shell.ShowUnexpectedFailure();
        }
    }

    private async Task OpenAsync(bool allowFirstRun)
    {
        _recovery = null;
        _shell.ShowStarting();
        if (await Task.Run(_storage.HasRestoreMarker))
        {
            ShowRecovery(new StartupRecovery(StartupRecoveryKind.InterruptedRestore));
            return;
        }

        var result = await _storage.OpenExistingAsync();
        if (result.Outcome == DatasetOpenOutcome.Missing && allowFirstRun && !_environment.HasPriorUse)
        {
            Log.FirstRun(_logger);
            result = await _storage.CreateNewAsync();
            if (result.Outcome == DatasetOpenOutcome.AlreadyExists)
            {
                // Files appeared between the inspection and creation; open what is there instead.
                result = await _storage.OpenExistingAsync();
            }
        }

        switch (result.Outcome)
        {
            case DatasetOpenOutcome.Opened:
                StartSession(result.Dataset!);
                break;
            case DatasetOpenOutcome.Missing:
                ShowRecovery(new StartupRecovery(StartupRecoveryKind.DatabaseMissing));
                break;
            default:
                ShowRecovery(new StartupRecovery(
                    StartupRecoveryKind.OpenFailed,
                    result.Error ?? new StorageFailureError(StorageFailureKind.Corrupt),
                    result.SafetyBackupDirectory));
                break;
        }
    }

    private void ShowRecovery(StartupRecovery recovery)
    {
        _recovery = recovery;
        _shell.ShowRecovery(recovery);
    }

    private void StartSession(IOpenedDataset dataset)
    {
        _recovery = null;
        // Owned before it is shown, so a failure while attaching is still closed by the next close or shutdown.
        _session = _sessions.Create(dataset, new DatasetSessionHooks(RestoreAsync, OnFirstReady));
        _session.Attach();
        var elapsedMs = _environment.ElapsedMilliseconds();
        Log.DatasetOpened(_logger, dataset.Id, dataset.SchemaVersion, _environment.AppVersion, elapsedMs);
        _session.Start();
    }

    private void OnFirstReady()
    {
        _hasReachedReady = true;

        // ADR 0015: after the first view is usable, so the backup never delays startup. The online backup
        // reads one WAL snapshot and runs alongside commits.
        _backupTask = RunDailyBackupAsync(_session!.Backups);
    }

    private async Task RunDailyBackupAsync(IDatasetBackups backups)
    {
        try
        {
            var result = await backups.RunDailyAsync(_lifetime.Token);
            if (result.IsFailed)
            {
                _shell.ReportBackupFailed(result.FailureKind);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing started before the backup did.
        }
        catch (Exception ex)
        {
            // Defects are not storage failures, but the user still learns that today's backup is missing.
            Log.BackupDefect(_logger, ex);
            _shell.ReportBackupFailed(null);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "First run: no dataset and no prior use of the data folder; creating a new dataset.")]
        public static partial void FirstRun(ILogger logger);

        [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Dataset {DatasetId} opened (schema {SchemaVersion}, app {AppVersion}) at {ElapsedMs} ms after launch.")]
        public static partial void DatasetOpened(ILogger logger, Guid datasetId, int schemaVersion, string appVersion, long elapsedMs);

        [LoggerMessage(EventId = 5005, Level = LogLevel.Information, Message = "The user confirmed creating a new empty dataset from the recovery page.")]
        public static partial void CreateConfirmed(ILogger logger);

        [LoggerMessage(EventId = 5006, Level = LogLevel.Warning, Message = "Creating a dataset was refused because database files already exist.")]
        public static partial void CreateRefused(ILogger logger);

        [LoggerMessage(EventId = 5007, Level = LogLevel.Error, Message = "Creating a dataset failed ({ErrorCode}).")]
        public static partial void CreateFailed(ILogger logger, string? errorCode);

        [LoggerMessage(EventId = 5008, Level = LogLevel.Critical, Message = "Startup failed unexpectedly.")]
        public static partial void StartupCrashed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 5009, Level = LogLevel.Error, Message = "The daily backup failed unexpectedly.")]
        public static partial void BackupDefect(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 5011, Level = LogLevel.Error, Message = "Restore refused: the restore-safety backup failed ({Failure}).")]
        public static partial void RestoreSafetyFailed(ILogger logger, StorageFailureKind? failure);

        [LoggerMessage(EventId = 5012, Level = LogLevel.Warning, Message = "Restore starting; safety backup {Safety}.")]
        public static partial void RestoreStarting(ILogger logger, string safety);

        [LoggerMessage(EventId = 5013, Level = LogLevel.Information, Message = "Restore swap finished; restored {Swapped}.")]
        public static partial void RestoreFinished(ILogger logger, bool swapped);

        [LoggerMessage(EventId = 5014, Level = LogLevel.Error, Message = "Restore swap failed; the restore marker is kept for recovery.")]
        public static partial void RestoreSwapFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 5015, Level = LogLevel.Warning, Message = "Interrupted restore recovery: {Outcome:l}.")]
        public static partial void RestoreRecovered(ILogger logger, string outcome);
    }
}
