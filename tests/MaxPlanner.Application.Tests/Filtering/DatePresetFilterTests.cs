using AwesomeAssertions;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using static MaxPlanner.Application.Tests.Filtering.ActiveDateFilterTests;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Filtering;

/// <summary>
/// The date submenus (design §2.6, owner decision 2026-11-07): rolling presets Past, Today, Tomorrow, This week, Next
/// week, Future, plus No date and one Customised range, combined as a union. Weeks run Sunday–Saturday. Presets are
/// saved as presets, and a range saved by an older version keeps its meaning.
/// </summary>
[Trait("Req", "B-10")]
public sealed class DatePresetFilterTests
{
    // TestWorkspace.Today is Thursday 2026-10-22: this week is Sun Oct 18 – Sat Oct 24.
    private static readonly DateOnly Thursday = Today;

    [Fact]
    public void Presets_resolve_against_today_with_sunday_to_saturday_weeks()
    {
        Thursday.DayOfWeek.Should().Be(DayOfWeek.Thursday);
        DatePresetRanges.Resolve(DatePresets.Past, Thursday).Should().Be(((DateOnly?)null, (DateOnly?)new DateOnly(2026, 10, 21)));
        DatePresetRanges.Resolve(DatePresets.Today, Thursday).Should().Be(((DateOnly?)Thursday, (DateOnly?)Thursday));
        DatePresetRanges.Resolve(DatePresets.Tomorrow, Thursday).Should().Be(((DateOnly?)new DateOnly(2026, 10, 23), (DateOnly?)new DateOnly(2026, 10, 23)));
        DatePresetRanges.Resolve(DatePresets.ThisWeek, Thursday).Should().Be(((DateOnly?)new DateOnly(2026, 10, 18), (DateOnly?)new DateOnly(2026, 10, 24)));
        DatePresetRanges.Resolve(DatePresets.NextWeek, Thursday).Should().Be(((DateOnly?)new DateOnly(2026, 10, 25), (DateOnly?)new DateOnly(2026, 10, 31)));
        DatePresetRanges.Resolve(DatePresets.Future, Thursday).Should().Be(((DateOnly?)new DateOnly(2026, 11, 1), (DateOnly?)null));
    }

    [Theory]
    [InlineData(2026, 10, 25, 2026, 10, 25)] // a Sunday starts its own week
    [InlineData(2026, 10, 31, 2026, 10, 25)] // a Saturday ends it
    [InlineData(2027, 1, 1, 2026, 12, 27)] // the week crosses the year
    public void This_week_is_the_sunday_to_saturday_week_containing_today(int y, int m, int d, int sy, int sm, int sd)
    {
        var start = new DateOnly(sy, sm, sd);
        DatePresetRanges.Resolve(DatePresets.ThisWeek, new DateOnly(y, m, d)).Should().Be(((DateOnly?)start, (DateOnly?)start.AddDays(6)));
    }

    [Fact]
    public void Selected_presets_combine_as_a_union()
    {
        var filter = new DateRangeFilter(null, null, false, DatePresets.Today | DatePresets.NextWeek);

        filter.Matches(Thursday, Thursday).Should().BeTrue();
        filter.Matches(new DateOnly(2026, 10, 27), Thursday).Should().BeTrue();
        filter.Matches(new DateOnly(2026, 10, 23), Thursday).Should().BeFalse("tomorrow is not selected");
        filter.Matches(null, Thursday).Should().BeFalse("No date is not selected");
        filter.IsSet.Should().BeTrue();
        filter.IsRolling.Should().BeTrue();
    }

    [Fact]
    public void Past_and_future_are_open_ended_and_leave_out_today_and_the_two_weeks()
    {
        var past = new DateRangeFilter(null, null, false, DatePresets.Past);
        var future = new DateRangeFilter(null, null, false, DatePresets.Future);

        past.Matches(Thursday.AddDays(-1), Thursday).Should().BeTrue();
        past.Matches(Thursday.AddDays(-4000), Thursday).Should().BeTrue();
        past.Matches(Thursday, Thursday).Should().BeFalse();
        future.Matches(new DateOnly(2026, 10, 31), Thursday).Should().BeFalse("still next week");
        future.Matches(new DateOnly(2026, 11, 1), Thursday).Should().BeTrue();
    }

    [Fact]
    public void No_date_keeps_records_without_the_date_alone_or_with_other_choices()
    {
        var noDate = new DateRangeFilter(null, null, IncludeUndated: true);

        noDate.IsSet.Should().BeTrue("No date alone is a filter");
        noDate.Matches(null, Thursday).Should().BeTrue();
        noDate.Matches(Thursday, Thursday).Should().BeFalse("only records without the date");

        var todayOrNone = noDate with { Presets = DatePresets.Today };
        todayOrNone.Matches(null, Thursday).Should().BeTrue();
        todayOrNone.Matches(Thursday, Thursday).Should().BeTrue();
        todayOrNone.Matches(Thursday.AddDays(1), Thursday).Should().BeFalse();
    }

    [Fact]
    public void A_customised_range_joins_the_union()
    {
        var filter = new DateRangeFilter(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 3), false, DatePresets.Tomorrow);

        filter.Matches(new DateOnly(2026, 12, 2), Thursday).Should().BeTrue();
        filter.Matches(new DateOnly(2026, 10, 23), Thursday).Should().BeTrue();
        filter.Matches(new DateOnly(2026, 12, 4), Thursday).Should().BeFalse();
        filter.IsRolling.Should().BeTrue("a preset is selected");
        (filter with { Presets = DatePresets.None }).IsRolling.Should().BeFalse("a customised range is fixed");
    }

    [Fact]
    public void Task_start_and_end_dates_use_the_presets_on_the_matching_day()
    {
        var spec = new TaskFilterSpec { StartDate = new DateRangeFilter(null, null, false, DatePresets.Today) };
        var task = Task(start: Thursday);

        TaskFilterMatcher.Matches(spec, task, Context(Thursday)).Should().BeTrue();
        TaskFilterMatcher.Matches(spec, task, Context(Thursday.AddDays(1))).Should().BeFalse("Today moved at midnight");
        spec.IsRolling.Should().BeTrue();

        var end = new TaskFilterSpec { EndDate = new DateRangeFilter(null, null, true, DatePresets.ThisWeek) };
        TaskFilterMatcher.Matches(end, Task(start: Thursday.AddDays(-3)), Context(Thursday)).Should().BeTrue("No date keeps the task without an end");
        TaskFilterMatcher.Matches(end, Task(start: Thursday.AddDays(-3), end: Thursday.AddDays(-1)), Context(Thursday)).Should().BeTrue();
        TaskFilterMatcher.Matches(end, Task(start: Thursday.AddDays(-9), end: Thursday.AddDays(-8)), Context(Thursday)).Should().BeFalse();
    }

    [Fact]
    public void Planned_dates_keep_the_overlap_rule_for_each_preset()
    {
        var crossing = Cr(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 26));

        Matches(DatePresets.Tomorrow, crossing).Should().BeTrue("the planned range covers tomorrow");
        Matches(DatePresets.NextWeek, crossing).Should().BeTrue("it reaches into next week");
        Matches(DatePresets.Future, crossing).Should().BeFalse();
        Matches(DatePresets.Past, crossing).Should().BeTrue("it started before today");
        Matches(DatePresets.Future, Cr(null, null)).Should().BeFalse();
        CrFilterMatcher.Matches(new CrFilterSpec { Planned = new DateRangeFilter(null, null, true) }, Cr(null, null), Thursday)
            .Should().BeTrue("No date keeps unscheduled CRs");

        static bool Matches(DatePresets presets, Application.ReadModels.ChangeRequestReadModel cr) =>
            CrFilterMatcher.Matches(new CrFilterSpec { Planned = new DateRangeFilter(null, null, false, presets) }, cr, Thursday);
    }

    [Fact]
    public void A_date_custom_field_uses_the_same_presets()
    {
        var field = new FieldId(Guid.NewGuid());
        var filter = new CustomFieldFilter(field, null, null, new DateRangeFilter(null, null, true, DatePresets.ThisWeek), []);

        filter.Matches(new Dictionary<FieldId, CustomValue> { [field] = new DateCustomValue(Thursday.AddDays(2)) }, Thursday).Should().BeTrue();
        filter.Matches(new Dictionary<FieldId, CustomValue> { [field] = new DateCustomValue(Thursday.AddDays(3)) }, Thursday).Should().BeFalse();
        filter.Matches(CustomValue.NoValues, Thursday).Should().BeTrue("No date is selected");
        new TaskFilterSpec { CustomFields = [filter] }.IsRolling.Should().BeTrue();
    }

    [Fact]
    public void Presets_no_date_and_a_customised_range_round_trip_as_presets()
    {
        var start = new DateRangeFilter(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7), true, DatePresets.Past | DatePresets.ThisWeek);
        var end = new DateRangeFilter(null, null, false, DatePresets.Future);
        var field = new FieldId(Guid.NewGuid());
        var custom = new CustomFieldFilter(field, null, null, new DateRangeFilter(null, null, false, DatePresets.NextWeek), []);

        var saved = FilterPersistence.ToPreference(new TaskFilterSpec { StartDate = start, EndDate = end, CustomFields = [custom] });

        saved.Fields["start"].Should().Equal("2026-11-01", "2026-11-07", "undated", "past", "thisWeek");
        saved.Fields["end"].Should().Equal(string.Empty, string.Empty, string.Empty, "future");
        var loaded = FilterPersistence.ToTaskSpec(saved);
        loaded.StartDate.Should().Be(start);
        loaded.EndDate.Should().Be(end);
        loaded.CustomFields.Single().Date.Should().Be(custom.Date);

        var planned = new DateRangeFilter(null, null, true, DatePresets.Today | DatePresets.Tomorrow);
        FilterPersistence.ToCrSpec(FilterPersistence.ToPreference(new CrFilterSpec { Planned = planned })).Planned.Should().Be(planned);
        FilterPersistence.ToPreference(new TaskFilterSpec { StartDate = new DateRangeFilter(null, null, false) }).Fields.Should().NotContainKey("start");
    }

    [Fact]
    public void A_range_saved_by_an_older_version_loads_as_customised_plus_no_date()
    {
        // The shape written before the presets existed: [from, to, "undated" or ""], and ["date", from, to, …] for a field.
        var field = new FieldId(Guid.NewGuid());
        var key = "custom:" + field.Value.ToString("N");
        var old = new ViewFilterPreference(null, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["start"] = ["2026-11-01", "2026-11-07", "undated"],
            ["end"] = ["2026-11-01", "", ""],
            [key] = ["date", "", "2026-12-31", "undated"],
        });

        var spec = FilterPersistence.ToTaskSpec(old);

        spec.StartDate.Should().Be(new DateRangeFilter(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7), true));
        spec.EndDate.Should().Be(new DateRangeFilter(new DateOnly(2026, 11, 1), null, true), "an older From-only End Date range meant \"or not ended yet\"");
        spec.CustomFields.Single().Date.Should().Be(new DateRangeFilter(null, new DateOnly(2026, 12, 31), true));
        spec.StartDate!.Presets.Should().Be(DatePresets.None);

        // The old meaning holds: in the range, or without the date.
        spec.StartDate.Matches(new DateOnly(2026, 11, 3), Thursday).Should().BeTrue();
        spec.StartDate.Matches(null, Thursday).Should().BeTrue();
        spec.StartDate.Matches(new DateOnly(2026, 11, 8), Thursday).Should().BeFalse();

        // It still keeps tasks without an end date, now through No date.
        spec.EndDate!.IncludeUndated.Should().BeTrue();
        TaskFilterMatcher.Matches(new TaskFilterSpec { EndDate = spec.EndDate }, Task(start: Thursday), Context(Thursday)).Should().BeTrue();
    }

    [Fact]
    public void Unknown_preset_names_are_dropped_and_an_inverted_range_is_never_loaded()
    {
        var saved = new ViewFilterPreference(null, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["start"] = ["", "", "", "today", "someday"],
            ["end"] = ["2026-11-08", "2026-11-01", "", "today"],
            ["planned"] = ["", "", "", "fortnight"],
        });

        FilterPersistence.ToTaskSpec(saved).StartDate.Should().Be(new DateRangeFilter(null, null, false, DatePresets.Today));
        FilterPersistence.ToTaskSpec(saved).EndDate.Should().BeNull("an inverted range is dropped, never guessed");
        FilterPersistence.ToCrSpec(saved).Planned.Should().BeNull("nothing readable is left");
    }
}
