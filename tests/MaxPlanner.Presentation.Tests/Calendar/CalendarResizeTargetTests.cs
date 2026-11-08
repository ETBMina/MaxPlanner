using AwesomeAssertions;
using MaxPlanner.Presentation.Calendar;

namespace MaxPlanner.Presentation.Tests.Calendar;

[Trait("Req", "B-08")]
public sealed class CalendarResizeTargetTests
{
    // Five 100-epx day columns plus the 28-epx Fri–Sat stub; three week rows 120 epx tall below a 24-epx names row.
    private const double SurfaceWidth = 528;
    private const double StubWidth = 28;
    private static readonly double[] RowTops = [24, 144, 264];

    [Theory]
    [InlineData(50, 30, 0, 0)]
    [InlineData(450, 143, 0, 4)]
    [InlineData(150, 144, 1, 1)]
    [InlineData(250, 300, 2, 2)]
    [InlineData(250, 5000, 2, 2)]
    [InlineData(250, 0, 0, 2)]
    public void The_pointer_maps_to_the_week_row_and_day_column_under_it(double x, double y, int week, int column)
    {
        CalendarResizeTarget.CellAt(x, y, RowTops, SurfaceWidth, StubWidth).Should().Be((week, column));
    }

    [Theory]
    [InlineData(510)]
    [InlineData(527)]
    [InlineData(900)]
    public void The_Fri_Sat_stub_and_beyond_snap_to_Thursday(double x)
    {
        CalendarResizeTarget.CellAt(x, 200, RowTops, SurfaceWidth, StubWidth).Should().Be((1, 4));
    }

    [Fact]
    public void Left_of_the_grid_snaps_to_Sunday()
    {
        CalendarResizeTarget.CellAt(-40, 200, RowTops, SurfaceWidth, StubWidth).Should().Be((1, 0));
    }

    [Fact]
    public void The_end_moves_into_a_later_week_and_the_start_stays()
    {
        var start = new DateOnly(2026, 11, 2);
        var end = new DateOnly(2026, 11, 4);

        CalendarResizeTarget.Range(start, end, isStart: false, new DateOnly(2026, 11, 17)).Should().Be((start, new DateOnly(2026, 11, 17)));
    }

    [Fact]
    public void The_start_moves_into_an_earlier_week_and_the_end_stays()
    {
        var start = new DateOnly(2026, 11, 15);
        var end = new DateOnly(2026, 11, 18);

        CalendarResizeTarget.Range(start, end, isStart: true, new DateOnly(2026, 11, 3)).Should().Be((new DateOnly(2026, 11, 3), end));
    }

    [Fact]
    public void A_single_day_range_is_allowed()
    {
        var start = new DateOnly(2026, 11, 15);
        var end = new DateOnly(2026, 11, 18);

        CalendarResizeTarget.Range(start, end, isStart: false, start).Should().Be((start, start));
        CalendarResizeTarget.Range(start, end, isStart: true, end).Should().Be((end, end));
    }

    [Fact]
    public void An_inverted_range_is_rejected()
    {
        var start = new DateOnly(2026, 11, 15);
        var end = new DateOnly(2026, 11, 18);

        CalendarResizeTarget.Range(start, end, isStart: false, new DateOnly(2026, 11, 8)).Should().BeNull("the end cannot move before the start");
        CalendarResizeTarget.Range(start, end, isStart: true, new DateOnly(2026, 11, 22)).Should().BeNull("the start cannot move past the end");
    }

    [Fact]
    public void Covered_columns_are_the_visible_days_of_a_row_inside_the_range()
    {
        DateOnly[] row = [new(2026, 11, 8), new(2026, 11, 9), new(2026, 11, 10), new(2026, 11, 11), new(2026, 11, 12)];

        CalendarResizeTarget.CoveredColumns(row, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 17)).Should().Be((0, 4), "a range through the whole week covers every column");
        CalendarResizeTarget.CoveredColumns(row, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 10)).Should().Be((0, 2));
        CalendarResizeTarget.CoveredColumns(row, new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 20)).Should().Be((3, 4));
        CalendarResizeTarget.CoveredColumns(row, new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 14)).Should().BeNull("Friday and Saturday are hidden");
        CalendarResizeTarget.CoveredColumns(row, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 20)).Should().BeNull();
    }

    [Fact]
    public void A_preview_piece_takes_the_lowest_lane_free_over_its_columns()
    {
        (int, int, int)[] occupied = [(0, 0, 2), (1, 1, 1), (2, 3, 4)];

        CalendarResizeTarget.PreviewLane(occupied, 0, 2, laneCount: 4, preferred: 0).Should().Be((2, false), "lanes 0 and 1 are used over Sun-Tue");
        CalendarResizeTarget.PreviewLane(occupied, 3, 4, laneCount: 4, preferred: 0).Should().Be((0, false));
        CalendarResizeTarget.PreviewLane([], 0, 4, laneCount: 4, preferred: 3).Should().Be((0, false));
    }

    [Fact]
    public void A_full_row_overlays_the_dragged_bars_lane()
    {
        (int, int, int)[] occupied = [(0, 0, 4), (1, 0, 4)];

        CalendarResizeTarget.PreviewLane(occupied, 1, 2, laneCount: 2, preferred: 1).Should().Be((1, true));
        CalendarResizeTarget.PreviewLane(occupied, 1, 2, laneCount: 2, preferred: 3).Should().Be((1, true), "the lane is clamped to the row");
    }
}
