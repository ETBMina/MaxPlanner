using System.Collections.Immutable;
using MaxPlanner.Application.Changes;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Infrastructure.Persistence;

/// <summary>Delivers committed change sets to subscribers with the semantics documented on <c>IChangeFeed</c>.</summary>
/// <remarks>
/// <see cref="Publish"/> and <see cref="Complete"/> are called only by the writer worker, so deliveries never
/// overlap. Subscribing and unsubscribing may happen on any thread; each delivery iterates an immutable
/// snapshot of the subscriber list taken under the lock.
/// </remarks>
internal sealed class ChangeFeedPublisher(long initialSequence, ILogger logger)
{
    private readonly Lock _gate = new();
    private ImmutableArray<IObserver<CommittedChangeSet>> _observers = [];
    private long _lastPublished = initialSequence;
    private bool _isCompleted;

    public long LastPublishedSequence => Interlocked.Read(ref _lastPublished);

    public IDisposable Subscribe(IObserver<CommittedChangeSet> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            if (!_isCompleted)
            {
                _observers = _observers.Add(observer);
                return new Subscription(this, observer);
            }
        }

        observer.OnCompleted();
        return new Subscription(this, observer);
    }

    public void Publish(CommittedChangeSet changeSet)
    {
        Interlocked.Exchange(ref _lastPublished, changeSet.Sequence);
        ImmutableArray<IObserver<CommittedChangeSet>> observers;
        lock (_gate)
        {
            observers = _observers;
        }

        foreach (var observer in observers)
        {
            try
            {
                observer.OnNext(changeSet);
            }
            catch (Exception ex)
            {
                // T-07: a subscriber failure never undoes or retries the commit. The subscriber's own
                // state is now unknown, so it is ended with OnError and must resync from a snapshot.
                PersistenceLog.SubscriberFaulted(logger, changeSet.Sequence, ex);
                Remove(observer);
                TryNotify(() => observer.OnError(ex));
            }
        }
    }

    public void Complete()
    {
        ImmutableArray<IObserver<CommittedChangeSet>> observers;
        lock (_gate)
        {
            _isCompleted = true;
            observers = _observers;
            _observers = [];
        }

        foreach (var observer in observers)
        {
            TryNotify(observer.OnCompleted);
        }
    }

    private void Remove(IObserver<CommittedChangeSet> observer)
    {
        lock (_gate)
        {
            _observers = _observers.Remove(observer);
        }
    }

    private void TryNotify(Action notify)
    {
        try
        {
            notify();
        }
        catch (Exception ex)
        {
            PersistenceLog.SubscriberFaulted(logger, LastPublishedSequence, ex);
        }
    }

    private sealed class Subscription(ChangeFeedPublisher publisher, IObserver<CommittedChangeSet> observer) : IDisposable
    {
        public void Dispose() => publisher.Remove(observer);
    }
}
