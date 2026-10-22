namespace MaxPlanner.Presentation.Common.Store;

/// <summary>
/// Tells the projection store when the user's local day may have changed, so date-relative values
/// (CR overdue flags, review week/month counts) refresh without a data edit or database polling (B-15.9).
/// </summary>
/// <remarks>
/// Desktop implements it from local midnight, resume from sleep, time-zone or clock changes, and window
/// activation. Each emission is the local date at that moment; the same date may repeat and subscribers
/// ignore unchanged dates. Emissions may arrive on any thread.
/// </remarks>
public interface IDayBoundarySource
{
    IObservable<DateOnly> LocalDates { get; }
}
