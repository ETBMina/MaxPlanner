using Microsoft.UI.Dispatching;

namespace MaxPlanner.Presentation.Common.Services;

/// <summary>The production <see cref="IUiDispatcher"/> over the window thread's WinUI <see cref="DispatcherQueue"/>.</summary>
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueUiDispatcher(DispatcherQueue queue) => _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _queue.TryEnqueue(action.Invoke);
    }
}
