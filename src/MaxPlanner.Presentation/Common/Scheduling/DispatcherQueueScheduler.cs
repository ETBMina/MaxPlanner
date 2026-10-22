using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Common.Scheduling;

/// <summary>
/// Rx scheduler that runs work on the UI thread through <see cref="IUiDispatcher"/> (ADR 0010).
/// System.Reactive 7 ships no WinUI 3 scheduler, and going through the injected dispatcher keeps
/// projection pipelines testable without a XAML runtime.
/// </summary>
/// <remarks>
/// Work posted after the dispatcher has shut down (window closing) is dropped: nothing can run on the
/// UI thread then. Delayed work waits on a thread-pool timer and is then posted.
/// </remarks>
public sealed class DispatcherQueueScheduler : LocalScheduler
{
    private readonly IUiDispatcher _dispatcher;

    public DispatcherQueueScheduler(IUiDispatcher dispatcher) =>
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public override IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var work = new SingleAssignmentDisposable();
        var due = Scheduler.Normalize(dueTime);
        if (due == TimeSpan.Zero)
        {
            Post(work, state, action);
            return work;
        }

        var timer = DefaultScheduler.Instance.Schedule(due, () => Post(work, state, action));
        return new CompositeDisposable(work, timer);
    }

    private void Post<TState>(SingleAssignmentDisposable work, TState state, Func<IScheduler, TState, IDisposable> action) =>
        _dispatcher.Post(() =>
        {
            if (!work.IsDisposed)
            {
                work.Disposable = action(this, state);
            }
        });
}
