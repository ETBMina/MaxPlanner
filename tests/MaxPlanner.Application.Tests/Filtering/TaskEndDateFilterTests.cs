using AwesomeAssertions;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Filtering;

/// <summary>
/// End Date ranges. An older saved From-only range meant "ended on or after From, or not ended yet"; it now loads with
/// No date set, so the matcher has no hidden rule and matches the same tasks as before.
/// </summary>
[Trait("Req", "B-10")]
public sealed class TaskEndDateFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly TaskFilterContext Context = new(_ => null, _ => null, Today);
    private static readonly DateOnly From = Today.AddDays(5);
    private static readonly DateOnly To = Today.AddDays(20);

    [Fact]
    public void An_older_from_only_end_range_loads_with_no_date_and_keeps_tasks_without_an_end_date()
    {
        var saved = new Application.Preferences.ViewFilterPreference(null, new Dictionary<string, IReadOnlyList<string>>
        {
            ["end"] = ["2026-11-01", "", ""],
        });

        var spec = FilterPersistence.ToTaskSpec(saved);

        spec.EndDate.Should().Be(new DateRangeFilter(new DateOnly(2026, 11, 1), null, IncludeUndated: true));
        Matches(spec, Compose(null)).Should().BeTrue("not ended yet");
        Matches(spec, Compose(new DateOnly(2026, 11, 1))).Should().BeTrue();
        Matches(spec, Compose(new DateOnly(2026, 10, 31))).Should().BeFalse();
        FilterPersistence.ToPreference(spec).Fields["end"].Should().Equal("2026-11-01", string.Empty, "undated");
    }

    [Fact]
    public void A_from_only_end_range_without_no_date_now_means_what_it_shows()
    {
        var spec = new TaskFilterSpec { EndDate = new DateRangeFilter(From, null, IncludeUndated: false) };

        Matches(spec, Compose(null)).Should().BeFalse("No date is not selected");
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
