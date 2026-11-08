using AwesomeAssertions;
using MaxPlanner.Application.Dashboard;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.Filtering.ActiveDateFilterTests;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Filtering;

/// <summary>
/// The Dashboard Active date (was "Period") narrows every card, chart and list to the records active during the range
/// (tasks and Subtask cards by Start → End Date, CRs by planned range, open reviews from creation, still active), and
/// every drill-down carries it, so a count and its drill-down agree (ADR 0023).
/// </summary>
[Trait("Req", "B-15")]
public sealed class DashboardActiveDateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly DashboardLabels Labels = new("General", "Unassigned");
    private static readonly DashboardFilter Last7 = DashboardFilter.None with { Period = PeriodFilter.Last7Days };

    private readonly TaskReadModel _recent = Task(start: Today.AddDays(-3), end: Today.AddDays(-1));
    private readonly TaskReadModel _ongoing = Task(start: Today.AddDays(-20));
    private readonly TaskReadModel _old = Task(start: Today.AddDays(-20), end: Today.AddDays(-10));
    private readonly TaskReadModel _undated = Task(start: null);
    private readonly ChangeRequestReadModel _recentOverdue = Cr(Today.AddDays(-5), Today.AddDays(-1));
    private readonly ChangeRequestReadModel _oldOverdue = Cr(Today.AddDays(-20), Today.AddDays(-10));
    private readonly ReviewItem _recentOpen = Review(createdDaysAgo: 2, developer: Bob.Id);
    private readonly ReviewItem _oldOpen = Review(createdDaysAgo: 20, developer: Bob.Id);
    private readonly ReviewItem _oldDone = Review(createdDaysAgo: 20, completedDaysAgo: 0);

    [Fact]
    public void Without_an_active_date_everything_is_counted()
    {
        var result = Calculate(DashboardFilter.None);

        result.ActiveTasks.Count.Should().Be(4);
        result.OverdueCrs.Count.Should().Be(2);
        result.Reviews.Open.Count.Should().Be(2);
        DashboardFilter.None.IsActive.Should().BeFalse();
        (DashboardFilter.None with { Period = PeriodFilter.AllTime }).IsActive.Should().BeFalse("All time is no filter");
    }

    [Fact]
    public void The_active_date_keeps_the_records_active_during_the_range()
    {
        var result = Calculate(Last7);

        Last7.IsActive.Should().BeTrue();
        result.ActiveTasks.Count.Should().Be(2, "the recent task and the one still in progress");
        result.TotalTasks.Should().Be(2);
        result.TasksByStatus.Total.Should().Be(2);
        result.UnlinkedList.TotalCount.Should().Be(2);
        result.ActiveSubtaskCards.Count.Should().Be(1, "only the card whose own span reaches the range");
        result.OverdueCrs.Count.Should().Be(1);
        result.OverdueList.Preview.Single().ChangeRequest.Id.Should().Be(_recentOverdue.Id);
        result.Reviews.Open.Count.Should().Be(2, "an open review is still active");
        result.OpenReviewsByDeveloper.Total.Should().Be(2);
        result.Reviews.DoneThisWeek.Count.Should().Be(1, "the done-this-week count keeps its own period");
    }

    [Fact]
    public void A_custom_range_in_the_past_leaves_out_open_reviews_created_after_it()
    {
        var result = Calculate(DashboardFilter.None with { Period = PeriodFilter.Custom(Today.AddDays(-25), Today.AddDays(-15)) });

        result.Reviews.Open.Count.Should().Be(1, "the review created 2 days ago was not active then");
        result.ActiveTasks.Count.Should().Be(2, "the ongoing and the old task were active then");
        result.OverdueCrs.Count.Should().Be(1);
        result.OverdueList.Preview.Single().ChangeRequest.Id.Should().Be(_oldOverdue.Id);
    }

    [Fact]
    public void Every_drill_down_carries_the_active_date_and_agrees_with_its_count()
    {
        var result = Calculate(Last7);
        var context = new TaskFilterContext(_ => null, _ => null, Today);
        var tasks = new[] { _recent, _ongoing, _old, _undated };

        foreach (var metric in new[] { result.ActiveTasks, result.UnscheduledTasks, result.CompletedTasks })
        {
            var spec = metric.DrillDown.Should().BeOfType<TaskDrillDown>().Subject.Spec;
            spec.Period.Should().Be(PeriodFilter.Last7Days);
            tasks.Count(t => TaskFilterMatcher.Matches(spec, t, context)).Should().Be(metric.Count);
        }

        result.TasksByStatus.Bars.Concat(result.TasksBySquad.Bars)
            .Select(b => b.DrillDown).OfType<TaskDrillDown>()
            .Should().OnlyContain(d => d.Spec.Period == PeriodFilter.Last7Days);
        ((TaskDrillDown)result.UnlinkedList.ViewAll).Spec.Period.Should().Be(PeriodFilter.Last7Days);
        ((TaskDrillDown)result.ActiveSubtaskCards.DrillDown).Spec.Period.Should().Be(PeriodFilter.Last7Days);

        var overdue = ((CrDrillDown)result.OverdueCrs.DrillDown).Spec;
        overdue.Period.Should().Be(PeriodFilter.Last7Days);
        new[] { _recentOverdue, _oldOverdue }.Count(c => CrFilterMatcher.Matches(overdue, c, Today)).Should().Be(result.OverdueCrs.Count);

        var reviews = new[] { _recentOpen, _oldOpen, _oldDone };
        var open = ((ReviewDrillDown)result.Reviews.Open.DrillDown).Spec;
        open.Period.Should().Be(PeriodFilter.Last7Days);
        reviews.Count(r => ReviewFilterMatcher.Matches(open, r, Today, TimeZoneInfo.Utc)).Should().Be(result.Reviews.Open.Count);
        var bar = ((ReviewDrillDown)result.OpenReviewsByDeveloper.Bars.Single().DrillDown!).Spec;
        bar.Should().Be(new ReviewFilterSpec(ReviewMetric.Open, true, Bob.Id, PeriodFilter.Last7Days));
        ((ReviewDrillDown)result.Reviews.DoneThisWeek.DrillDown).Spec.Period.Should().BeNull();
    }

    [Fact]
    public void A_rolling_active_date_follows_the_dashboard_day()
    {
        var nextWeek = Calculate(Last7, Today.AddDays(8));

        nextWeek.ActiveTasks.Count.Should().Be(1, "the recent task ended 9 days before; only the ongoing one is still active");
    }

    private DashboardResult Calculate(DashboardFilter filter, DateOnly? today = null)
    {
        var parent = TaskItem.Create(
            _recent.Id, new TaskContent(TitleText.Create("Release").Value, null, null, null, [], null, CustomValue.NoValues),
            Backlog, Medium.Id, DateIntent.Unchanged, DateIntent.Unchanged, null, 1, Today, Now).Value;
        var cards = new (DateOnly Start, DateOnly? End)[] { (Today.AddDays(-1), null), (Today.AddDays(-12), Today.AddDays(-9)) }
            .Select(dates => new ReadModelComposer().ComposeSubtaskCard(
                SubtaskCard.Create(
                    new SubtaskCardId(Guid.NewGuid()), parent.Id, SubtitleText.Create("API").Value, null, Backlog,
                    DateIntent.Set(dates.Start), dates.End is { } end ? DateIntent.Set(end) : DateIntent.Unchanged, 1, Today, Now).Value,
                parent, null, SeedWorkflow))
            .ToList();
        var input = new DashboardInput([_recent, _ongoing, _old, _undated], cards, [_recentOverdue, _oldOverdue], [_recentOpen, _oldOpen, _oldDone], SeedWorkflow);
        return DashboardCalculator.Calculate(input, filter, today ?? Today, TimeZoneInfo.Utc, Labels);
    }
}
