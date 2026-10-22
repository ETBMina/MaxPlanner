using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;

namespace MaxPlanner.Presentation.Common.Store;

/// <summary>
/// The shared, incrementally updated read models of one open dataset (T-07, ADR 0010). Every page
/// projects from these caches; nothing in Presentation queries the database to stay current.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Caches, records, and state are mutated only on <see cref="Scheduler"/>, so cache
/// notifications arrive there; views must <c>ObserveOn</c> the UI scheduler before touching bound
/// objects. The observables and query members can be used from any thread.</para>
/// <para><b>Startup and resync.</b> <see cref="Start"/> subscribes to the change feed into a buffer,
/// loads a snapshot, applies it, drains the buffer (dropping sequences already in the snapshot), and
/// goes live expecting consecutive sequences. A gap, a feed fault, an inconsistent change set, or a
/// bulk change re-runs the same handoff with a new subscription (Resyncing). A failed snapshot load
/// is reported as Faulted; <see cref="Reload"/> retries it.</para>
/// </remarks>
public sealed class WorkspaceProjectionStore : IDisposable
{
    private readonly IChangeFeed _changeFeed;
    private readonly IReadSnapshotSource _snapshots;
    private readonly TimeProvider _timeProvider;
    private readonly IScheduler _scheduler;
    private readonly EventLoopScheduler? _ownedScheduler;
    private readonly WorkspaceRecords _records;
    private readonly SubscriptionCounter _subscriptions = new();
    private readonly SourceCache<TaskReadModel, TaskId> _tasks = new(m => m.Id);
    private readonly SourceCache<SubtaskCardReadModel, SubtaskCardId> _subtaskCards = new(m => m.Id);
    private readonly SourceCache<ChangeRequestReadModel, ChangeRequestId> _changeRequests = new(m => m.Id);
    private readonly SourceCache<ReviewItem, ReviewItemId> _reviewItems = new(r => r.Id);
    private readonly SourceCache<GeneralTodo, GeneralTodoId> _generalTodos = new(t => t.Id);
    private readonly BehaviorSubject<WorkflowSnapshot?> _workflow = new(null);
    private readonly BehaviorSubject<ProjectionState> _state = new(ProjectionState.Loading);
    private readonly Subject<ProjectionCommit> _committed = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<CommittedChangeSet> _buffer = [];
    private readonly IDisposable _dayBoundarySubscription;

    // Taken by every store-scheduler action and by Dispose, so disposal never overlaps a cache edit.
    // Reentrant, which an immediate or trampolining scheduler relies on.
    private readonly Lock _gate = new();

    private HandoffPhase _phase = HandoffPhase.NotStarted;
    private IDisposable? _feedSubscription;
    private int _generation;
    private bool _hasLoaded;
    private long _sequence;
    private volatile bool _isDisposed;

    /// <param name="composer">Composes read models; constructed with the localized "General" label (ADR 0020).</param>
    /// <param name="storeScheduler">
    /// The single scheduler that owns all mutation. It must run actions one at a time in submission order.
    /// Null creates (and disposes with the store) a dedicated background event loop, as production does.
    /// </param>
    public WorkspaceProjectionStore(
        IChangeFeed changeFeed,
        IReadSnapshotSource snapshots,
        ReadModelComposer composer,
        TimeProvider timeProvider,
        IDayBoundarySource dayBoundaries,
        IScheduler? storeScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(dayBoundaries);
        _changeFeed = changeFeed ?? throw new ArgumentNullException(nameof(changeFeed));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _records = new WorkspaceRecords(composer);
        if (storeScheduler is null)
        {
            _ownedScheduler = new EventLoopScheduler(start => new Thread(start) { IsBackground = true, Name = "Projection store" });
        }

        _scheduler = storeScheduler ?? _ownedScheduler!;
        Tasks = new ReadOnlyProjectionCache<TaskReadModel, TaskId>(_tasks, _subscriptions);
        SubtaskCards = new ReadOnlyProjectionCache<SubtaskCardReadModel, SubtaskCardId>(_subtaskCards, _subscriptions);
        ChangeRequests = new ReadOnlyProjectionCache<ChangeRequestReadModel, ChangeRequestId>(_changeRequests, _subscriptions);
        ReviewItems = new ReadOnlyProjectionCache<ReviewItem, ReviewItemId>(_reviewItems, _subscriptions);
        GeneralTodos = new ReadOnlyProjectionCache<GeneralTodo, GeneralTodoId>(_generalTodos, _subscriptions);
        _dayBoundarySubscription = dayBoundaries.LocalDates.Subscribe(date => Run(null, () => OnLocalDate(date)));
    }

    private enum HandoffPhase
    {
        NotStarted,
        Buffering,
        Live,
        Stopped,
    }

    /// <summary>Every task, archived included (B-05), as displayed.</summary>
    public IObservableCache<TaskReadModel, TaskId> Tasks { get; }

    public IObservableCache<SubtaskCardReadModel, SubtaskCardId> SubtaskCards { get; }

    public IObservableCache<ChangeRequestReadModel, ChangeRequestId> ChangeRequests { get; }

    public IObservableCache<ReviewItem, ReviewItemId> ReviewItems { get; }

    public IObservableCache<GeneralTodo, GeneralTodoId> GeneralTodos { get; }

    /// <summary>The scheduler that mutates the caches; view pipelines filter on it (ADR 0010).</summary>
    public IScheduler Scheduler => _scheduler;

    /// <summary>The current workflow, replayed to new subscribers once a snapshot has loaded.</summary>
    public IObservable<WorkflowSnapshot> Workflow => _workflow.Where(w => w is not null).Select(w => w!);

    public WorkflowSnapshot? CurrentWorkflow => _workflow.Value;

    /// <summary>The lifecycle state, replayed to new subscribers. Raised on the store scheduler.</summary>
    public IObservable<ProjectionState> State => _state.AsObservable();

    public ProjectionState CurrentState => _state.Value;

    public bool IsEditingBlocked => CurrentState.IsEditingBlocked;

    /// <summary>One notification per applied batch, raised on the store scheduler after all caches were updated.</summary>
    public IObservable<ProjectionCommit> Committed => _committed.AsObservable();

    /// <summary>The commit sequence the caches reflect; 0 before the first snapshot.</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>Live subscriptions to the store's caches, for disposal and navigation-budget tests (T-11).</summary>
    public int ActiveCacheSubscriptions => _subscriptions.Count;

    /// <summary>Begins the first load. Calling it again has no effect.</summary>
    public void Start() => Run(null, () =>
    {
        if (_phase == HandoffPhase.NotStarted)
        {
            BeginHandoff(ProjectionStatus.Loading, null);
        }
    });

    /// <summary>Retries after a Faulted load; ignored in any other state.</summary>
    public void Reload() => Run(null, () =>
    {
        if (_state.Value.Status == ProjectionStatus.Faulted)
        {
            BeginHandoff(_hasLoaded ? ProjectionStatus.Resyncing : ProjectionStatus.Loading, null);
        }
    });

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _lifetime.Cancel();
            _feedSubscription?.Dispose();
            _dayBoundarySubscription.Dispose();

            // Completed rather than disposed, so CurrentState and CurrentWorkflow stay readable afterwards.
            _committed.OnCompleted();
            _state.OnCompleted();
            _workflow.OnCompleted();
            _tasks.Dispose();
            _subtaskCards.Dispose();
            _changeRequests.Dispose();
            _reviewItems.Dispose();
            _generalTodos.Dispose();
            _lifetime.Dispose();
        }

        _ownedScheduler?.Dispose();
    }

    /// <summary>
    /// Queues <paramref name="action"/> on the store scheduler. When <paramref name="generation"/> is set,
    /// the action is dropped if a newer handoff has started since, so a stale subscription or snapshot
    /// can never apply data out of order.
    /// </summary>
    private void Run(int? generation, Action action)
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            _scheduler.Schedule(() =>
            {
                lock (_gate)
                {
                    if (!_isDisposed && (generation is null || generation == _generation))
                    {
                        action();
                    }
                }
            });
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
            // The owned event loop was disposed concurrently; the store is closing.
        }
    }

    private void BeginHandoff(ProjectionStatus status, Exception? cause)
    {
        _feedSubscription?.Dispose();
        _buffer.Clear();
        var generation = ++_generation;
        _phase = HandoffPhase.Buffering;
        SetState(new ProjectionState(status, Exception: cause));

        // Subscribe before loading: a change committed after the snapshot's read transaction is then
        // buffered, and one committed before it is in the snapshot (IChangeFeed handoff contract).
        _feedSubscription = _changeFeed.Subscribe(new FeedObserver(this, generation));
        _ = LoadSnapshotAsync(generation);
    }

    private async Task LoadSnapshotAsync(int generation)
    {
        AppResult<ReadSnapshot> result;
        try
        {
            result = await _snapshots.LoadAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_isDisposed)
        {
            return;
        }
        catch (Exception exception)
        {
            Run(generation, () => Stop(null, exception));
            return;
        }

        Run(generation, () => OnSnapshotLoaded(result));
    }

    private void OnSnapshotLoaded(AppResult<ReadSnapshot> result)
    {
        if (!result.IsSuccess)
        {
            Stop(result.Error, null);
            return;
        }

        var snapshot = result.Value;
        try
        {
            var delta = _records.Load(snapshot, _timeProvider.LocalToday());
            Commit(delta, snapshot.CommitSequence, ProjectionCommitReason.Snapshot);
        }
        catch (Exception exception)
        {
            // A snapshot that cannot be projected will not improve by reloading it; report instead of looping.
            Stop(null, exception);
            return;
        }

        _hasLoaded = true;
        _phase = HandoffPhase.Live;
        var generation = _generation;
        var buffered = _buffer.ToArray();
        _buffer.Clear();
        foreach (var changeSet in buffered)
        {
            ApplyLive(changeSet);
            if (generation != _generation)
            {
                return;
            }
        }

        SetState(new ProjectionState(ProjectionStatus.Ready));
    }

    private void OnChangeSet(CommittedChangeSet changeSet)
    {
        switch (_phase)
        {
            case HandoffPhase.Buffering:
                _buffer.Add(changeSet);
                break;
            case HandoffPhase.Live:
                ApplyLive(changeSet);
                break;
            default:
                break;
        }
    }

    private void ApplyLive(CommittedChangeSet changeSet)
    {
        var current = Sequence;
        if (changeSet.Sequence <= current)
        {
            // Already reflected by the snapshot.
            return;
        }

        if (changeSet.Sequence != current + 1 || changeSet.RequiresFullReload)
        {
            BeginHandoff(ProjectionStatus.Resyncing, null);
            return;
        }

        try
        {
            Commit(_records.Apply(changeSet), changeSet.Sequence, ProjectionCommitReason.ChangeSet);
        }
        catch (Exception exception)
        {
            // The records may be partly updated; a fresh snapshot replaces all of them.
            BeginHandoff(ProjectionStatus.Resyncing, exception);
        }
    }

    private void OnLocalDate(DateOnly today)
    {
        if (_phase != HandoffPhase.Live || today == _records.Today)
        {
            // Before going live the snapshot load reads today itself.
            return;
        }

        try
        {
            Commit(_records.RecomposeForDay(today), Sequence, ProjectionCommitReason.DayChanged);
        }
        catch (Exception exception)
        {
            BeginHandoff(ProjectionStatus.Resyncing, exception);
        }
    }

    private void OnFeedFault(Exception exception)
    {
        if (_phase is HandoffPhase.Buffering or HandoffPhase.Live)
        {
            BeginHandoff(_hasLoaded ? ProjectionStatus.Resyncing : ProjectionStatus.Loading, exception);
        }
    }

    private void OnFeedCompleted()
    {
        // The dataset is closing; no further change set can arrive on this subscription.
        _feedSubscription?.Dispose();
        _feedSubscription = null;
    }

    private void Stop(AppError? error, Exception? exception)
    {
        _feedSubscription?.Dispose();
        _feedSubscription = null;
        _buffer.Clear();
        _phase = HandoffPhase.Stopped;
        SetState(new ProjectionState(ProjectionStatus.Faulted, error, exception));
    }

    private void SetState(ProjectionState state)
    {
        if (state != _state.Value)
        {
            _state.OnNext(state);
        }
    }

    /// <summary>Writes one batch with a single edit per cache, then announces it once (ADR 0010).</summary>
    private void Commit(ProjectionDelta delta, long sequence, ProjectionCommitReason reason)
    {
        if (delta.Workflow is not null)
        {
            _workflow.OnNext(delta.Workflow);
        }

        Write(_changeRequests, delta.ChangeRequests, delta.RemovedChangeRequests);
        Write(_tasks, delta.Tasks, delta.RemovedTasks);
        Write(_subtaskCards, delta.SubtaskCards, delta.RemovedSubtaskCards);
        Write(_reviewItems, delta.ReviewItems, delta.RemovedReviewItems);
        Write(_generalTodos, delta.GeneralTodos, delta.RemovedGeneralTodos);
        Interlocked.Exchange(ref _sequence, sequence);
        _committed.OnNext(new ProjectionCommit(sequence, reason, _records.Today));
    }

    private static void Write<TObject, TKey>(SourceCache<TObject, TKey> cache, IReadOnlyList<TObject> upserts, IReadOnlyCollection<TKey> removals)
        where TObject : notnull
        where TKey : notnull
    {
        if (upserts.Count == 0 && removals.Count == 0)
        {
            return;
        }

        cache.Edit(updater =>
        {
            updater.RemoveKeys(removals);
            updater.AddOrUpdate(upserts);
        });
    }

    /// <summary>
    /// Receives change sets on the database writer thread and only hands them to the store scheduler,
    /// so the writer is never blocked by projection work (IChangeFeed contract).
    /// </summary>
    private sealed class FeedObserver(WorkspaceProjectionStore store, int generation) : IObserver<CommittedChangeSet>
    {
        public void OnNext(CommittedChangeSet value) => store.Run(generation, () => store.OnChangeSet(value));

        public void OnError(Exception error) => store.Run(generation, () => store.OnFeedFault(error));

        public void OnCompleted() => store.Run(generation, store.OnFeedCompleted);
    }
}
