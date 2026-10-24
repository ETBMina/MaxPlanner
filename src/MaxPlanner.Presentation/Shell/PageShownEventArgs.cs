namespace MaxPlanner.Presentation.Shell;

/// <summary>A page switch and the time from the navigation request to its first rendered frame (T-11 evidence).</summary>
public sealed class PageShownEventArgs(AppPage page, TimeSpan elapsed) : EventArgs
{
    public AppPage Page { get; } = page;

    public TimeSpan Elapsed { get; } = elapsed;
}
