using AwesomeAssertions;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Filtering;

/// <summary>An End Date filter with From only also keeps tasks that have no end date yet.</summary>
[Trait("Req", "B-10")]
public sealed class TaskEndDateFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly TaskFilterContext Context = new(_ => null, _ => null, Today);
    private static readonly DateOnly From = Today.AddDays(5);
    private static readonly DateOnly To = Today.AddDays(20);

    [Fact]
    public void From_only_includes_tasks_without_an_end_date()
    {
        var spec = new TaskFilterSpec { EndDate = new DateRangeFilter(From, null, IncludeUndated: false) };

        Matches(spec, Compose(null)).Should().BeTrue();
        Matches(spec, Compose(From)).Should().BeTrue();
        Matches(spec, Compose(From.AddDays(-1))).Should().BeFalse();
    }

    [Fact]
    public void From_and_to_exclude_undated_tasks_unless_undated_is_included()
    {
        Matches(new TaskFilterSpec { EndDate = new DateRangeFilter(From, To, false) }, Compose(null)).Should().BeFalse();
        Matches(new TaskFilterSpec { EndDate = new DateRangeFilter(From, To, true) }, Compose(null)).Should().BeTrue();
        Matches(new TaskFilterSpec { EndDate = new DateRangeFilter(From, To, false) }, Compose(To)).Should().BeTrue();
    }

    [Fact]
    public void To_only_is_unchanged()
    {
        Matches(new TaskFilterSpec { EndDate = new DateRangeFilter(null, To, false) }, Compose(null)).Should().BeFalse();
        Matches(new TaskFilterSpec { EndDate = new DateRangeFilter(null, To, true) }, Compose(null)).Should().BeTrue();
        Matches(new TaskFilterSpec { EndDate = new DateRangeFilter(null, To, false) }, Compose(To)).Should().BeTrue();
    }

    [Fact]
    public void Start_date_from_only_still_excludes_undated_tasks()
    {
        var spec = new TaskFilterSpec { StartDate = new DateRangeFilter(From, null, false) };
        var task = Compose(null, startCleared: true);

        task.StartDate.Should().BeNull();
        Matches(spec, task).Should().BeFalse();
    }

    private static bool Matches(TaskFilterSpec spec, TaskReadModel task) => TaskFilterMatcher.Matches(spec, task, Context);

    private static TaskReadModel Compose(DateOnly? end, bool startCleared = false)
    {
        var content = new TaskContent(TitleText.Create("Release").Value, null, null, null, [], null, CustomValue.NoValues);
        var task = TaskItem.Create(
            new TaskId(Guid.NewGuid()), content, Backlog, Medium.Id,
            startCleared ? DateIntent.Cleared : DateIntent.Unchanged,
            end is { } day ? DateIntent.Set(day) : DateIntent.Unchanged,
            null, 1_024, Today, Now).Value;
        return new ReadModelComposer().ComposeTask(task, null, 0, SeedWorkflow);
    }
}
