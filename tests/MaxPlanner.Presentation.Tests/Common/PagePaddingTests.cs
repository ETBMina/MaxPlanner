using AwesomeAssertions;
using MaxPlanner.Presentation.Common;

namespace MaxPlanner.Presentation.Tests.Common;

[Trait("Req", "T-08")]
public sealed class PagePaddingTests
{
    [Theory]
    [InlineData(1920, 24)]
    [InlineData(1008, 24)]
    [InlineData(1007, 16)]
    [InlineData(640, 16)]
    [InlineData(639, 12)]
    [InlineData(400, 12)]
    public void The_header_top_follows_the_window_size_class(double windowWidth, double top)
    {
        PagePadding.Top(windowWidth).Should().Be(top);
    }

    [Theory]
    [InlineData(1920, 32)]
    [InlineData(1008, 32)]
    [InlineData(1007, 24)]
    [InlineData(640, 24)]
    [InlineData(639, 16)]
    [InlineData(400, 16)]
    public void The_side_padding_follows_the_window_size_class(double windowWidth, double side)
    {
        PagePadding.Side(windowWidth).Should().Be(side);
    }
}
