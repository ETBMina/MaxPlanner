using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Common;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;

namespace MaxPlanner.Presentation.Tests.Testing;

/// <summary>A scheduler that queues work until the test runs it, so every interleaving is explicit.</summary>
internal sealed class ManualScheduler : LocalScheduler
{
    private readonly Queue<Action> _queue = new();
    private readonly Lock _gate = new();

    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public override IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        var work = new SingleAssignmentDisposable();
        lock (_gate)
        {
            _queue.Enqueue(() =>
            {
                if (!work.IsDisposed)
                {
                    work.Disposable = action(this, state);
                }
            });
        }

        return work;
    }

    /// <summary>Runs queued work, including work queued while running, until the queue is empty.</summary>
    public void RunAll()
    {
        for (var executed = 0; executed < 1_000_000; executed++)
        {
            Action next;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out next!))
                {
                    return;
                }
            }

            next();
        }

        throw new InvalidOperationException("The scheduler did not become idle.");
    }
}

/// <summary>Runs UI work inline, as if every post came from the UI thread.</summary>
internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;

    public bool Post(Action action)
    {
        action();
        return true;
    }
}

/// <summary>Queues UI work until the test pumps it, like a dispatcher queue that has not run yet.</summary>
internal sealed class ManualUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();

    public bool HasThreadAccess => true;

    public int Pending => _queue.Count;

    public bool Post(Action action)
    {
        _queue.Enqueue(action);
        return true;
    }

    public void RunAll()
    {
        while (_queue.TryDequeue(out var next))
        {
            next();
        }
    }
}

/// <summary>A change feed the test publishes to, counting subscriptions.</summary>
internal sealed class FakeChangeFeed : IChangeFeed
{
    private readonly List<IObserver<CommittedChangeSet>> _observers = [];

    public long LastPublishedSequence { get; private set; }

    public int SubscriberCount => _observers.Count;

    public int SubscribeCount { get; private set; }

    public IDisposable Subscribe(IObserver<CommittedChangeSet> observer)
    {
        SubscribeCount++;
        _observers.Add(observer);
        return Disposable.Create(() => _observers.Remove(observer));
    }

    public void Publish(CommittedChangeSet changeSet)
    {
        LastPublishedSequence = changeSet.Sequence;
        foreach (var observer in _observers.ToArray())
        {
            observer.OnNext(changeSet);
        }
    }

    /// <summary>The IChangeFeed contract for a throwing subscriber: OnError, then unsubscribed.</summary>
    public void Fault(Exception error)
    {
        foreach (var observer in _observers.ToArray())
        {
            _observers.Remove(observer);
            observer.OnError(error);
        }
    }
}

/// <summary>A snapshot source whose loads stay pending until the test completes them.</summary>
internal sealed class FakeSnapshotSource : IReadSnapshotSource
{
    private readonly Queue<TaskCompletionSource<AppResult<ReadSnapshot>>> _pending = new();

    public int LoadCount { get; private set; }

    public bool HasPendingLoad => _pending.Count > 0;

    public Task<AppResult<ReadSnapshot>> LoadAsync(CancellationToken cancellationToken)
    {
        LoadCount++;
        var load = new TaskCompletionSource<AppResult<ReadSnapshot>>();
        _pending.Enqueue(load);
        return load.Task;
    }

    public void Complete(ReadSnapshot snapshot) => _pending.Dequeue().SetResult(AppResult.Success(snapshot));

    public void Fail(AppError error) => _pending.Dequeue().SetResult(AppResult.Failure<ReadSnapshot>(error));
}

internal sealed class FakeDayBoundarySource : IDayBoundarySource, IDisposable
{
    private readonly Subject<DateOnly> _dates = new();

    public IObservable<DateOnly> LocalDates => _dates;

    public void Raise(DateOnly localDate) => _dates.OnNext(localDate);

    public void Dispose() => _dates.Dispose();
}

/// <summary>A clock whose UTC instant the test controls; local time is UTC.</summary>
internal sealed class TestClock(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
