using AwesomeAssertions;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.Shell;

namespace MaxPlanner.Presentation.Tests.Shell;

[Trait("Req", "T-09")]
public sealed class WindowPlacementPolicyTests
{
    private static readonly PixelRect Primary = new(0, 0, 2560, 1400);
    private static readonly PixelRect Left = new(-1920, 0, 1920, 1040);
    private static readonly (int, int) DefaultSize = (1920, 1200);
    private static readonly (int, int) Minimum = (750, 750);

    [Fact]
    public void The_first_run_window_is_centered_on_the_primary_work_area()
    {
        var decision = WindowPlacementPolicy.Resolve(null, [Primary], Primary, DefaultSize, Minimum);

        decision.Should().Be(new WindowPlacementDecision(new PixelRect(320, 100, 1920, 1200), Maximize: false));
    }

    [Fact]
    public void The_first_run_size_shrinks_to_a_small_work_area()
    {
        var small = new PixelRect(0, 0, 1366, 728);

        var decision = WindowPlacementPolicy.Resolve(null, [small], small, DefaultSize, Minimum);

        decision.Bounds.Should().Be(new PixelRect(0, 0, 1366, 728));
    }

    [Fact]
    public void Saved_bounds_on_a_monitor_that_still_exists_are_kept_with_their_state()
    {
        var saved = new WindowPlacementPreference(new PixelRect(-1800, 20, 1200, 900), WindowStatePreference.Maximized, Left);

        var decision = WindowPlacementPolicy.Resolve(saved, [Primary, Left], Primary, DefaultSize, Minimum);

        decision.Should().Be(new WindowPlacementDecision(new PixelRect(-1800, 20, 1200, 900), Maximize: true));
    }

    [Fact]
    public void Bounds_from_a_removed_monitor_move_to_the_primary_display()
    {
        var saved = new WindowPlacementPreference(new PixelRect(-1800, 20, 1200, 900), WindowStatePreference.Normal, Left);

        var decision = WindowPlacementPolicy.Resolve(saved, [Primary], Primary, DefaultSize, Minimum);

        decision.Bounds.Should().Be(new PixelRect(680, 250, 1200, 900));
    }

    [Fact]
    public void Partly_visible_bounds_are_pulled_fully_into_the_display_that_shows_most_of_them()
    {
        var saved = new WindowPlacementPreference(new PixelRect(2000, 1000, 1200, 900), WindowStatePreference.Normal, null);

        var decision = WindowPlacementPolicy.Resolve(saved, [Primary, Left], Primary, DefaultSize, Minimum);

        decision.Bounds.Should().Be(new PixelRect(1360, 500, 1200, 900));
    }

    [Fact]
    public void Saved_sizes_are_kept_between_the_minimum_and_the_work_area()
    {
        var tooBig = new WindowPlacementPreference(new PixelRect(0, 0, 4000, 3000), WindowStatePreference.Normal, Primary);
        var tooSmall = new WindowPlacementPreference(new PixelRect(10, 10, 200, 100), WindowStatePreference.Normal, Primary);

        WindowPlacementPolicy.Resolve(tooBig, [Primary], Primary, DefaultSize, Minimum).Bounds.Should().Be(Primary);
        WindowPlacementPolicy.Resolve(tooSmall, [Primary], Primary, DefaultSize, Minimum).Bounds.Should().Be(new PixelRect(10, 10, 750, 750));
    }
}
