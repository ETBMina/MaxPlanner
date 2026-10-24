using MaxPlanner.Application.Dashboard;
using MaxPlanner.Presentation.Shell;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>A Dashboard drill-down the workspace performs: the metric's predicate and its visible label.</summary>
/// <param name="Label">"Active tasks", "Tasks in In Progress": the "From Dashboard" chip and InfoBar text.</param>
public sealed record DrillDownRequest(DashboardDrillDown DrillDown, string Label);

/// <summary>
/// The drill-down context stack of design §2.9 (AC-20). Each entry is a destination page whose filter context a
/// Dashboard drill-down replaced; Back (TitleBar, Alt+Left) and Return to Dashboard pop it, restore the destination's
/// previous filters, and show the Dashboard again with its own filters and scroll position, which its retained
/// ViewModel keeps. Use on the UI thread.
/// </summary>
public sealed class DrillDownNavigator
{
    private readonly List<(AppPage Destination, Action EndContext)> _stack = [];

    /// <summary>Raised when <see cref="CanGoBack"/> may have changed (the TitleBar back button).</summary>
    public event EventHandler? StackChanged;

    /// <summary>Asks the shell to show a page.</summary>
    public event EventHandler<AppPage>? NavigationRequested;

    /// <summary>
    /// Raised just before Return or Back shows the Dashboard again, so the view can give focus back to the element the
    /// drill-down started from (design §7.7).
    /// </summary>
    public event EventHandler? Returning;

    /// <summary>Raised by the Dashboard; the workspace applies the replacement context and navigates.</summary>
    public event EventHandler<DrillDownRequest>? DrillDownRequested;

    public bool CanGoBack => _stack.Count > 0;

    /// <summary>Asks the shell to show <paramref name="page"/> without a drill-down context (for example Grid Export… opening Settings › Data).</summary>
    public void RequestNavigation(AppPage page) => NavigationRequested?.Invoke(this, page);

    public void RequestDrillDown(DrillDownRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DrillDownRequested?.Invoke(this, request);
    }

    /// <summary>Records a replaced context. A second drill-down into the same page replaces its older entry.</summary>
    public void Push(AppPage destination, Action endContext)
    {
        ArgumentNullException.ThrowIfNull(endContext);
        _stack.RemoveAll(e => e.Destination == destination);
        _stack.Add((destination, endContext));
        StackChanged?.Invoke(this, EventArgs.Empty);
        NavigationRequested?.Invoke(this, destination);
    }

    /// <summary>The destination's context ended by itself (chip removed, Clear all): drop its entry without navigating.</summary>
    public void Forget(AppPage destination)
    {
        if (_stack.RemoveAll(e => e.Destination == destination) > 0)
        {
            StackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Return to Dashboard from a destination's InfoBar.</summary>
    public void Return(AppPage destination)
    {
        var index = _stack.FindIndex(e => e.Destination == destination);
        if (index < 0)
        {
            Returning?.Invoke(this, EventArgs.Empty);
            NavigationRequested?.Invoke(this, AppPage.Dashboard);
            return;
        }

        var entry = _stack[index];
        _stack.RemoveAt(index);
        entry.EndContext();
        StackChanged?.Invoke(this, EventArgs.Empty);
        Returning?.Invoke(this, EventArgs.Empty);
        NavigationRequested?.Invoke(this, AppPage.Dashboard);
    }

    /// <summary>TitleBar Back / Alt+Left: pops the newest entry. Returns false when there is nothing to go back to.</summary>
    public bool GoBack()
    {
        if (_stack.Count == 0)
        {
            return false;
        }

        Return(_stack[^1].Destination);
        return true;
    }
}
