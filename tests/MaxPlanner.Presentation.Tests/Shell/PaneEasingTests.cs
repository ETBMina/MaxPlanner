using AwesomeAssertions;
using MaxPlanner.Presentation.Shell;

namespace MaxPlanner.Presentation.Tests.Shell;

public sealed class PaneEasingTests
{
    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1.5, 1)]
    public void The_ends_are_fixed(double time, double expected)
    {
        PaneEasing.Ease(time).Should().Be(expected);
    }

    [Fact]
    public void Progress_only_moves_forward()
    {
        // One direction only: the pane edge and the content never step back during a slide.
        var previous = 0.0;
        for (var i = 1; i <= 1000; i++)
        {
            var eased = PaneEasing.Ease(i / 1000.0);
            eased.Should().BeGreaterThanOrEqualTo(previous);
            previous = eased;
        }
    }

    [Theory]
    // Points on the stock KeySpline "0.0,0.35 0.15,1.0" (curve parameter s = 0.25, 0.5, 0.75).
    [InlineData(0.03671875, 0.30390625)]
    [InlineData(0.18125, 0.63125)]
    [InlineData(0.48515625, 0.89296875)]
    public void It_follows_the_stock_pane_spline(double time, double expected)
    {
        PaneEasing.Ease(time).Should().BeApproximately(expected, 1e-4);
    }
}
