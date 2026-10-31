using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Application.Filtering;

/// <summary>
/// The Dashboard metric a drill-down carries into the task scope (B-10, B-15.6, design §11 Q7). It is shown as the
/// non-editable "From Dashboard" chip, because some metrics (unscheduled, unlinked, active Subtask cards) have no
/// equivalent in the editable fields.
/// </summary>
public enum DashboardTaskMetric
{
    /// <summary>Non-archived tasks (a chart category adds its status or squad as an ordinary field).</summary>
    AllTasks,

    /// <summary>Non-archived tasks not in a completed status.</summary>
    ActiveTasks,

    CompletedTasks,

    /// <summary>Active tasks with no CR, or whose CR has no planned range (B-15).</summary>
    UnscheduledTasks,

    /// <summary>Active tasks with no CR link (B-15.5).</summary>
    UnlinkedTasks,

    /// <summary>Subtask cards not completed whose parent is not archived (B-15).</summary>
    ActiveSubtaskCards,
}

/// <summary>
/// The shared Board/Grid task scope (B-10): text search plus separate fields combined with AND, values within one
/// field combined with OR. Lookups match by stable ID. Archive visibility is not part of it; each page composes it.
/// </summary>
public sealed record TaskFilterSpec
{
    public static TaskFilterSpec Empty { get; } = new();

    public string SearchText { get; init; } = string.Empty;

    public IReadOnlyList<TaskStatusId> Statuses { get; init; } = [];

    public IReadOnlyList<PriorityId> Priorities { get; init; } = [];

    /// <summary>Inherited squads; a null entry is "General" (no CR, or a CR without a squad).</summary>
    public IReadOnlyList<SquadId?> Squads { get; init; } = [];

    /// <summary>Inherited CR Owners (unlinked records have none and fail this field).</summary>
    public IReadOnlyList<PersonId> Owners { get; init; } = [];

    public IReadOnlyList<PersonId> Designers { get; init; } = [];

    public IReadOnlyList<PersonId> Developers { get; init; } = [];

    public IReadOnlyList<TagId> Tags { get; init; } = [];

    public TagMatchMode TagMode { get; init; } = TagMatchMode.Any;

    public CrLinkFilter Cr { get; init; } = CrLinkFilter.Any;

    public string? ServiceContains { get; init; }

    /// <summary>Actual start date (B-10: actual dates, never CR planned dates).</summary>
    public DateRangeFilter? StartDate { get; init; }

    public DateRangeFilter? EndDate { get; init; }

    public IReadOnlyList<CustomFieldFilter> CustomFields { get; init; } = [];

    /// <summary>Set only by a Dashboard drill-down (never persisted).</summary>
    public DashboardTaskMetric? Dashboard { get; init; }

    /// <summary>The number of editable fields set, for the Filters InfoBadge and the flyout header (design §2.6).</summary>
    public int FieldCount =>
        Count(Statuses.Count > 0) + Count(Priorities.Count > 0) + Count(Squads.Count > 0) + Count(Owners.Count > 0)
        + Count(Designers.Count > 0) + Count(Developers.Count > 0) + Count(Tags.Count > 0) + Count(Cr.IsSet)
        + Count(!string.IsNullOrWhiteSpace(ServiceContains)) + Count(StartDate is { IsSet: true }) + Count(EndDate is { IsSet: true })
        + CustomFields.Count(f => f.IsSet);

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>True when any search, field, or Dashboard context narrows the records.</summary>
    public bool IsActive => HasSearch || FieldCount > 0 || Dashboard is not null;

    /// <summary>The same fields without the search text (a drill-down replaces fields but the page owns its box).</summary>
    public TaskFilterSpec WithoutSearch() => this with { SearchText = string.Empty };

    private static int Count(bool isSet) => isSet ? 1 : 0;
}

/// <summary>Lookups the task predicate needs beyond the record itself, resolved from the current read models.</summary>
/// <param name="FindTask">A Subtask card's parent; its priority, people, tags, service and custom values apply to the card.</param>
/// <param name="FindChangeRequest">The linked CR, for the Unscheduled metric (CR planned range).</param>
public sealed record TaskFilterContext(
    Func<TaskId, TaskReadModel?> FindTask,
    Func<ChangeRequestId, ChangeRequestReadModel?> FindChangeRequest);

/// <summary>
/// The one task predicate (B-10, B-15.6): Board, Grid, the Dashboard metrics, and their drill-downs all use it, so a
/// count and its drill-down always agree.
/// </summary>
public static class TaskFilterMatcher
{
    public static bool Matches(TaskFilterSpec spec, TaskReadModel task, TaskFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(context);
        if (spec.Dashboard is { } metric && !MatchesMetric(metric, task, context))
        {
            return false;
        }

        return MatchesFields(spec, task.Status.Id, task, task.Cr, task.StartDate, task.EndDate)
            && (!spec.HasSearch || TaskSearch.Matches(task, spec.SearchText));
    }

    /// <summary>
    /// B-16.8: a Subtask card filters by its own status and dates, by the CR values inherited through its parent,
    /// and by its parent's priority, people, tags, service and custom values.
    /// </summary>
    public static bool Matches(TaskFilterSpec spec, SubtaskCardReadModel card, TaskFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(context);
        if (spec.Dashboard is { } metric
            && (metric != DashboardTaskMetric.ActiveSubtaskCards || card.IsCompleted || card.IsParentArchived))
        {
            return false;
        }

        var parent = context.FindTask(card.ParentTaskId);
        return MatchesFields(spec, card.Status.Id, parent, card.Cr, card.StartDate, card.EndDate)
            && (!spec.HasSearch || TaskSearch.Matches(card, parent, spec.SearchText));
    }

    /// <summary>The Dashboard metric predicates of B-15; task metrics never count Subtask cards.</summary>
    public static bool MatchesMetric(DashboardTaskMetric metric, TaskReadModel task, TaskFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(context);
        if (task.IsArchived)
        {
            return false;
        }

        return metric switch
        {
            DashboardTaskMetric.AllTasks => true,
            DashboardTaskMetric.ActiveTasks => !task.IsCompleted,
            DashboardTaskMetric.CompletedTasks => task.IsCompleted,
            DashboardTaskMetric.UnlinkedTasks => !task.IsCompleted && task.Cr.ChangeRequestId is null,
            DashboardTaskMetric.UnscheduledTasks => !task.IsCompleted && IsUnscheduled(task, context),
            _ => false,
        };
    }

    /// <summary>B-15: no linked CR, or a linked CR with no planned range. It is not a deadline rule.</summary>
    public static bool IsUnscheduled(TaskReadModel task, TaskFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(context);
        return task.Cr.ChangeRequestId is not { } crId || context.FindChangeRequest(crId) is not { PlannedStart: not null };
    }

    private static bool MatchesFields(
        TaskFilterSpec spec,
        TaskStatusId status,
        TaskReadModel? owner,
        InheritedCrValues cr,
        DateOnly? start,
        DateOnly? end)
    {
        if (spec.Statuses.Count > 0 && !spec.Statuses.Contains(status))
        {
            return false;
        }

        if (spec.Priorities.Count > 0 && (owner is null || !spec.Priorities.Contains(owner.Priority.Id)))
        {
            return false;
        }

        if (spec.Squads.Count > 0 && !spec.Squads.Contains(cr.SquadId))
        {
            return false;
        }

        if (spec.Owners.Count > 0 && (cr.OwnerId is not { } ownerId || !spec.Owners.Contains(ownerId)))
        {
            return false;
        }

        if (!spec.Cr.Matches(cr.ChangeRequestId))
        {
            return false;
        }

        // ADR 0024: several selected people match a task whose single Designer (or Developer) is any of them.
        if (spec.Designers.Count > 0 && (owner?.Designer is not { } designer || !spec.Designers.Contains(designer.Id)))
        {
            return false;
        }

        if (spec.Developers.Count > 0 && (owner?.Developer is not { } developer || !spec.Developers.Contains(developer.Id)))
        {
            return false;
        }

        if (spec.Tags.Count > 0 && !MatchesTags(spec, owner))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(spec.ServiceContains)
            && owner?.Service?.Contains(spec.ServiceContains.Trim(), StringComparison.CurrentCultureIgnoreCase) != true)
        {
            return false;
        }

        if (spec.StartDate is { } startFilter && !startFilter.Matches(start))
        {
            return false;
        }

        if (spec.EndDate is { } endFilter && !endFilter.Matches(end))
        {
            return false;
        }

        return spec.CustomFields.All(f => !f.IsSet || f.Matches(owner?.CustomValues));
    }

    private static bool MatchesTags(TaskFilterSpec spec, TaskReadModel? owner)
    {
        if (owner is null)
        {
            return false;
        }

        var tags = owner.Tags.Select(t => t.Id).ToHashSet();
        return spec.TagMode == TagMatchMode.All ? spec.Tags.All(tags.Contains) : spec.Tags.Any(tags.Contains);
    }
}

/// <summary>
/// B-10 task text search: a case-insensitive substring across the effective title, CR and task number, inherited
/// squad and owner, service, people, tags, notes, and textual custom values. Subtask cards match their subtitle,
/// notes, and their parent's searchable values (B-16.8).
/// </summary>
public static class TaskSearch
{
    public static bool Matches(TaskReadModel task, string query)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var q = query.Trim();
        return MatchesCr(task.Cr, q) || MatchesOwn(task, q);
    }

    public static bool Matches(SubtaskCardReadModel card, TaskReadModel? parent, string query)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var q = query.Trim();
        return Has(card.Subtitle, q) || Has(card.Notes, q) || MatchesCr(card.Cr, q)
            || (parent is not null ? MatchesOwn(parent, q) : Has(card.ParentTitle, q) || Has(card.ParentService, q));
    }

    private static bool MatchesCr(InheritedCrValues cr, string q) =>
        Has(cr.CrNumber, q) || Has(cr.TaskNumber, q) || Has(cr.SquadName, q) || Has(cr.OwnerName, q);

    private static bool MatchesOwn(TaskReadModel task, string q) =>
        Has(task.EffectiveTitle, q) || Has(task.Service, q) || Has(task.Notes, q)
        || Has(task.Designer?.Name, q) || Has(task.Developer?.Name, q) || task.Tags.Any(t => Has(t.Name, q))
        || task.CustomValues.Values.Any(v => v switch
        {
            Domain.Fields.TextCustomValue t => Has(t.Value, q),
            Domain.Fields.MultilineTextCustomValue m => Has(m.Value, q),
            _ => false,
        });

    private static bool Has(string? value, string q) => value?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true;
}
