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
/// The Period filter: a record is in the period when its own start date (a CR's planned start) is inside it,
/// inclusive; the end date is never considered and undated records are excluded. Rolling presets resolve against the
/// day they are matched on.
/// </summary>
[Trait("Req", "B-10")]
public sealed class PeriodFilterTests
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
        Matches(PeriodFilter.Last7Days, Task(start: Today.AddDays(offset))).Should().Be(expected);
    }

    [Theory]
    [InlineData(-30, true)]
    [InlineData(0, true)]
    [InlineData(-31, false)]
    [InlineData(1, false)]
    public void Last_30_days_is_today_and_the_30_days_before_it(int offset, bool expected)
    {
        Matches(PeriodFilter.Last30Days, Task(start: Today.AddDays(offset))).Should().Be(expected);
    }

    [Fact]
    public void Only_the_start_date_counts()
    {
        Matches(PeriodFilter.Last7Days, Task(start: null)).Should().BeFalse("a record without a start date is outside a set period");
        Matches(PeriodFilter.Last7Days, Task(start: Today.AddDays(-20), end: Today)).Should().BeFalse("the end date is not considered");
        Matches(PeriodFilter.Last7Days, Task(start: Today.AddDays(-2), end: Today.AddDays(40))).Should().BeTrue();
        Matches(PeriodFilter.AllTime, Task(start: null)).Should().BeTrue("All time is no filter");
    }

    [Fact]
    public void A_custom_range_is_inclusive_and_may_be_open_at_either_end()
    {
        var from = Today.AddDays(-10);
        var to = Today.AddDays(-4);

        Matches(PeriodFilter.Custom(from, to), Task(start: from)).Should().BeTrue();
        Matches(PeriodFilter.Custom(from, to), Task(start: to)).Should().BeTrue();
        Matches(PeriodFilter.Custom(from, to), Task(start: to.AddDays(1))).Should().BeFalse();
        Matches(PeriodFilter.Custom(from, null), Task(start: Today.AddDays(100))).Should().BeTrue();
        Matches(PeriodFilter.Custom(null, to), Task(start: Today.AddDays(-400))).Should().BeTrue();
        PeriodFilter.Custom(to, from).IsInverted.Should().BeTrue();
        PeriodFilter.Custom(null, null).IsSet.Should().BeFalse();
    }

    [Fact]
    public void A_rolling_preset_resolves_against_the_day_it_is_matched_on()
    {
        var spec = new TaskFilterSpec { Period = PeriodFilter.Last7Days };
        var task = Task(start: Today.AddDays(-7));

        TaskFilterMatcher.Matches(spec, task, Context(Today)).Should().BeTrue();
        TaskFilterMatcher.Matches(spec, task, Context(Today.AddDays(1))).Should().BeFalse("the window moved at midnight");
        TaskFilterMatcher.Matches(spec with { Period = PeriodFilter.Custom(Today.AddDays(-7), Today) }, task, Context(Today.AddDays(1)))
            .Should().BeTrue("a custom range is fixed");
    }

    [Fact]
    public void A_subtask_card_uses_its_own_start_date()
    {
        var parent = Task(start: Today.AddDays(-60));
        var inside = Card(parent, Today.AddDays(-1));
        var outside = Card(parent, Today.AddDays(-9));
        var spec = new TaskFilterSpec { Period = PeriodFilter.Last7Days };
        var context = new TaskFilterContext(_ => parent, _ => null, Today);

        TaskFilterMatcher.Matches(spec, inside, context).Should().BeTrue();
        TaskFilterMatcher.Matches(spec, outside, context).Should().BeFalse();
    }

    [Fact]
    public void A_cr_is_in_the_period_by_its_planned_start()
    {
        var spec = new CrFilterSpec { Period = PeriodFilter.Last7Days };

        CrFilterMatcher.Matches(spec, Cr(Today.AddDays(-7), Today.AddDays(30)), Today).Should().BeTrue();
        CrFilterMatcher.Matches(spec, Cr(Today.AddDays(-8), Today), Today).Should().BeFalse("the planned end is not considered");
        CrFilterMatcher.Matches(spec, Cr(null, null), Today).Should().BeFalse("an unscheduled CR has no start");
        spec.FieldCount.Should().Be(1);
    }

    [Fact]
    public void An_open_review_counts_by_its_creation_day_and_a_done_one_by_its_completion_day()
    {
        var period = PeriodFilter.Last7Days;
        var open = new ReviewFilterSpec(ReviewMetric.Open, false, null, period);

        ReviewFilterMatcher.Matches(open, Review(createdDaysAgo: 7), Today, TimeZoneInfo.Utc).Should().BeTrue();
        ReviewFilterMatcher.Matches(open, Review(createdDaysAgo: 8), Today, TimeZoneInfo.Utc).Should().BeFalse();
        ReviewFilterMatcher.Matches(open with { Period = null }, Review(createdDaysAgo: 8), Today, TimeZoneInfo.Utc).Should().BeTrue();

        var doneThisMonth = new ReviewFilterSpec(ReviewMetric.DoneThisMonth, false, null, period);
        ReviewFilterMatcher.Matches(doneThisMonth, Review(createdDaysAgo: 60, completedDaysAgo: 2), Today, TimeZoneInfo.Utc).Should().BeTrue("created long ago, completed in the period");
        ReviewFilterMatcher.Matches(doneThisMonth, Review(createdDaysAgo: 3, completedDaysAgo: 9), Today, TimeZoneInfo.Utc).Should().BeFalse("completed before the period");
    }

    [Fact]
    public void A_review_creation_instant_is_placed_on_the_local_day_of_the_given_zone()
    {
        // Created at 12:00 UTC eight days ago: still eight days ago in UTC, but already seven days ago at UTC+13.
        var review = Review(createdDaysAgo: 8);
        var open = new ReviewFilterSpec(ReviewMetric.Open, false, null, PeriodFilter.Last7Days);
        var plus13 = TimeZoneInfo.CreateCustomTimeZone("Test+13", TimeSpan.FromHours(13), "Test+13", "Test+13");
        var minus12 = TimeZoneInfo.CreateCustomTimeZone("Test-12", TimeSpan.FromHours(-12), "Test-12", "Test-12");

        ReviewFilterMatcher.PeriodDay(review, TimeZoneInfo.Utc).Should().Be(Today.AddDays(-8));
        ReviewFilterMatcher.PeriodDay(review, plus13).Should().Be(Today.AddDays(-7));
        ReviewFilterMatcher.PeriodDay(review, minus12).Should().Be(Today.AddDays(-8));
        ReviewFilterMatcher.Matches(open, review, Today, TimeZoneInfo.Utc).Should().BeFalse();
        ReviewFilterMatcher.Matches(open, review, Today, plus13).Should().BeTrue();
    }

    [Fact]
    public void The_period_counts_as_one_field_and_shares_the_and_of_the_other_fields()
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
    public void Older_and_unreadable_saved_periods_load_as_all_time()
    {
        Load().Should().BeNull("a configuration.json from before the Period field has no key");
        Load("last7").Should().Be(PeriodFilter.Last7Days);
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

    private static TaskFilterContext Context(DateOnly today) => new(_ => null, _ => null, today);

    private static bool Matches(PeriodFilter period, TaskReadModel task) => Matches(new TaskFilterSpec { Period = period }, task);

    private static bool Matches(TaskFilterSpec spec, TaskReadModel task) => TaskFilterMatcher.Matches(spec, task, Context(Today));

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

    private static SubtaskCardReadModel Card(TaskReadModel parent, DateOnly start)
    {
        var parentTask = TaskItem.Create(
            parent.Id, new TaskContent(TitleText.Create("Release").Value, null, null, null, [], null, CustomValue.NoValues),
            Backlog, Medium.Id, DateIntent.Unchanged, DateIntent.Unchanged, null, 1, Today, Now).Value;
        var card = SubtaskCard.Create(
            new SubtaskCardId(Guid.NewGuid()), parent.Id, SubtitleText.Create("API").Value, null, Backlog,
            DateIntent.Set(start), DateIntent.Unchanged, 1, Today, Now).Value;
        return Composer.ComposeSubtaskCard(card, parentTask, null, SeedWorkflow);
    }
}
