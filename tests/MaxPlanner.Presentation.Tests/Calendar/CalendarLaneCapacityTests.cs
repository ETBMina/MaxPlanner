using AwesomeAssertions;
using MaxPlanner.Domain.Calendar;
using MaxPlanner.Presentation.Calendar;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Calendar;

[Trait("Req", "B-08")]
public sealed class CalendarLaneCapacityTests
{
    [Fact]
    public void Month_caps_the_fitted_lanes_while_week_shows_every_lane_that_fits()
    {
        using var h = WorkspaceHarness.Create();
        var calendar = h.Workspace.Calendar;
        h.Settle();
        calendar.SetMode(CalendarMode.Month);

        calendar.SetLaneCapacity(9).Should().BeFalse("Month rows already hold the four-lane cap, so a taller row changes nothing");
        calendar.Surface.MaxLanes.Should().Be(CalendarLayoutPolicy.DefaultMaxLanes);

        calendar.SetMode(CalendarMode.Week);
        calendar.SetLaneCapacity(7).Should().BeTrue();
        calendar.Surface.MaxLanes.Should().Be(7, "Week mode fits lanes to the height with no upper cap");
        calendar.SetLaneCapacity(7).Should().BeFalse("an unchanged capacity does not rebuild the surface");
    }

    [Fact]
    public void Switching_modes_never_carries_the_week_lanes_into_month()
    {
        using var h = WorkspaceHarness.Create();
        var calendar = h.Workspace.Calendar;
        h.Settle();
        calendar.SetMode(CalendarMode.Week);
        calendar.SetLaneCapacity(9);

        calendar.SetMode(CalendarMode.Month);
        calendar.Surface.MaxLanes.Should().Be(CalendarLayoutPolicy.DefaultMaxLanes, "the Month cap applies before the view refits");

        calendar.SetLaneCapacity(2).Should().BeTrue("a short window lowers the Month lanes below the cap");
        calendar.Surface.MaxLanes.Should().Be(2);
    }
}
