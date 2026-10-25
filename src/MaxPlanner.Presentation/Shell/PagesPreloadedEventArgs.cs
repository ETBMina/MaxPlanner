namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// The startup page preload finished (T-11 evidence): how many pages it created and how long it took from its start
/// after the first rendered frame. <see cref="Failure"/> is set when it stopped early; the remaining pages are then
/// created on their first visit.
/// </summary>
public sealed class PagesPreloadedEventArgs(int pageCount, TimeSpan elapsed, Exception? failure) : EventArgs
{
    public int PageCount { get; } = pageCount;

    public TimeSpan Elapsed { get; } = elapsed;

    public Exception? Failure { get; } = failure;
}
