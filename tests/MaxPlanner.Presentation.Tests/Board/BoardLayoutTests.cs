using AwesomeAssertions;
using MaxPlanner.Presentation.Board;

namespace MaxPlanner.Presentation.Tests.Board;

[Trait("Req", "B-07")]
public sealed class BoardLayoutTests
{
    [Theory]
    [InlineData(1008, 300, 32, 24, 12, false)]
    [InlineData(1600, 300, 32, 24, 12, false)]
    [InlineData(640, 280, 24, 16, 12, false)]
    [InlineData(1007, 280, 24, 16, 12, false)]
    [InlineData(639, 595, 16, 12, 8, true)]
    [InlineData(400, 356, 16, 12, 8, true)]
    [InlineData(250, 260, 16, 12, 8, true)]
    public void Each_breakpoint_has_the_design_layout(double width, double bucket, double side, double top, double bottom, bool isSmall)
    {
        BoardLayout.ForWidth(width).Should().Be(new BoardLayout(bucket, side, top, bottom, isSmall));
    }

    [Theory]
    [InlineData(1008, 1920)]
    [InlineData(640, 1007)]
    public void Widths_within_the_L_or_M_breakpoint_need_no_layout_change(double narrower, double wider)
    {
        // The view skips equal layouts, so the navigation pane sliding or a resize within a breakpoint costs no
        // Board relayout.
        BoardLayout.ForWidth(narrower).Should().Be(BoardLayout.ForWidth(wider));
    }

    [Fact]
    public void The_S_bucket_follows_the_viewport_width()
    {
        BoardLayout.ForWidth(500).Should().NotBe(BoardLayout.ForWidth(520));
        BoardLayout.ForWidth(520).BucketWidth.Should().Be(520 - 32 - 12);
    }
}
