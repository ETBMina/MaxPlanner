using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Policies;

/// <summary>
/// B-15 overdue rule for CRs, the only overdue concept in release 1. Shared by the Dashboard,
/// Calendar indicators, Records mode, and drill-down so their counts always agree.
/// </summary>
public static class OverduePolicy
{
    /// <remarks>
    /// The planned range is inclusive: a CR whose PlannedEnd is today is still on time and becomes
    /// overdue on the next local day. Task dates and task completion are never inputs.
    /// </remarks>
    public static bool IsOverdue(PlannedRange? planned, bool statusIsCompleted, DateOnly today) =>
        !statusIsCompleted && planned is not null && planned.End < today;

    /// <summary>Calendar days past PlannedEnd for an overdue CR; 0 otherwise.</summary>
    public static int DaysOverdue(PlannedRange? planned, bool statusIsCompleted, DateOnly today) =>
        IsOverdue(planned, statusIsCompleted, today) ? today.DayNumber - planned!.End.DayNumber : 0;

    /// <summary>The same rule over a read model's PlannedEnd, so read-side filters and metrics never restate it.</summary>
    public static bool IsOverdueByPlannedEnd(DateOnly? plannedEnd, bool statusIsCompleted, DateOnly today) =>
        !statusIsCompleted && plannedEnd is { } end && end < today;

    public static int DaysOverdueByPlannedEnd(DateOnly? plannedEnd, bool statusIsCompleted, DateOnly today) =>
        IsOverdueByPlannedEnd(plannedEnd, statusIsCompleted, today) ? today.DayNumber - plannedEnd!.Value.DayNumber : 0;
}
