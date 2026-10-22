using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using Microsoft.UI.Dispatching;

namespace PlatformControls.Projection;

/// <summary>
/// Minimal Rx scheduler over a WinUI <see cref="DispatcherQueue"/>. System.Reactive 7 ships no WinUI 3
/// scheduler (its CoreDispatcher scheduler is UWP-only), so ObserveOn needs this to reach the UI thread.
/// </summary>
internal sealed class DispatcherQueueScheduler(DispatcherQueue dispatcherQueue, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    : LocalScheduler
{
    public DispatcherQueue DispatcherQueue { get; } = dispatcherQueue;

    /// <summary>Work items dropped because the queue was shutting down (window closing).</summary>
    public int DroppedWorkItems { get; private set; }

    public override IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var due = Scheduler.Normalize(dueTime);
        var work = new SingleAssignmentDisposable();

        if (due == TimeSpan.Zero)
        {
            var enqueued = DispatcherQueue.TryEnqueue(priority, () =>
            {
                if (!work.IsDisposed)
                {
                    work.Disposable = action(this, state);
                }
            });

            // TryEnqueue fails only after the queue has begun shutting down; nothing can run on the UI thread then.
            if (!enqueued)
            {
                DroppedWorkItems++;
            }

            return work;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = due;
        timer.IsRepeating = false;
        timer.Tick += (sender, _) =>
        {
            sender.Stop();
            if (!work.IsDisposed)
            {
                work.Disposable = action(this, state);
            }
        };
        timer.Start();
        return new CompositeDisposable(work, Disposable.Create(timer.Stop));
    }
}
