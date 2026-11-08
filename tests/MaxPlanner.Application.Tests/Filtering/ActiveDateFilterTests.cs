using AwesomeAssertions;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Filtering;

/// <summary>
/// The Active date filter (was "Period", owner decision 2026-11-07): a record matches when its active span overlaps
/// the range, inclusive. The span is Start Date → End Date (a CR's planned start → end, a review's creation → completion);
/// a missing end is open-ended, a missing start never matches. Rolling presets resolve against the day they are
/// matched on.
/// </summary>
[Trait("Req", "B-10")]
public sealed class ActiveDateFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly ReadModelComposer Composer = new();

    [Theory]
    [InlineData(-7, true)]
    [InlineData(-3, true)]
    [InlineData(0, true)]
    [InlineData(-8, false)]
    [InlineData(1, false)]
    public void Last_7_days_is_today_and_the_7_days_before_it(int offset, bool expected)
    {
        var day = Today.AddDays(offset);
        Matches(PeriodFilter.Last7Days, Task(start: day, end: day)).Should().Be(expected);
    }

    [Theory]
    [InlineData(-30, true)]
    [InlineData(0, true)]
    [InlineData(-31, false)]
    [InlineData(1, false)]
    public void Last_30_days_is_today_and_the_30_days_before_it(int offset, bool expected)
    {
        var day = Today.AddDays(offset);
        Matches(PeriodFilter.Last30Days, Task(start: day, end: day)).Should().Be(expected);
    }

    [Fact]
    public void A_record_matches_when_its_active_span_overlaps_the_range()
    {
        Matches(PeriodFilter.Last7Days, Task(start: Today.AddDays(-20), end: Today.AddDays(-7))).Should().BeTrue("it ended on the first day of the range");
        Matches(PeriodFilter.Last7Days, Task(start: Today.AddDays(-20), end: Today.AddDays(-8))).Should().BeFalse("it ended before the range");
        Matches(PeriodFilter.Last7Days, Task(start: Today.AddDays(-20), end: Today.AddDays(30))).Should().BeTrue("it spans the whole range");
        Matches(PeriodFilter.Last7Days, Task(start: Today.AddDays(-2), end: Today.AddDays(40))).Should().BeTrue("it started in the range");
        Matches(PeriodFilter.Last7Days, Task(start: Today.AddDays(1))).Should().BeFalse("it starts after the range");
    }

    [Fact]
    public void A_missing_end_is_still_active_and_a_missing_start_never_matches()
    {
        Matches(PeriodFilter.Last7Days, Task(start: Today.AddDays(-400))).Should().BeTrue("a record without an end is still in progress");
        Matches(PeriodFilter.Last7Days, Task(start: null)).Should().BeFalse("a record without a start is excluded");
        Matches(PeriodFilter.AllTime, Task(start: null)).Should().BeTrue("All time is no filter");
        PeriodFilter.Last7Days.Matches(null, Today, Today).Should().BeFalse("an end alone is not a span");
    }

    [Fact]
    public void A_custom_range_is_inclusive_and_may_be_open_at_either_end()
    {
        var from = Today.AddDays(-10);
        var to = Today.AddDays(-4);

        Matches(PeriodFilter.Custom(from, to), Task(start: to, end: to)).Should().BeTrue();
        Matches(PeriodFilter.Custom(from, to), Task(start: from.AddDays(-5), end: from)).Should().BeTrue();
        Matches(PeriodFilter.Custom(from, to), Task(start: to.AddDays(1), end: to.AddDays(2))).Should().BeFalse();
        Matches(PeriodFilter.Custom(from, to), Task(start: from.AddDays(-5), end: from.AddDays(-1))).Should().BeFalse();
        Matches(PeriodFilter.Custom(from, null), Task(start: Today.AddDays(100), end: Today.AddDays(101))).Should().BeTrue();
        Matches(PeriodFilter.Custom(null, to), Task(start: Today.AddDays(-400), end: Today.AddDays(-399))).Should().BeTrue();
        PeriodFilter.Custom(to, from).IsInverted.Should().BeTrue();
        PeriodFilter.Custom(null, null).IsSet.Should().BeFalse();
    }

    [Fact]
    public void A_rolling_preset_resolves_against_the_day_it_is_matched_on()
    {
        var spec = new TaskFilterSpec { Period = PeriodFilter.Last7Days };
        var task = Task(start: Today.AddDays(-9), end: Today.AddDays(-7));

        TaskFilterMatcher.Matches(spec, task, Context(Today)).Should().BeTrue();
        TaskFilterMatcher.Matches(spec, task, Context(Today.AddDays(1))).Should().BeFalse("the window moved at midnight");
        TaskFilterMatcher.Matches(spec with { Period = PeriodFilter.Custom(Today.AddDays(-7), Today) }, task, Context(Today.AddDays(1)))
            .Should().BeTrue("a custom range is fixed");
        spec.IsRolling.Should().BeTrue();
        (spec with { Period = PeriodFilter.Custom(Today, Today) }).IsRolling.Should().BeFalse();
    }

    [Fact]
    public void A_subtask_card_uses_its_own_dates()
    {
        var parent = Task(start: Today.AddDays(-60));
        var ongoing = Card(parent, Today.AddDays(-30));
        var finished = Card(parent, Today.AddDays(-12), Today.AddDays(-9));
        var spec = new TaskFilterSpec { Period = PeriodFilter.Last7Days };
        var context = new TaskFilterContext(_ => parent, _ => null, Today);

        TaskFilterMatcher.Matches(spec, ongoing, context).Should().BeTrue("it has no end yet");
        TaskFilterMatcher.Matches(spec, finished, context).Should().BeFalse("it ended before the range");
    }

    [Fact]
    public void A_cr_matches_when_its_planned_range_overlaps()
    {
        var spec = new CrFilterSpec { Period = PeriodFilter.Last7Days };

        CrFilterMatcher.Matches(spec, Cr(Today.AddDays(-7), Today.AddDays(30)), Today).Should().BeTrue();
        CrFilterMatcher.Matches(spec, Cr(Today.AddDays(-30), Today.AddDays(-3)), Today).Should().BeTrue("the planned end is in the range");
        CrFilterMatcher.Matches(spec, Cr(Today.AddDays(-30), Today.AddDays(-8)), Today).Should().BeFalse("planned to end before the range");
        CrFilterMatcher.Matches(spec, Cr(null, null), Today).Should().BeFalse("an unscheduled CR has no start");
        spec.FieldCount.Should().Be(1);
    }

    [Fact]
    public void A_review_is_active_from_its_creation_until_it_is_done()
    {
        var range = PeriodFilter.Custom(Today.AddDays(-15), Today.AddDays(-10));
        var open = new ReviewFilterSpec(ReviewMetric.Open, false, null, range);

        ReviewFilterMatcher.Matches(open, Review(createdDaysAgo: 30), Today, TimeZoneInfo.Utc).Should().BeTrue("an open review is still active");
        ReviewFilterMatcher.Matches(open, Review(createdDaysAgo: 9), Today, TimeZoneInfo.Utc).Should().BeFalse("created after the range");
        ReviewFilterMatcher.Matches(open with { Period = null }, Review(createdDaysAgo: 9), Today, TimeZoneInfo.Utc).Should().BeTrue();

        var doneThisMonth = new ReviewFilterSpec(ReviewMetric.DoneThisMonth, false, null, PeriodFilter.Last7Days);
        ReviewFilterMatcher.Matches(doneThisMonth, Review(createdDaysAgo: 60, completedDaysAgo: 2), Today, TimeZoneInfo.Utc).Should().BeTrue("created long ago, done in the range");
        ReviewFilterMatcher.Matches(doneThisMonth, Review(createdDaysAgo: 12, completedDaysAgo: 9), Today, TimeZoneInfo.Utc).Should().BeFalse("done before the range");
    }

    [Fact]
    public void A_review_creation_instant_is_placed_on_the_local_day_of_the_given_zone()
    {
        // Created at 12:00 UTC on day −1: still day −1 in UTC, but already today at UTC+13. A range of "today" only.
        var review = Review(createdDaysAgo: 1, completedDaysAgo: 1);
        var done = new ReviewFilterSpec(ReviewMetric.DoneThisMonth, false, null, PeriodFilter.Custom(Today, Today));
        var plus13 = TimeZoneInfo.CreateCustomTimeZone("Test+13", TimeSpan.FromHours(13), "Test+13", "Test+13");
        var minus12 = TimeZoneInfo.CreateCustomTimeZone("Test-12", TimeSpan.FromHours(-12), "Test-12", "Test-12");

        ReviewFilterMatcher.CreatedDay(Review(createdDaysAgo: 8), TimeZoneInfo.Utc).Should().Be(Today.AddDays(-8));
        ReviewFilterMatcher.CreatedDay(Review(createdDaysAgo: 8), plus13).Should().Be(Today.AddDays(-7));
        ReviewFilterMatcher.CreatedDay(Review(createdDaysAgo: 8), minus12).Should().Be(Today.AddDays(-8));
        ReviewFilterMatcher.Matches(done, review, Today, TimeZoneInfo.Utc).Should().BeFalse("done on day −1");
        ReviewFilterMatcher.Matches(done with { Period = PeriodFilter.Custom(Today.AddDays(-1), Today.AddDays(-1)) }, review, Today, TimeZoneInfo.Utc).Should().BeTrue();
    }

    [Fact]
    public void The_active_date_counts_as_one_field_and_shares_the_and_of_the_other_fields()
    {
        var spec = new TaskFilterSpec { Period = PeriodFilter.Last7Days, Priorities = [High.Id] };

        spec.FieldCount.Should().Be(2);
        Matches(spec, Task(start: Today)).Should().BeFalse("the task is Medium");
    }

    [Fact]
    public void Rolling_presets_are_saved_as_presets_and_custom_ranges_as_iso_dates()
    {
        var rolling = FilterPersistence.ToPreference(new TaskFilterSpec { Period = PeriodFilter.Last30Days });
        rolling.Fields[FilterPersistence.PeriodKey].Should().Equal("last30");
        FilterPersistence.ToTaskSpec(rolling).Period.Should().Be(PeriodFilter.Last30Days);

        var custom = PeriodFilter.Custom(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7));
        var saved = FilterPersistence.ToPreference(new CrFilterSpec { Period = custom });
        saved.Fields[FilterPersistence.PeriodKey].Should().Equal("custom", "2026-11-01", "2026-11-07");
        FilterPersistence.ToCrSpec(saved).Period.Should().Be(custom);

        var openEnded = PeriodFilter.Custom(null, new DateOnly(2026, 11, 7));
        FilterPersistence.ToTaskSpec(FilterPersistence.ToPreference(new TaskFilterSpec { Period = openEnded })).Period.Should().Be(openEnded);
        FilterPersistence.ToPreference(new TaskFilterSpec { Period = PeriodFilter.AllTime }).Fields.Should().NotContainKey(FilterPersistence.PeriodKey);
    }

    [Fact]
    public void A_period_saved_by_an_older_version_loads_as_the_active_date()
    {
        Load().Should().BeNull("a configuration.json from before the Period field has no key");
        Load("last7").Should().Be(PeriodFilter.Last7Days);
        Load("last30").Should().Be(PeriodFilter.Last30Days);
        Load("custom", "2026-11-01", "2026-11-07").Should().Be(PeriodFilter.Custom(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7)));
        Load("last14").Should().BeNull("an unknown preset is dropped");
        Load("custom", "2026-11-08", "2026-11-01").Should().BeNull("an inverted range is dropped");
        Load("custom", "not a date", "").Should().BeNull();
        Load("custom").Should().BeNull();

        static PeriodFilter? Load(params string[] parts)
        {
            var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["status"] = [] };
            if (parts.Length > 0)
            {
                fields[FilterPersistence.PeriodKey] = parts;
            }

            return FilterPersistence.ToTaskSpec(new ViewFilterPreference(null, fields)).Period;
        }
    }

    internal static TaskFilterContext Context(DateOnly today) => new(_ => null, _ => null, today);

    internal static TaskReadModel Task(DateOnly? start, DateOnly? end = null, ChangeRequest? cr = null)
    {
        var content = new TaskContent(TitleText.Create("Release").Value, null, null, null, [], null, CustomValue.NoValues);
        var task = TaskItem.Create(
            new TaskId(Guid.NewGuid()), content, Backlog, Medium.Id,
            start is { } s ? DateIntent.Set(s) : DateIntent.Unchanged,
            end is { } e ? DateIntent.Set(e) : DateIntent.Unchanged,
            cr?.Id, 1_024, Today, Now).Value;
        return Composer.ComposeTask(task, cr, 0, SeedWorkflow);
    }

    internal static ChangeRequestReadModel Cr(DateOnly? start, DateOnly? end) =>
        Composer.ComposeChangeRequest(CrRecord(start, end), 0, Today, SeedWorkflow);

    internal static ChangeRequest CrRecord(DateOnly? start, DateOnly? end)
    {
        var planned = start is { } s && end is { } e ? PlannedRange.Create(s, e).Value : null;
        var content = new ChangeRequestContent(CrNumber.Create("CR-" + Guid.NewGuid().ToString("N")[..6]).Value, TitleText.Create("Instant card freeze").Value, Alice.Id,
            null, Medium.Id, planned, null, null, null, null, CustomValue.NoValues);
        return ChangeRequest.Create(new ChangeRequestId(Guid.NewGuid()), content, CrInProgress, Now);
    }

    internal static ReviewItem Review(int createdDaysAgo, int? completedDaysAgo = null, PersonId? developer = null)
    {
        var created = new DateTimeOffset(Today.AddDays(-createdDaysAgo).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        var review = ReviewItem.Create(
            new ReviewItemId(Guid.NewGuid()), new ReviewContent(TitleText.Create("Review").Value, null, null, [], developer, null), 1, created);
        return completedDaysAgo is { } days ? review.SetDone(true, Today.AddDays(-days), Now) : review;
    }

    internal static SubtaskCardReadModel Card(TaskReadModel parent, DateOnly start, DateOnly? end = null)
    {
        var parentTask = TaskItem.Create(
            parent.Id, new TaskContent(TitleText.Create("Release").Value, null, null, null, [], null, CustomValue.NoValues),
            Backlog, Medium.Id, DateIntent.Unchanged, DateIntent.Unchanged, null, 1, Today, Now).Value;
        var card = SubtaskCard.Create(
            new SubtaskCardId(Guid.NewGuid()), parent.Id, SubtitleText.Create("API").Value, null, Backlog,
            DateIntent.Set(start), end is { } e ? DateIntent.Set(e) : DateIntent.Unchanged, 1, Today, Now).Value;
        return Composer.ComposeSubtaskCard(card, parentTask, null, SeedWorkflow);
    }

    private static bool Matches(PeriodFilter period, TaskReadModel task) => Matches(new TaskFilterSpec { Period = period }, task);

    private static bool Matches(TaskFilterSpec spec, TaskReadModel task) => TaskFilterMatcher.Matches(spec, task, Context(Today));
}
