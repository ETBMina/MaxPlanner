using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Reviews;

namespace MaxPlanner.Application.Dashboard;

/// <summary>Where a Dashboard metric drills down to (B-10, B-15.6, design §2.9).</summary>
public abstract record DashboardDrillDown;

/// <summary>Task and Subtask card metrics open the Grid with exactly this predicate.</summary>
public sealed record TaskDrillDown(TaskFilterSpec Spec) : DashboardDrillDown;

/// <summary>Overdue CRs open Calendar › Records with this CR scope.</summary>
public sealed record CrDrillDown(CrFilterSpec Spec) : DashboardDrillDown;

/// <summary>Review metrics open Checklists › Reviews with this review scope.</summary>
public sealed record ReviewDrillDown(ReviewFilterSpec Spec) : DashboardDrillDown;

/// <summary>The Dashboard Squad choice: a null <paramref name="Id"/> is "General".</summary>
public sealed record DashboardSquad(SquadId? Id);

/// <summary>
/// The Dashboard's own filters (B-15): each null means "all". They apply to task metrics (inherited squad and owner,
/// task priority), to Subtask card metrics (through the parent), and to CR metrics (the CR's own values); never to
/// review metrics.
/// </summary>
public sealed record DashboardFilter(DashboardSquad? Squad, PriorityId? Priority, PersonId? Owner)
{
    public static DashboardFilter None { get; } = new(null, null, null);

    public bool IsActive => Squad is not null || Priority is not null || Owner is not null;
}

/// <summary>A count with the drill-down that shows exactly the records it counts.</summary>
public sealed record DashboardMetric(int Count, DashboardDrillDown DrillDown);

/// <summary>
/// One chart category. <paramref name="Key"/> is the stable lookup ID ("N" format), "General", or "Unassigned", for
/// AutomationIds (design §8.1). <paramref name="DrillDown"/> is null when the Dashboard filter already excludes the
/// category, so the bar can only be zero.
/// </summary>
public sealed record DashboardBar(string Key, string Label, int Count, DashboardDrillDown? DrillDown);

/// <summary>Bars in display order and their total (B-15.1–3).</summary>
public sealed record DashboardChart(IReadOnlyList<DashboardBar> Bars, int Total);

/// <summary>At most ten preview rows, the full matching count, and the "View all" drill-down (B-15.6).</summary>
public sealed record DashboardList<T>(IReadOnlyList<T> Preview, int TotalCount, DashboardDrillDown ViewAll);

/// <summary>An Overdue CRs row (B-15.4); DaysOverdue is computed, never stored.</summary>
public sealed record OverdueCrRow(ChangeRequestReadModel ChangeRequest, int DaysOverdue);

public sealed record DashboardReviewSummary(DashboardMetric Open, DashboardMetric DoneThisWeek, DashboardMetric DoneThisMonth);

/// <summary>Everything the Dashboard shows for one consistent projection state (B-15).</summary>
/// <param name="TotalTasks">Active + Completed tasks in the task scope; the status and squad chart totals equal it (AC-18).</param>
/// <param name="IsDatasetEmpty">No tasks and no CRs at all: the page offers New CR and New task (B-15.7).</param>
public sealed record DashboardResult(
    DashboardMetric ActiveTasks,
    DashboardMetric CompletedTasks,
    DashboardMetric UnscheduledTasks,
    DashboardMetric OverdueCrs,
    DashboardMetric ActiveSubtaskCards,
    int TotalTasks,
    DashboardReviewSummary Reviews,
    DashboardChart TasksByStatus,
    DashboardChart TasksBySquad,
    DashboardChart OpenReviewsByDeveloper,
    DashboardList<OverdueCrRow> OverdueList,
    DashboardList<TaskReadModel> UnlinkedList,
    bool IsDatasetEmpty);

/// <summary>The current records the Dashboard summarizes. Archived tasks are included; the calculator excludes them.</summary>
public sealed record DashboardInput(
    IReadOnlyCollection<TaskReadModel> Tasks,
    IReadOnlyCollection<SubtaskCardReadModel> SubtaskCards,
    IReadOnlyCollection<ChangeRequestReadModel> ChangeRequests,
    IReadOnlyCollection<ReviewItem> Reviews,
    WorkflowSnapshot Workflow);

/// <param name="General">The squad label for unlinked tasks and CRs without a squad (ADR 0020).</param>
/// <param name="Unassigned">The developer label for reviews without a developer.</param>
public sealed record DashboardLabels(string General, string Unassigned);

/// <summary>
/// B-15 Dashboard metrics from the current read models and the local day. Pure and deterministic: every number is
/// computed with the same predicates the drill-down applies (<see cref="TaskFilterMatcher"/>,
/// <see cref="CrFilterMatcher"/>, <see cref="ReviewFilterMatcher"/>), so a card and the page it opens always agree.
/// Task metrics count tasks only, by Task.Id; Subtask cards, checklist items, reviews and CRs never inflate them.
/// </summary>
public static class DashboardCalculator
{
    public const int PreviewRows = 10;

    public static DashboardResult Calculate(DashboardInput input, DashboardFilter filter, DateOnly today, DashboardLabels labels)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(labels);

        var tasksById = input.Tasks.ToDictionary(t => t.Id);
        var crsById = input.ChangeRequests.ToDictionary(c => c.Id);
        var context = new TaskFilterContext(id => tasksById.GetValueOrDefault(id), id => crsById.GetValueOrDefault(id));
        var taskBase = TaskScope(filter);
        var crBase = CrScope(filter);

        // B-15: the task scope is every non-archived task that passes the Dashboard filters, counted once by ID.
        var scoped = input.Tasks.Where(t => TaskFilterMatcher.Matches(taskBase with { Dashboard = DashboardTaskMetric.AllTasks }, t, context)).ToList();
        var active = scoped.Count(t => !t.IsCompleted);
        var completed = scoped.Count - active;
        var unscheduled = scoped.Count(t => !t.IsCompleted && TaskFilterMatcher.IsUnscheduled(t, context));
        var cardSpec = taskBase with { Dashboard = DashboardTaskMetric.ActiveSubtaskCards };
        var activeCards = input.SubtaskCards.Count(c => TaskFilterMatcher.Matches(cardSpec, c, context));

        var overdueSpec = crBase with { DashboardOverdue = true };
        var overdue = input.ChangeRequests
            .Where(c => CrFilterMatcher.Matches(overdueSpec, c, today))
            .OrderBy(c => c.PlannedEnd)
            .ThenBy(c => c.PriorityOrder)
            .ThenBy(c => c.Id.Value)
            .Select(c => new OverdueCrRow(c, OverduePolicy.DaysOverdueByPlannedEnd(c.PlannedEnd, c.IsCompleted, today)))
            .ToList();

        var unlinkedSpec = taskBase with { Dashboard = DashboardTaskMetric.UnlinkedTasks };
        var unlinked = scoped
            .Where(t => TaskFilterMatcher.Matches(unlinkedSpec, t, context))
            .OrderByDescending(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id.Value)
            .ToList();

        return new DashboardResult(
            new DashboardMetric(active, new TaskDrillDown(taskBase with { Dashboard = DashboardTaskMetric.ActiveTasks })),
            new DashboardMetric(completed, new TaskDrillDown(taskBase with { Dashboard = DashboardTaskMetric.CompletedTasks })),
            new DashboardMetric(unscheduled, new TaskDrillDown(taskBase with { Dashboard = DashboardTaskMetric.UnscheduledTasks })),
            new DashboardMetric(overdue.Count, new CrDrillDown(overdueSpec)),
            new DashboardMetric(activeCards, new TaskDrillDown(cardSpec)),
            scoped.Count,
            Reviews(input.Reviews, today),
            StatusChart(input.Workflow, scoped, taskBase),
            SquadChart(input.Workflow, scoped, taskBase, labels.General),
            ReviewChart(input.Workflow, input.Reviews, labels.Unassigned),
            new DashboardList<OverdueCrRow>([.. overdue.Take(PreviewRows)], overdue.Count, new CrDrillDown(overdueSpec)),
            new DashboardList<TaskReadModel>([.. unlinked.Take(PreviewRows)], unlinked.Count, new TaskDrillDown(unlinkedSpec)),
            input.Tasks.Count == 0 && input.ChangeRequests.Count == 0);
    }

    /// <summary>The task fields the Dashboard filters set; a drill-down replaces the Grid filters with these (B-10).</summary>
    public static TaskFilterSpec TaskScope(DashboardFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return new TaskFilterSpec
        {
            Squads = filter.Squad is { } squad ? [squad.Id] : [],
            Priorities = filter.Priority is { } priority ? [priority] : [],
            Owners = filter.Owner is { } owner ? [owner] : [],
        };
    }

    public static CrFilterSpec CrScope(DashboardFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return new CrFilterSpec
        {
            Squads = filter.Squad is { } squad ? [squad.Id] : [],
            Priorities = filter.Priority is { } priority ? [priority] : [],
            Owners = filter.Owner is { } owner ? [owner] : [],
        };
    }

    private static DashboardReviewSummary Reviews(IReadOnlyCollection<ReviewItem> reviews, DateOnly today)
    {
        DashboardMetric Metric(ReviewMetric metric)
        {
            var spec = new ReviewFilterSpec(metric, false, null);
            return new DashboardMetric(reviews.Count(r => ReviewFilterMatcher.Matches(spec, r, today)), new ReviewDrillDown(spec));
        }

        return new DashboardReviewSummary(Metric(ReviewMetric.Open), Metric(ReviewMetric.DoneThisWeek), Metric(ReviewMetric.DoneThisMonth));
    }

    /// <summary>B-15.1: configured status order, zero-count statuses included.</summary>
    private static DashboardChart StatusChart(WorkflowSnapshot workflow, List<TaskReadModel> scoped, TaskFilterSpec taskBase)
    {
        var bars = workflow.TaskStatuses
            .OrderBy(s => s.Order)
            .Select(s => new DashboardBar(
                Key(s.Id.Value),
                s.Name.Display,
                scoped.Count(t => t.Status.Id == s.Id),
                new TaskDrillDown(taskBase with { Statuses = [s.Id], Dashboard = DashboardTaskMetric.AllTasks })))
            .ToList();
        return new DashboardChart(bars, scoped.Count);
    }

    /// <summary>
    /// B-15.2: configured squad order with zero-count squads, then "General" for unlinked tasks and CRs without a
    /// squad. Each task is in exactly one bucket, so the total equals the status chart's.
    /// </summary>
    private static DashboardChart SquadChart(WorkflowSnapshot workflow, List<TaskReadModel> scoped, TaskFilterSpec taskBase, string general)
    {
        DashboardBar Bar(SquadId? id, string key, string label)
        {
            var excluded = taskBase.Squads.Count > 0 && !taskBase.Squads.Contains(id);
            return new DashboardBar(
                key,
                label,
                scoped.Count(t => t.Cr.SquadId == id),
                excluded ? null : new TaskDrillDown(taskBase with { Squads = [id], Dashboard = DashboardTaskMetric.AllTasks }));
        }

        var bars = workflow.Squads
            .OrderBy(s => s.Order)
            .Select(s => Bar(s.Id, Key(s.Id.Value), s.Name.Display))
            .Append(Bar(null, "General", general))
            .ToList();
        return new DashboardChart(bars, scoped.Count);
    }

    /// <summary>B-15.3: open reviews by developer, "Unassigned" for none; count descending, then name.</summary>
    private static DashboardChart ReviewChart(WorkflowSnapshot workflow, IReadOnlyCollection<ReviewItem> reviews, string unassigned)
    {
        var open = reviews.Where(r => !r.IsDone).ToList();
        var bars = open
            .GroupBy(r => r.Content.DeveloperId)
            .Select(g => new DashboardBar(
                g.Key is { } id ? Key(id.Value) : "Unassigned",
                g.Key is { } person ? workflow.FindPerson(person)?.Name.Display ?? unassigned : unassigned,
                g.Count(),
                new ReviewDrillDown(new ReviewFilterSpec(ReviewMetric.Open, true, g.Key))))
            .OrderByDescending(b => b.Count)
            .ThenBy(b => b.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToList();
        return new DashboardChart(bars, open.Count);
    }

    private static string Key(Guid id) => id.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
}
