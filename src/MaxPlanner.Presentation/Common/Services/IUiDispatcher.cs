namespace MaxPlanner.Presentation.Common.Services;

/// <summary>
/// Marshals work onto the UI thread that owns the window.
/// </summary>
/// <remarks>
/// T-07: projection updates are delivered through this abstraction rather than an
/// assumed <see cref="SynchronizationContext.Current"/>, so ViewModels stay testable
/// with an immediate dispatcher.
/// </remarks>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    /// <summary>Queues <paramref name="action"/> on the UI thread; returns false if the queue is shut down.</summary>
    bool Post(Action action);
}
