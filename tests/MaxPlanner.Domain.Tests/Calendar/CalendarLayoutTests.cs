using AwesomeAssertions;
using MaxPlanner.Domain.Calendar;

namespace MaxPlanner.Domain.Tests.Calendar;

[Trait("Req", "B-08")]
[Trait("Req", "AC-11")]
public sealed class CalendarLayoutTests
{
    // Week of Sunday 18 October 2026: Sun 18, Mon 19, Tue 20, Wed 21, Thu 22, Fri 23, Sat 24.
    private static readonly DateOnly Sunday = new(2026, 10, 18);
    private static readonly IReadOnlyList<WeekRow> OneWeek = CalendarGrid.Week(Sunday.AddDays(3));

    [Fact]
    public void Month_rows_cover_every_week_that_intersects_the_month_including_adjacent_days()
    {
        // 1 October 2026 is a Thursday and 31 October a Saturday.
        var october = CalendarGrid.Month(2026, 10);
        // February 2026 starts on a Sunday and ends on a Saturday: exactly four rows.
        var february = CalendarGrid.Month(2026, 2);

        october.Select(r => r.WeekStart).Should().Equal(
            new DateOnly(2026, 9, 27), new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 25));
        october[0].VisibleDays.Should().StartWith(new DateOnly(2026, 9, 27), "September days are shown as adjacent-month dates");
        october[^1].WeekEnd.Should().Be(new DateOnly(2026, 10, 31));
        february.Should().HaveCount(4);
        february[0].WeekStart.Should().Be(new DateOnly(2026, 2, 1));
    }

    [Fact]
    public void Week_mode_shows_the_sunday_to_thursday_columns_of_the_selected_week()
    {
        OneWeek.Should().ContainSingle().Which.VisibleDays.Should().Equal(Sunday, Sunday.AddDays(1), Sunday.AddDays(2), Sunday.AddDays(3), Sunday.AddDays(4));
    }

    [Fact]
    [Trait("Req", "B-08.2")]
    public void Shifting_months_never_skips_a_month_from_a_long_month()
    {
        var january31 = new DateOnly(2026, 1, 31);

        var february = CalendarGrid.ShiftMonth(january31, 1);
        var march = CalendarGrid.ShiftMonth(february, 1);

        february.Should().Be(new DateOnly(2026, 2, 1));
        march.Should().Be(new DateOnly(2026, 3, 1));
        CalendarGrid.ShiftMonth(new DateOnly(2026, 3, 31), -1).Should().Be(new DateOnly(2026, 2, 1));
        CalendarGrid.ShiftMonth(new DateOnly(2026, 12, 31), 1).Should().Be(new DateOnly(2027, 1, 1));
    }

    [Fact]
    [Trait("Req", "B-08.6")]
    public void Lanes_are_ordered_by_full_duration_then_start_then_id()
    {
        var threeDaysEarlier = Item(1, Sunday.AddDays(1), Sunday.AddDays(3));
        var threeDaysLater = Item(2, Sunday.AddDays(2), Sunday.AddDays(4));
        var fiveDays = Item(3, Sunday, Sunday.AddDays(4));
        var tieA = Item(4, Sunday.AddDays(1), Sunday.AddDays(2));
        var tieB = Item(5, Sunday.AddDays(1), Sunday.AddDays(2));

        var layout = CalendarLayoutPolicy.Layout([tieB, threeDaysEarlier, threeDaysLater, tieA, fiveDays], OneWeek, maxLanes: 10);

        // Durations: fiveDays 5, both three-day items 3 (start decides), tieA/tieB 2 (same start, so the id decides).
        LaneOf(layout, fiveDays).Should().Be(0);
        LaneOf(layout, threeDaysEarlier).Should().Be(1);
        LaneOf(layout, threeDaysLater).Should().Be(2);
        LaneOf(layout, tieA).Should().Be(3);
        LaneOf(layout, tieB).Should().Be(4);
    }

    [Fact]
    [Trait("Req", "B-08.6")]
    public void First_fit_reuses_a_lane_that_is_free_for_the_whole_piece_even_before_an_existing_piece()
    {
        // The two-day item is placed first (longer). The one-day Sunday item comes later in the sort
        // but sits before it in the week, so both share lane 0.
        var wednesdayThursday = Item(1, Sunday.AddDays(3), Sunday.AddDays(4));
        var sundayOnly = Item(2, Sunday, Sunday);
        var mondayToWednesday = Item(3, Sunday.AddDays(1), Sunday.AddDays(3));

        var layout = CalendarLayoutPolicy.Layout([sundayOnly, wednesdayThursday, mondayToWednesday], OneWeek, maxLanes: 2);

        LaneOf(layout, mondayToWednesday).Should().Be(0);
        LaneOf(layout, wednesdayThursday).Should().Be(1);
        LaneOf(layout, sundayOnly).Should().Be(0);
        layout.Overflows.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "B-08.6")]
    [Trait("Req", "B-08.7")]
    public void A_range_that_only_spills_onto_hidden_days_is_clipped_without_a_continuation_cue()
    {
        var mondayToFriday = Item(1, Sunday.AddDays(1), Sunday.AddDays(5));
        var thursdayToSaturday = Item(2, Sunday.AddDays(4), Sunday.AddDays(6));
        var saturdayToTuesday = Item(3, Sunday.AddDays(-1), Sunday.AddDays(2));
        var insideWeek = Item(4, Sunday.AddDays(1), Sunday.AddDays(2));

        var layout = CalendarLayoutPolicy.Layout([mondayToFriday, thursdayToSaturday, saturdayToTuesday, insideWeek], OneWeek);

        Segment(layout, mondayToFriday).Should().BeEquivalentTo(new { StartColumn = 1, EndColumn = 4, ContinuesBefore = false, ContinuesAfter = false });
        Segment(layout, thursdayToSaturday).Should().BeEquivalentTo(new { StartColumn = 4, EndColumn = 4, ContinuesBefore = false, ContinuesAfter = false });
        Segment(layout, saturdayToTuesday).Should().BeEquivalentTo(new { StartColumn = 0, EndColumn = 2, ContinuesBefore = false, ContinuesAfter = false });
        Segment(layout, insideWeek).Should().BeEquivalentTo(new { StartColumn = 1, EndColumn = 2, ContinuesBefore = false, ContinuesAfter = false });
    }

    [Fact]
    [Trait("Req", "B-08.6")]
    public void The_cue_shows_when_the_range_reaches_another_visible_day_even_beyond_the_displayed_grid()
    {
        // Sunday to the following Friday: visible days on both rows of a two-week grid.
        var sundayToNextFriday = Item(1, Sunday, Sunday.AddDays(12));
        // Thursday to the next Sunday: the only other visible day is outside this one-week grid.
        var thursdayToSunday = Item(2, Sunday.AddDays(4), Sunday.AddDays(7));
        // Previous Thursday to this Monday: starts on a visible day before the grid.
        var thursdayBeforeToMonday = Item(3, Sunday.AddDays(-3), Sunday.AddDays(1));
        IReadOnlyList<WeekRow> twoWeeks = [new WeekRow(Sunday), new WeekRow(Sunday.AddDays(7))];

        var spanning = CalendarLayoutPolicy.Layout([sundayToNextFriday], twoWeeks).Segments;
        var oneWeek = CalendarLayoutPolicy.Layout([thursdayToSunday, thursdayBeforeToMonday], OneWeek);

        spanning.Select(s => (s.RowIndex, s.StartColumn, s.EndColumn, s.ContinuesBefore, s.ContinuesAfter))
            .Should().Equal((0, 0, 4, false, true), (1, 0, 4, true, false));
        Segment(oneWeek, thursdayToSunday).Should().BeEquivalentTo(new { StartColumn = 4, EndColumn = 4, ContinuesBefore = false, ContinuesAfter = true });
        Segment(oneWeek, thursdayBeforeToMonday).Should().BeEquivalentTo(new { StartColumn = 0, EndColumn = 1, ContinuesBefore = true, ContinuesAfter = false });
    }

    [Fact]
    [Trait("Req", "B-08.7")]
    public void A_multiweek_range_is_split_per_row_without_changing_its_duration()
    {
        var rows = CalendarGrid.Month(2026, 10);
        var threeWeeks = Item(1, new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 21));

        var segments = CalendarLayoutPolicy.Layout([threeWeeks], rows).Segments;

        threeWeeks.DurationDays.Should().Be(15);
        segments.Select(s => (s.RowIndex, s.StartColumn, s.EndColumn, s.ContinuesBefore, s.ContinuesAfter)).Should().Equal(
            (1, 3, 4, false, true),
            (2, 0, 4, true, true),
            (3, 0, 3, true, false));
    }

    [Fact]
    [Trait("Req", "B-08.9")]
    public void Items_beyond_the_lane_limit_are_counted_per_day_as_hidden()
    {
        var fullWeek = Enumerable.Range(1, 4).Select(i => Item(i, Sunday, Sunday.AddDays(4))).ToList();
        var mondayTuesday = Item(10, Sunday.AddDays(1), Sunday.AddDays(2));
        var wednesday = Item(11, Sunday.AddDays(3), Sunday.AddDays(3));
        var secondWednesday = Item(12, Sunday.AddDays(3), Sunday.AddDays(3));

        var layout = CalendarLayoutPolicy.Layout([.. fullWeek, mondayTuesday, wednesday, secondWednesday], OneWeek);

        layout.Segments.Select(s => s.ItemId).Should().BeEquivalentTo(fullWeek.Select(i => i.Id));
        layout.Overflows.Should().Equal(
            new DayOverflow(Sunday.AddDays(1), 1),
            new DayOverflow(Sunday.AddDays(2), 1),
            new DayOverflow(Sunday.AddDays(3), 2));
    }

    [Fact]
    [Trait("Req", "B-08.7")]
    public void A_range_wholly_on_hidden_days_is_reported_as_outside_visible_workdays()
    {
        var fridaySaturday = Item(1, Sunday.AddDays(5), Sunday.AddDays(6));
        var outsideDisplayedSpan = Item(2, Sunday.AddDays(12), Sunday.AddDays(13));

        var layout = CalendarLayoutPolicy.Layout([fridaySaturday, outsideDisplayedSpan], OneWeek);

        layout.Segments.Should().BeEmpty();
        layout.OutsideVisibleWorkdays.Should().Equal(fridaySaturday.Id);
    }

    [Fact]
    [Trait("Req", "B-08.2")]
    public void A_range_across_the_year_boundary_keeps_its_inclusive_dates_in_both_months()
    {
        // 1 January 2027 is a Friday, so January's first row starts on Sunday 27 December 2026.
        var newYear = Item(1, new DateOnly(2026, 12, 30), new DateOnly(2027, 1, 5));

        var december = CalendarLayoutPolicy.Layout([newYear], CalendarGrid.Month(2026, 12)).Segments;
        var january = CalendarLayoutPolicy.Layout([newYear], CalendarGrid.Month(2027, 1)).Segments;

        newYear.DurationDays.Should().Be(7);
        december.Select(s => (s.RowIndex, s.StartColumn, s.EndColumn, s.ContinuesBefore, s.ContinuesAfter))
            .Should().Equal((4, 3, 4, false, true));
        january.Select(s => (s.RowIndex, s.StartColumn, s.EndColumn, s.ContinuesBefore, s.ContinuesAfter))
            .Should().Equal((0, 3, 4, false, true), (1, 0, 2, true, false));
    }

    private static CalendarItem Item(int id, DateOnly start, DateOnly end) =>
        new(new Guid($"00000000-0000-0000-0000-{id:D12}"), start, end);

    private static CalendarSegment Segment(CalendarLayout layout, CalendarItem item) => layout.Segments.Single(s => s.ItemId == item.Id);

    private static int LaneOf(CalendarLayout layout, CalendarItem item) => Segment(layout, item).Lane;
}
