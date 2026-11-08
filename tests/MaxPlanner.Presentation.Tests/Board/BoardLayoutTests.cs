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

    [Theory]
    [InlineData(1100)]
    [InlineData(1400)]
    public void Opening_or_closing_the_Left_pane_never_moves_the_page_edges(double window)
    {
        // The open pane (320 epx) or the closed one (48 epx) narrows the page across the L/M page breakpoint; the
        // padding follows the window, so the header and the buckets keep their insets and only the buckets resize.
        var open = BoardLayout.ForWidth(window - 320, window);
        var closed = BoardLayout.ForWidth(window - 48, window);

        open.HeaderTop.Should().Be(closed.HeaderTop).And.Be(24);
        open.HeaderBottom.Should().Be(closed.HeaderBottom);
        open.SidePadding.Should().Be(closed.SidePadding).And.Be(32);
    }

    [Fact]
    public void The_S_bucket_uses_the_window_side_padding()
    {
        // A 660-epx window (M) beside the compact pane leaves a 600-epx page (S): one bucket fills the page less
        // the window's M side padding.
        var layout = BoardLayout.ForWidth(600, 660);

        layout.IsSmall.Should().BeTrue();
        layout.SidePadding.Should().Be(24);
        layout.BucketWidth.Should().Be(600 - 48 - 12);
    }

    [Fact]
    public void The_S_bucket_follows_the_viewport_width()
    {
        BoardLayout.ForWidth(500).Should().NotBe(BoardLayout.ForWidth(520));
        BoardLayout.ForWidth(520).BucketWidth.Should().Be(520 - 32 - 12);
    }
}
