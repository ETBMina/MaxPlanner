using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MaxPlanner.Presentation.Common.Scheduling;

/// <summary>Rx helpers for work that only needs the newest value.</summary>
public static class CoalescingObservable
{
    /// <summary>
    /// Delivers the latest value on <paramref name="scheduler"/>, dropping values that a newer one replaced before the
    /// scheduler got to them. A burst of N notifications (an import, a resync, several quick commits) therefore costs
    /// one downstream run instead of N, and no value waits for a timer, so a single change has no added latency.
    /// </summary>
    /// <remarks>
    /// The scheduler must run actions one at a time in submission order (the projection store's event loop does). At
    /// most one action is queued at a time; a value that arrives while it is queued replaces the pending one.
    /// </remarks>
    public static IObservable<T> CoalesceLatest<T>(this IObservable<T> source, IScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scheduler);
        return Observable.Create<T>(observer =>
        {
            var gate = new object();
            var hasPending = false;
            var pending = default(T)!;
            var disposed = new BooleanDisposable();
            var subscription = source.Subscribe(
                value =>
                {
                    lock (gate)
                    {
                        pending = value;
                        if (hasPending)
                        {
                            return;
                        }

                        hasPending = true;
                    }

                    scheduler.Schedule(() =>
                    {
                        T latest;
                        lock (gate)
                        {
                            latest = pending;
                            pending = default!;
                            hasPending = false;
                        }

                        if (!disposed.IsDisposed)
                        {
                            observer.OnNext(latest);
                        }
                    });
                },
                error => scheduler.Schedule(() =>
                {
                    if (!disposed.IsDisposed)
                    {
                        observer.OnError(error);
                    }
                }),
                () => scheduler.Schedule(() =>
                {
                    if (!disposed.IsDisposed)
                    {
                        observer.OnCompleted();
                    }
                }));
            return new CompositeDisposable(subscription, disposed);
        });
    }
}
