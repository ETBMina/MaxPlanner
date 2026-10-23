using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Reviews;

namespace MaxPlanner.Application.Filtering;

/// <summary>
/// The Calendar CR scope (B-10, B-08.12): one filter for bars, backlog, overflow, and Records mode. Planned range
/// filtering uses interval overlap with explicit inclusion of unscheduled CRs.
/// </summary>
public sealed record CrFilterSpec
{
    public static CrFilterSpec Empty { get; } = new();

    public string SearchText { get; init; } = string.Empty;

    public IReadOnlyList<CrStatusId> Statuses { get; init; } = [];

    public IReadOnlyList<PriorityId> Priorities { get; init; } = [];

    /// <summary>A null entry is "General" (a CR without a squad).</summary>
    public IReadOnlyList<SquadId?> Squads { get; init; } = [];

    public IReadOnlyList<PersonId> Owners { get; init; } = [];

    /// <summary>Overlap with the planned range; <see cref="DateRangeFilter.IncludeUndated"/> keeps unscheduled CRs.</summary>
    public DateRangeFilter? Planned { get; init; }

    public IReadOnlyList<CustomFieldFilter> CustomFields { get; init; } = [];

    /// <summary>Set only by the Overdue CRs drill-down (B-15.4); never persisted.</summary>
    public bool DashboardOverdue { get; init; }

    public int FieldCount =>
        (Statuses.Count > 0 ? 1 : 0) + (Priorities.Count > 0 ? 1 : 0) + (Squads.Count > 0 ? 1 : 0) + (Owners.Count > 0 ? 1 : 0)
        + (Planned is { IsSet: true } ? 1 : 0) + CustomFields.Count(f => f.IsSet);

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsActive => HasSearch || FieldCount > 0 || DashboardOverdue;
}

/// <summary>The one CR predicate shared by Calendar, Records, the Dashboard, and the Overdue drill-down (B-15).</summary>
public static class CrFilterMatcher
{
    /// <param name="today">The local day of the overdue rule (the store's day, B-15).</param>
    public static bool Matches(CrFilterSpec spec, ChangeRequestReadModel cr, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(cr);
        if (spec.DashboardOverdue && !OverduePolicy.IsOverdueByPlannedEnd(cr.PlannedEnd, cr.IsCompleted, today))
        {
            return false;
        }

        return (spec.Statuses.Count == 0 || spec.Statuses.Contains(cr.Status.Id))
            && (spec.Priorities.Count == 0 || spec.Priorities.Contains(cr.Priority.Id))
            && (spec.Squads.Count == 0 || spec.Squads.Contains(cr.SquadId))
            && (spec.Owners.Count == 0 || spec.Owners.Contains(cr.Owner.Id))
            && (spec.Planned is not { } planned || planned.MatchesOverlap(cr.PlannedStart, cr.PlannedEnd))
            && spec.CustomFields.All(f => !f.IsSet || f.Matches(cr.CustomValues))
            && (!spec.HasSearch || Search(cr, spec.SearchText.Trim()));
    }

    /// <summary>
    /// B-10 CR search: a case-insensitive substring across Number, Title, TaskNumber, Squad, Owner, EmailSubject,
    /// and Description.
    /// </summary>
    public static bool Search(ChangeRequestReadModel cr, string query)
    {
        ArgumentNullException.ThrowIfNull(cr);
        return string.IsNullOrWhiteSpace(query)
            || Has(cr.Number, query) || Has(cr.Title, query) || Has(cr.TaskNumber, query) || Has(cr.SquadName, query)
            || Has(cr.Owner.Name, query) || Has(cr.EmailSubject, query) || Has(cr.Description, query);
    }

    private static bool Has(string? value, string q) => value?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true;
}

/// <summary>The review metrics of B-15 that a Dashboard drill-down opens in Checklists › Reviews.</summary>
public enum ReviewMetric
{
    Open,

    /// <summary>CompletedOn within the local week that starts on Sunday.</summary>
    DoneThisWeek,

    /// <summary>CompletedOn within the local calendar month.</summary>
    DoneThisMonth,
}

/// <summary>
/// A review drill-down (B-15.3): the metric and optionally one developer bucket. Reviews are never filtered by the
/// Dashboard Squad/Priority/Owner fields.
/// </summary>
/// <param name="ByDeveloper">True for a developer bar; then a null <paramref name="DeveloperId"/> is "Unassigned".</param>
public sealed record ReviewFilterSpec(ReviewMetric Metric, bool ByDeveloper, PersonId? DeveloperId);

public static class ReviewFilterMatcher
{
    public static bool Matches(ReviewFilterSpec spec, ReviewItem review, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(review);
        var inMetric = spec.Metric switch
        {
            ReviewMetric.Open => !review.IsDone,
            ReviewMetric.DoneThisWeek => review.IsDone && review.CompletedOn is { } week && ReviewPeriods.IsInWeek(week, today),
            _ => review.IsDone && review.CompletedOn is { } month && ReviewPeriods.IsInMonth(month, today),
        };
        return inMetric && (!spec.ByDeveloper || review.Content.DeveloperId == spec.DeveloperId);
    }
}

/// <summary>B-15: the review week starts on Sunday; the month is the local calendar month.</summary>
public static class ReviewPeriods
{
    public static bool IsInWeek(DateOnly day, DateOnly today)
    {
        var start = today.AddDays(-(int)today.DayOfWeek);
        return day >= start && day <= start.AddDays(6);
    }

    public static bool IsInMonth(DateOnly day, DateOnly today) => day.Year == today.Year && day.Month == today.Month;
}
