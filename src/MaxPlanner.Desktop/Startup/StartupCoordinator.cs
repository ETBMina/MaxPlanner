using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MaxPlanner.Application.Common;
using MaxPlanner.Desktop.Composition;
using MaxPlanner.Desktop.Maintenance;
using MaxPlanner.Infrastructure.Backup;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Desktop.Startup;

/// <summary>
/// Opens the dataset after the window is shown and decides between normal use, first-run creation, and the
/// recovery page (T-09, AC-15); then runs the daily backup and, on close, the T-06.6 shutdown. Runs on the
/// UI thread; all file and database work is awaited off it.
/// </summary>
/// <remarks>
/// Decision table for <see cref="SqliteDataset.OpenExistingAsync"/>:
/// a restore marker → recovery (InterruptedRestore) without opening; Opened → load; Missing in a folder
/// with no prior use → create the first dataset; Missing otherwise → recovery offering a confirmed create;
/// Failed → recovery for the specific error. An empty dataset is never substituted automatically.
/// </remarks>
internal sealed partial class StartupCoordinator : IRecoveryActions, IDisposable
{
    private readonly StartupContext _context;
    private readonly IServiceProvider _appServices;
    private readonly ShellViewModel _shell;
    private readonly ILocalizedStrings _strings;
    private readonly IDataFolderLauncher _dataFolder;
    private readonly IScheduler _uiScheduler;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly SqliteDatasetOptions _datasetOptions;
    private readonly Action _requestClose;
    private readonly TimeProvider _time;
    private readonly IFilePickerService _pickers;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private DatasetSession? _session;
    private IDisposable? _readySubscription;
    private RecoveryInfo? _recovery;
    private Task _backupTask = Task.CompletedTask;
    private bool _hasReachedReady;

    public StartupCoordinator(
        StartupContext context,
        IServiceProvider appServices,
        ShellViewModel shell,
        ILocalizedStrings strings,
        IDataFolderLauncher dataFolder,
        IScheduler uiScheduler,
        IUiDispatcher uiDispatcher,
        TimeProvider time,
        IFilePickerService pickers,
        Action requestClose,
        ILogger<StartupCoordinator> logger)
    {
        _time = time;
        _pickers = pickers;
        _context = context;
        _appServices = appServices;
        _shell = shell;
        _strings = strings;
        _dataFolder = dataFolder;
        _uiScheduler = uiScheduler;
        _uiDispatcher = uiDispatcher;
        _requestClose = requestClose;
        _logger = logger;
        _datasetOptions = new SqliteDatasetOptions(time, context.LoggerFactory);
    }

    /// <summary>The ADR 0003 code for the process when it closes in the current state.</summary>
    public int ExitCode => _recovery is { } recovery
        ? ExitCodes.ForRecovery(recovery.Kind)
        : _shell.HasFailed && !_hasReachedReady ? ExitCodes.StartupFailure : ExitCodes.Ok;

    public Task RunAsync()
    {
        _shell.ReportPreferencesRecovery(_context.LoadedPreferences.Recovery);
        return GuardAsync(() => OpenAsync(allowFirstRun: true));
    }

    public async Task<CreateDatasetOutcome> CreateNewDatasetAsync()
    {
        Log.CreateConfirmed(_logger);
        var outcome = CreateDatasetOutcome.Failed;
        await GuardAsync(async () =>
        {
            var result = await SqliteDataset.CreateNewAsync(_context.Paths, _datasetOptions);
            switch (result.Status)
            {
                case DatasetOpenStatus.Opened:
                    StartSession(result.Dataset!);
                    outcome = CreateDatasetOutcome.Created;
                    break;
                case DatasetOpenStatus.AlreadyExists:
                    Log.CreateRefused(_logger);
                    outcome = CreateDatasetOutcome.AlreadyExists;
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
    public void Dispose()
    {
        _readySubscription?.Dispose();
        _lifetime.Dispose();
    }

    public void CloseApp() => _requestClose();

    /// <summary>
    /// T-06.6: stops edits, waits for the daily backup, disposes the store, then drains and closes the
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

        _readySubscription?.Dispose();
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell.DetachWorkspace();
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

    }

    /// <summary>
    /// T-09 / ADR 0015 restore from Settings › Data: a restore-safety backup of the open dataset, then the T-06.6 close
    /// (pages, store, drained writer), the marker-guarded swap, and a normal reopen with a full projection load. The
    /// outcome is reported in the shell, because the page that asked belonged to the closed session.
    /// </summary>
    private async Task<RestoreRunOutcome> RestoreAsync(RestoreValidation validation)
    {
        if (_session is not { } session)
        {
            return RestoreRunOutcome.NotStarted;
        }

        var safety = await Task.Run(() => BackupMaintenance.Create(session.Dataset, BackupKinds.RestoreSafety, _time.GetUtcNow(), StartupContext.AppVersion));
        if (!safety.IsSuccess)
        {
            // No safety backup, no restore: the current data stays open and untouched.
            Log.RestoreSafetyFailed(_logger, (safety.Error as StorageFailureError)?.Kind);
            return RestoreRunOutcome.NotStarted;
        }

        Log.RestoreStarting(_logger, Path.GetFileName(safety.Value));
        _shell.BeginDatasetReplacement();
        await CloseSessionAsync();
        bool swapped;
        try
        {
            swapped = await Task.Run(() => RestoreService.Swap(_context.Paths, validation));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The marker stays; opening shows the interrupted-restore recovery, which finishes or undoes the swap.
            Log.RestoreSwapFailed(_logger, ex);
            await GuardAsync(() => OpenAsync(allowFirstRun: false));
            return RestoreRunOutcome.RolledBack;
        }

        Log.RestoreFinished(_logger, swapped);
        await GuardAsync(() => OpenAsync(allowFirstRun: false));
        _shell.ReportDataNotice(
            swapped ? _strings.Format("Restore_DoneNotice", safety.Value) : _strings.Format("Restore_RolledBackNotice", safety.Value),
            isError: !swapped);
        return swapped ? RestoreRunOutcome.Restored : RestoreRunOutcome.RolledBack;
    }

    /// <summary>ADR 0015: finishes or undoes an interrupted restore from its marker, then opens the dataset.</summary>
    public async Task<RecoveryRestoreOutcome> FinishInterruptedRestoreAsync()
    {
        var outcome = await Task.Run(() => RestoreService.RecoverInterrupted(_context.Paths));
        Log.RestoreRecovered(_logger, outcome.ToString());
        if (outcome == RestoreRecoveryOutcome.Unresolved)
        {
            return RecoveryRestoreOutcome.Unresolved;
        }

        await GuardAsync(() => OpenAsync(allowFirstRun: false));
        _shell.ReportDataNotice(_strings.GetString(outcome == RestoreRecoveryOutcome.RolledForward ? "Recovery_RestoreFinishedNotice" : "Recovery_RestoreUndoneNotice"), isError: false);
        return outcome == RestoreRecoveryOutcome.RolledForward ? RecoveryRestoreOutcome.Completed : RecoveryRestoreOutcome.RolledBack;
    }

    /// <summary>
    /// T-09 from the recovery page (no dataset is open): validate the chosen backup in staging, keep a raw copy of the
    /// current database files, swap under the marker, and open. Never creates an empty database.
    /// </summary>
    public async Task<RecoveryRestoreOutcome> RestoreFromBackupAsync()
    {
        if (await _pickers.PickFolderAsync() is not { } folder)
        {
            return RecoveryRestoreOutcome.Cancelled;
        }

        try
        {
            var validation = await Task.Run(() => RestoreService.Validate(_context.Paths, folder, CancellationToken.None));
            if (!validation.IsValid)
            {
                RestoreService.Discard(validation);
                return RecoveryRestoreOutcome.Invalid;
            }

            var safety = await Task.Run(() => RestoreService.CreateRawSafetyCopy(_context.Paths, _time.GetUtcNow(), StartupContext.AppVersion));
            Log.RestoreStarting(_logger, safety is null ? "none" : Path.GetFileName(safety));
            var swapped = await Task.Run(() => RestoreService.Swap(_context.Paths, validation));
            Log.RestoreFinished(_logger, swapped);
            await GuardAsync(() => OpenAsync(allowFirstRun: false));
            return swapped ? RecoveryRestoreOutcome.Completed : RecoveryRestoreOutcome.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.RestoreSwapFailed(_logger, ex);
            await GuardAsync(() => OpenAsync(allowFirstRun: false));
            return RecoveryRestoreOutcome.Failed;
        }
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

        _readySubscription?.Dispose();
        _readySubscription = null;
        _shell.PropertyChanged -= OnShellPropertyChanged;
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
            _shell.MarkFailed(_strings.GetString("Shell_UnexpectedStartupFailure"));
        }
    }

    private async Task OpenAsync(bool allowFirstRun)
    {
        _recovery = null;
        _shell.ShowStarting();
        var paths = _context.Paths;
        if (await Task.Run(() => File.Exists(paths.RestoreMarkerFile)))
        {
            ShowRecovery(new RecoveryInfo(RecoveryKind.InterruptedRestore, paths.DataDirectory, paths.BackupsDirectory));
            return;
        }

        var result = await SqliteDataset.OpenExistingAsync(paths, _datasetOptions);
        if (result.Status == DatasetOpenStatus.Missing && allowFirstRun && !_context.Inspection.HasPriorUse)
        {
            Log.FirstRun(_logger);
            result = await SqliteDataset.CreateNewAsync(paths, _datasetOptions);
            if (result.Status == DatasetOpenStatus.AlreadyExists)
            {
                // Files appeared between the inspection and creation; open what is there instead.
                result = await SqliteDataset.OpenExistingAsync(paths, _datasetOptions);
            }
        }

        switch (result.Status)
        {
            case DatasetOpenStatus.Opened:
                StartSession(result.Dataset!);
                break;
            case DatasetOpenStatus.Missing:
                ShowRecovery(new RecoveryInfo(RecoveryKind.DatabaseMissing, paths.DataDirectory, paths.BackupsDirectory));
                break;
            default:
                ShowRecovery(RecoveryInfo.ForOpenFailure(
                    result.Error ?? new StorageFailureError(StorageFailureKind.Corrupt),
                    paths.DataDirectory,
                    paths.BackupsDirectory,
                    result.SafetyBackupDirectory));
                break;
        }
    }

    private void ShowRecovery(RecoveryInfo info)
    {
        _recovery = info;
        var elapsedMs = _context.ElapsedMilliseconds;
        var kind = info.Kind.ToString();
        var storageFailure = info.StorageFailure?.ToString() ?? "none";
        Log.Recovery(_logger, kind, storageFailure, elapsedMs);
        _shell.ShowRecovery(new RecoveryViewModel(info, _strings, this, _dataFolder));
    }

    private void StartSession(SqliteDataset dataset)
    {
        _recovery = null;
        _session = DatasetSession.Create(_appServices, dataset);
        var store = _session.Store;
        _shell.AttachStore(store, _uiScheduler);
        _shell.AttachWorkspace(_session.CreateWorkspace(_uiScheduler, _uiDispatcher, _strings, _pickers, RestoreAsync));
        _shell.PropertyChanged += OnShellPropertyChanged;
        _readySubscription = store.State
            .Where(state => state.Status == ProjectionStatus.Ready)
            .Take(1)
            .ObserveOn(_uiScheduler)
            .Subscribe(_ => OnFirstReady());
        var elapsedMs = _context.ElapsedMilliseconds;
        Log.DatasetOpened(_logger, dataset.Id.Value, dataset.SchemaVersion, StartupContext.AppVersion, elapsedMs);
        store.Start();
    }

    // T-11 / AC-17 evidence: every navigation logs the live store subscriptions and the working set, so a
    // navigation run shows whether either grows (ADR 0010 open point).
    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage) && _session is { } session)
        {
            var page = _shell.CurrentPage.ToString();
            var workingSetMiB = Environment.WorkingSet / (1024 * 1024);
            Log.Navigated(_logger, page, session.Store.ActiveCacheSubscriptions, workingSetMiB);
        }
    }

    private void OnFirstReady()
    {
        _hasReachedReady = true;
        var store = _session!.Store;
        var elapsedMs = _context.ElapsedMilliseconds;
        Log.Ready(_logger, elapsedMs, store.Tasks.Count, store.ChangeRequests.Count, store.ReviewItems.Count);
        LogFirstRenderedFrame();

        // ADR 0015: after the first view is usable, so the backup never delays startup. The online backup
        // reads one WAL snapshot and runs alongside commits.
        _backupTask = RunDailyBackupAsync(_session.Backups);
    }

    // T-11 startup budget ("usable initial view"): the first frame rendered after the store became ready. The
    // low-priority enqueue lets the page bindings that the ready state scheduled run first. scripts/run-performance.ps1
    // reads this line (event 5016).
    private void LogFirstRenderedFrame()
    {
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            void OnRendered(object? sender, Microsoft.UI.Xaml.Media.RenderedEventArgs e)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendered -= OnRendered;
                Log.FirstViewRendered(_logger, _context.ElapsedMilliseconds);
            }

            Microsoft.UI.Xaml.Media.CompositionTarget.Rendered += OnRendered;
        });
    }

    private async Task RunDailyBackupAsync(DailyBackupService backups)
    {
        try
        {
            var result = await backups.RunAsync(_lifetime.Token);
            if (result.Outcome == DailyBackupOutcome.Failed)
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

        [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "Startup ready: usable view at {ElapsedMs} ms after launch ({Tasks} tasks, {ChangeRequests} CRs, {Reviews} reviews).")]
        public static partial void Ready(ILogger logger, long elapsedMs, int tasks, int changeRequests, int reviews);

        [LoggerMessage(EventId = 5004, Level = LogLevel.Warning, Message = "Startup recovery: {Kind:l} (storage failure: {StorageFailure:l}) at {ElapsedMs} ms after launch; the dataset was not opened.")]
        public static partial void Recovery(ILogger logger, string kind, string storageFailure, long elapsedMs);

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

        [LoggerMessage(EventId = 5016, Level = LogLevel.Information, Message = "Startup rendered: first frame with the ready view at {ElapsedMs} ms after launch.")]
        public static partial void FirstViewRendered(ILogger logger, long elapsedMs);

        [LoggerMessage(EventId = 5010, Level = LogLevel.Information, Message = "Navigated to {Page:l}; live store subscriptions {Subscriptions}; working set {WorkingSetMiB} MiB.")]
        public static partial void Navigated(ILogger logger, string page, int subscriptions, long workingSetMiB);
    }
}
