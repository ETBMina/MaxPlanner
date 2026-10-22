using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Domain.Policies;

/// <summary>
/// B-04 automatic actual dates, shared by every normal create/update/move route for tasks and,
/// independently, for Subtask cards.
/// </summary>
/// <remarks>
/// Import, migration, restore, and lookup-removal remapping must not call this policy: they keep
/// supplied historical dates and never stamp today's date.
/// </remarks>
public static class TaskDatePolicy
{
    /// <param name="current">Dates before the command.</param>
    /// <param name="from">The status before the command, or null when the record is being created.</param>
    /// <param name="to">The status after the command.</param>
    /// <param name="start">What the command does to StartDate.</param>
    /// <param name="end">What the command does to EndDate.</param>
    /// <param name="today">The user's local calendar day.</param>
    public static DomainResult<ActualDates> Apply(
        ActualDates current,
        TaskStatusDefinition? from,
        TaskStatusDefinition to,
        DateIntent start,
        DateIntent end,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(to);

        var startDate = Resolve(current.Start, start);
        var endDate = Resolve(current.End, end);

        // A label/flag edit is not a transition; only creation or a real status change counts.
        var isTransition = from is null || from.Id != to.Id;
        if (isTransition)
        {
            // An explicit clear in this same command wins over automation (B-04 "explicit manual clear").
            if (to.SetsStartDate && startDate is null && start.Kind != DateIntentKind.Cleared)
            {
                startDate = today;
            }

            if (to.IsCompleted && endDate is null && end.Kind != DateIntentKind.Cleared)
            {
                endDate = today;
            }
        }

        // Never silently move the other date: an inverted result is a validation error.
        return ActualDates.Create(startDate, endDate);
    }

    private static DateOnly? Resolve(DateOnly? current, DateIntent intent) =>
        intent.Kind switch
        {
            DateIntentKind.Set => intent.Value,
            DateIntentKind.Cleared => null,
            _ => current,
        };
}
