using AwesomeAssertions;
using MaxPlanner.Presentation.Common;

namespace MaxPlanner.Presentation.Tests.Common;

public sealed class WheelScrollingTests
{
    [Fact]
    public void Settled_view_steps_from_the_current_offset()
    {
        WheelScrolling.ComputeWheelTarget(300, double.NaN, 120, 600, 10_000).Should().Be(420);
        WheelScrolling.ComputeWheelTarget(300, double.NaN, -120, 600, 10_000).Should().Be(180);
    }

    [Fact]
    public void Fast_spin_builds_on_the_pending_target()
    {
        WheelScrolling.ComputeWheelTarget(300, 540, 120, 600, 10_000).Should().Be(660);
    }

    [Fact]
    public void Wheel_flood_never_leads_the_current_offset_by_more_than_two_viewports()
    {
        var pending = double.NaN;
        for (var i = 0; i < 1000; i++)
        {
            pending = WheelScrolling.ComputeWheelTarget(300, pending, 120, 600, 100_000);
        }

        pending.Should().Be(300 + (WheelScrolling.MaxLeadInViewports * 600));
        WheelScrolling.ComputeWheelTarget(5000, 50, -120, 600, 100_000).Should().Be(3800);
    }

    [Fact]
    public void Lead_always_allows_one_full_step()
    {
        // A snapped Board at S steps one viewport, and a zero viewport must not freeze the wheel.
        WheelScrolling.ComputeWheelTarget(0, double.NaN, 300, 300, 3000).Should().Be(300);
        WheelScrolling.ComputeWheelTarget(0, double.NaN, 120, 0, 3000).Should().Be(120);
    }

    [Fact]
    public void One_notch_scrolls_120_epx_toward_the_end_for_a_wheel_turned_toward_the_user_or_tilted_right()
    {
        WheelScrolling.ComputeWheelChange(-120, isHorizontalWheel: false).Should().Be(120);
        WheelScrolling.ComputeWheelChange(120, isHorizontalWheel: false).Should().Be(-120);
        WheelScrolling.ComputeWheelChange(120, isHorizontalWheel: true).Should().Be(120);
        WheelScrolling.ComputeWheelChange(-120, isHorizontalWheel: true).Should().Be(-120);
    }

    [Fact]
    public void High_resolution_deltas_scroll_proportionally()
    {
        WheelScrolling.ComputeWheelChange(-30, isHorizontalWheel: false).Should().Be(30);
        WheelScrolling.ComputeWheelChange(-360, isHorizontalWheel: false).Should().Be(360);
    }

    [Theory]
    [InlineData(-1680, 360)]
    [InlineData(-4080, 360)]
    [InlineData(3120, -360)]
    [InlineData(int.MinValue, 360)]
    [InlineData(int.MaxValue, -360)]
    public void Driver_accelerated_event_is_capped_at_three_notches(int delta, double expected)
    {
        WheelScrolling.ComputeWheelChange(delta, isHorizontalWheel: false).Should().Be(expected);
        WheelScrolling.ComputeWheelChange(-Math.Max(delta, -int.MaxValue), isHorizontalWheel: true).Should().Be(expected);
    }

    [Fact]
    public void Accelerated_spin_from_the_user_log_travels_a_bounded_distance()
    {
        // The reported spin (deltas from the log) moved the native Grid 3001 -> 12105 epx; capped, it travels 3000.
        var current = 0.0;
        var pending = double.NaN;
        foreach (var delta in new[] { -120, -240, -240, -240, -480, -1680, -1800, -4080, -3120, -2880 })
        {
            pending = WheelScrolling.ComputeWheelTarget(current, pending, WheelScrolling.ComputeWheelChange(delta, false), 765, 12_523);
            current = pending;
        }

        current.Should().Be(3000);
    }

    [Fact]
    public void Target_stays_within_the_scrollable_range()
    {
        WheelScrolling.ComputeWheelTarget(950, 1000, 120, 600, 1000).Should().Be(1000);
        WheelScrolling.ComputeWheelTarget(50, double.NaN, -120, 600, 1000).Should().Be(0);
        WheelScrolling.ComputeWheelTarget(0, double.NaN, 120, 600, 0).Should().Be(0);
    }
}
