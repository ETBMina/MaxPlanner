using System.Collections.Concurrent;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Startup;

namespace MaxPlanner.Application.Tests.Startup;

/// <summary>
/// One ordered record of every port call, so tests can assert the order of file operations and UI steps. Without a UI
/// synchronization context the coordinator's continuations run on pool threads, so access is locked.
/// </summary>
internal sealed class CallLog
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public void Add(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }
}

internal static class TestWaits
{
    /// <summary>How long a test waits for a step before failing instead of hanging.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static Task WithTimeout(this Task task) => task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

    public static Task<T> WithTimeout<T>(this Task<T> task) => task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
}

/// <summary>
/// A single-threaded synchronization context like the UI thread's: posted continuations run one at a time on its own
/// thread. <see cref="RunAsync{T}"/> pumps it until the body's task completes.
/// </summary>
internal sealed class SingleThreadSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    public static Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new SingleThreadSynchronizationContext();
            SetSynchronizationContext(context);
            Task<T> task;
            try
            {
                task = body();
            }
            catch (Exception ex)
            {
                task = Task.FromException<T>(ex);
            }

            task.ContinueWith(_ => context._queue.CompleteAdding(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
            {
                callback(state);
            }

            context._queue.Dispose();
            if (task.IsFaulted)
            {
                result.SetException(task.Exception!.InnerExceptions);
            }
            else if (task.IsCanceled)
            {
                result.SetCanceled();
            }
            else
            {
                result.SetResult(task.Result);
            }
        })
        {
            IsBackground = true,
            Name = "Fake UI thread",
        };
        thread.Start();
        return result.Task;
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("The UI thread is never blocked.");

    public override SynchronizationContext CreateCopy() => this;
}

internal sealed class FakeOpenedDataset(CallLog log, string name = "dataset") : IOpenedDataset
{
    public Guid Id { get; } = Guid.NewGuid();

    public int SchemaVersion => 7;

    public string Name => name;

    public bool IsDisposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        log.Add($"dataset.dispose:{name}");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Answers opens and creates from queues; the restore marker is shared with <see cref="FakeRestoreOperations"/>.</summary>
internal sealed class FakeDatasetStorage(CallLog log) : IDatasetStorage
{
    public Queue<Func<DatasetOpenAttempt>> Opens { get; } = [];

    public Queue<Func<DatasetOpenAttempt>> Creates { get; } = [];

    public bool HasMarker { get; set; }

    public bool HasRestoreMarker()
    {
        log.Add("storage.marker?");
        return HasMarker;
    }

    public Task<DatasetOpenAttempt> OpenExistingAsync()
    {
        log.Add("storage.open");
        return Task.FromResult(Opens.Dequeue()());
    }

    public Task<DatasetOpenAttempt> CreateNewAsync()
    {
        log.Add("storage.create");
        return Task.FromResult(Creates.Dequeue()());
    }

    public DatasetOpenAttempt Opened(string name = "dataset") => new(DatasetOpenOutcome.Opened, new FakeOpenedDataset(log, name), null);

    public static DatasetOpenAttempt Missing() => new(DatasetOpenOutcome.Missing, null, null);

    public static DatasetOpenAttempt Failed(AppError? error, string? safety = null) => new(DatasetOpenOutcome.Failed, null, error, safety);
}

internal sealed class FakeStagedRestore(bool isValid) : IStagedRestore
{
    public bool IsValid => isValid;

    public IReadOnlyList<RestoreCheckResult> Checks { get; } = [new("Manifest", isValid, null)];

    public DateTimeOffset? BackupCreatedAtUtc => null;

    public string? BackupKind => "manual";
}

internal sealed class FakeRestoreOperations(CallLog log, FakeDatasetStorage storage) : IRestoreOperations
{
    public FakeStagedRestore Staged { get; set; } = new(isValid: true);

    public string? RawSafetyCopy { get; set; } = @"C:\data\backups\20261111T090000Z-restore-safety";

    public DateTimeOffset? RawSafetyCopyTime { get; private set; }

    /// <summary>What <see cref="Swap"/> does; the default restores the backup.</summary>
    public Func<bool> SwapBehavior { get; set; } = () => true;

    public InterruptedRestoreOutcome Recovery { get; set; } = InterruptedRestoreOutcome.RolledForward;

    public IStagedRestore? Swapped { get; private set; }

    public IStagedRestore Validate(string backupFolder, CancellationToken cancellationToken)
    {
        log.Add($"restore.validate:{backupFolder}");
        return Staged;
    }

    public void Discard(IStagedRestore staged) => log.Add("restore.discard");

    public string? CreateRawSafetyCopy(DateTimeOffset utcNow)
    {
        log.Add("restore.raw-safety-copy");
        RawSafetyCopyTime = utcNow;
        return RawSafetyCopy;
    }

    public bool Swap(IStagedRestore staged)
    {
        log.Add("restore.swap");
        Swapped = staged;
        return SwapBehavior();
    }

    public InterruptedRestoreOutcome RecoverInterrupted()
    {
        log.Add("restore.recover-interrupted");
        if (Recovery != InterruptedRestoreOutcome.Unresolved)
        {
            storage.HasMarker = false;
        }

        return Recovery;
    }
}

internal sealed class FakeDatasetBackups(CallLog log) : IDatasetBackups
{
    public AppResult<string> CreateResult { get; set; } = AppResult.Success(@"C:\data\backups\20261111T090000Z-restore-safety");

    public (BackupKind Kind, DateTimeOffset UtcNow)? Created { get; private set; }

    /// <summary>What <see cref="RunDailyAsync"/> returns; the default finishes at once with nothing failed.</summary>
    public Func<CancellationToken, Task<DailyBackupReport>> Daily { get; set; } = _ => Task.FromResult(new DailyBackupReport(false));

    public int DailyRuns { get; private set; }

    public string BackupsDirectory => @"C:\data\backups";

    public AppResult<string> Create(BackupKind kind, DateTimeOffset utcNow)
    {
        log.Add($"backups.create:{kind}");
        Created = (kind, utcNow);
        return CreateResult;
    }

    public Task<DailyBackupReport> RunDailyAsync(CancellationToken cancellationToken)
    {
        log.Add("backups.daily");
        DailyRuns++;
        return Daily(cancellationToken);
    }

    public IReadOnlyList<BackupSummary> List() => [];

    public bool DeleteManual(string backupFolder) => false;
}

internal sealed class FakeDatasetSession(CallLog log, FakeOpenedDataset dataset, DatasetSessionHooks hooks, FakeDatasetBackups backups) : IDatasetSession
{
    public FakeOpenedDataset Dataset => dataset;

    public DatasetSessionHooks Hooks => hooks;

    public IDatasetBackups Backups => backups;

    public FakeDatasetBackups FakeBackups => backups;

    public bool IsStarted { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>Thrown by <see cref="Attach"/>, for a failure while the pages are shown.</summary>
    public Exception? AttachFailure { get; init; }

    public void Attach()
    {
        log.Add($"session.attach:{dataset.Name}");
        if (AttachFailure is not null)
        {
            throw AttachFailure;
        }
    }

    public void Start()
    {
        IsStarted = true;
        log.Add($"session.start:{dataset.Name}");
    }

    /// <summary>What the Desktop session does when the first projection load is ready.</summary>
    public void BecomeReady() => hooks.OnFirstReady();

    public async ValueTask DisposeAsync()
    {
        IsDisposed = true;
        log.Add($"session.dispose:{dataset.Name}");
        await dataset.DisposeAsync();
    }
}

internal sealed class FakeSessionFactory(CallLog log) : IDatasetSessionFactory
{
    public List<FakeDatasetSession> Sessions { get; } = [];

    public FakeDatasetSession Current => Sessions[^1];

    /// <summary>The backups the next session gets; tests configure them before the session exists.</summary>
    public FakeDatasetBackups NextBackups { get; set; } = new(log);

    /// <summary>Thrown by <see cref="Create"/>, for a failure while the session's services are built.</summary>
    public Exception? CreateFailure { get; set; }

    /// <summary>Thrown by the next session's <see cref="IDatasetSession.Attach"/>.</summary>
    public Exception? AttachFailure { get; set; }

    public IDatasetSession Create(IOpenedDataset dataset, DatasetSessionHooks hooks)
    {
        var fake = (FakeOpenedDataset)dataset;
        log.Add($"session.create:{fake.Name}");
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        var session = new FakeDatasetSession(log, fake, hooks, NextBackups) { AttachFailure = AttachFailure };
        NextBackups = new FakeDatasetBackups(log);
        Sessions.Add(session);
        return session;
    }
}

internal sealed class FakeStartupShell(CallLog log) : IStartupShell
{
    public bool HasFailed { get; set; }

    public StartupRecovery? Recovery { get; private set; }

    public List<(DataNotice Notice, string? Safety)> Notices { get; } = [];

    public List<StorageFailureKind?> BackupFailures { get; } = [];

    /// <summary>The folder the user picks; null means the user cancelled.</summary>
    public string? PickedFolder { get; set; } = @"D:\Backups\20261101T080000Z-manual";

    /// <summary>Completes when a restore blocks edits, right before it closes the session.</summary>
    public TaskCompletionSource ReplacementBegun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The managed thread of every call, to check that the coordinator stays on the UI thread.</summary>
    public ConcurrentQueue<int> CallThreads { get; } = [];

    public void ShowStarting()
    {
        Record("shell.starting");
        Recovery = null;
        HasFailed = false;
    }

    public void ShowRecovery(StartupRecovery recovery)
    {
        Record($"shell.recovery:{recovery.Kind}");
        Recovery = recovery;
    }

    public void ShowUnexpectedFailure()
    {
        Record("shell.failed");
        HasFailed = true;
    }

    public void BeginDatasetReplacement()
    {
        Record("shell.begin-replacement");
        ReplacementBegun.TrySetResult();
    }

    public void BeginShutdown() => Record("shell.begin-shutdown");

    public void DetachWorkspace() => Record("shell.detach");

    public void ReportDataNotice(DataNotice notice, string? safetyBackupDirectory = null)
    {
        Record($"shell.notice:{notice}");
        Notices.Add((notice, safetyBackupDirectory));
    }

    public void ReportBackupFailed(StorageFailureKind? kind)
    {
        Record("shell.backup-failed");
        BackupFailures.Add(kind);
    }

    public Task<string?> PickBackupFolderAsync()
    {
        Record("shell.pick-folder");
        return Task.FromResult(PickedFolder);
    }

    private void Record(string call)
    {
        CallThreads.Enqueue(Environment.CurrentManagedThreadId);
        log.Add(call);
    }
}
