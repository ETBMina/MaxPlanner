using CommunityToolkit.Mvvm.ComponentModel;

namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// Window-level state shown before and around the pages: startup progress, readiness, and a
/// startup failure that keeps the window open with an explanation instead of exiting silently.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarting), nameof(IsReady), nameof(HasFailed))]
    public partial ShellState State { get; private set; } = ShellState.Starting;

    /// <summary>User-facing explanation when <see cref="State"/> is <see cref="ShellState.Failed"/>; otherwise null.</summary>
    [ObservableProperty]
    public partial string? FailureMessage { get; private set; }

    public bool IsStarting => State == ShellState.Starting;

    public bool IsReady => State == ShellState.Ready;

    public bool HasFailed => State == ShellState.Failed;

    public void MarkReady()
    {
        FailureMessage = null;
        State = ShellState.Ready;
    }

    public void MarkFailed(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        FailureMessage = message;
        State = ShellState.Failed;
    }
}

public enum ShellState
{
    Starting,
    Ready,
    Failed,
}
