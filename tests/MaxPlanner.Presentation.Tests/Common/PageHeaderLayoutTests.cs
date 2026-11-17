using AwesomeAssertions;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Controls;
using Microsoft.UI.Xaml;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// Design §2.3: the shared page header keeps each page's header exactly as it was: the padding by page and window
/// width, and spacing only where there is something to separate (a missing subtitle, action group or row adds no gap).
/// </summary>
public sealed class PageHeaderLayoutTests
{
    [Theory]
    [InlineData(1400, 1400, 32, 24, 12)]
    [InlineData(1008, 1008, 32, 24, 12)]
    [InlineData(800, 800, 24, 16, 12)]
    [InlineData(640, 640, 24, 16, 12)]
    [InlineData(639, 639, 16, 12, 8)]
    [InlineData(360, 360, 16, 12, 8)]
    public void The_padding_follows_the_window_for_the_sides_and_top_and_the_page_for_the_bottom(double page, double window, double side, double top, double bottom) =>
        PageHeaderLayout.Padding(page, window).Should().Be(new Thickness(side, top, side, bottom));

    [Theory]
    [InlineData(1200, 1028)]
    [InlineData(500, 328)]
    [InlineData(172, 0)]
    [InlineData(100, 0)]
    public void The_actions_get_the_row_but_the_gap_and_the_title_blocks_minimum(double rowWidth, double expected) =>
        PageHeaderLayout.ActionsMaxWidth(rowWidth).Should().Be(expected);

    [Fact]
    public void An_open_navigation_pane_narrows_the_page_but_keeps_the_windows_insets()
    {
        // L window (1100) with the inline pane open: the page is M-wide (760), so only the bottom follows the page.
        PageHeaderLayout.Padding(760, 1100).Should().Be(new Thickness(32, 24, 32, 12));

        // An S page in an L window keeps the L insets and takes the S bottom.
        PageHeaderLayout.Padding(600, 1100).Should().Be(new Thickness(32, 24, 32, 8));
    }

    [Fact]
    public void The_padding_matches_the_board_layouts_header_insets()
    {
        foreach (var width in new[] { 360d, 639, 640, 1007, 1008, 1600 })
        {
            var board = Presentation.Board.BoardLayout.ForWidth(width);
            PageHeaderLayout.Padding(width, width).Should().Be(new Thickness(board.SidePadding, board.HeaderTop, board.SidePadding, board.HeaderBottom));
        }
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "", true)]
    [InlineData(false, "Grid_PageSubtitle", true)]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    public void A_subtitle_line_is_shown_when_asked_for_or_named_never_because_its_text_is_not_null(bool hasSubtitle, string? uid, bool expected) =>
        PageHeaderLayout.ShowsSubtitle(hasSubtitle, uid).Should().Be(expected);

    [Fact]
    public void A_named_subtitle_reads_its_resource_and_a_bound_one_shows_its_text_with_null_as_empty()
    {
        static string Lookup(string uid) => "[" + uid + "]";

        PageHeaderLayout.SubtitleText("Grid_PageSubtitle", "ignored", Lookup).Should().Be("[Grid_PageSubtitle]");
        PageHeaderLayout.SubtitleText(null, "3 open tasks", Lookup).Should().Be("3 open tasks");
        PageHeaderLayout.SubtitleText("", "3 open tasks", Lookup).Should().Be("3 open tasks");
        PageHeaderLayout.SubtitleText(null, null, Lookup).Should().BeEmpty();
    }

    [Fact]
    public void Spacing_is_kept_only_between_parts_that_exist()
    {
        (PageHeaderLayout.TitleSpacing(true), PageHeaderLayout.TitleSpacing(false)).Should().Be((2d, 0d));
        (PageHeaderLayout.BodySpacing(true), PageHeaderLayout.BodySpacing(false)).Should().Be((8d, 0d));
        (PageHeaderLayout.ColumnSpacing(true), PageHeaderLayout.ColumnSpacing(false)).Should().Be((12d, 0d));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(0.5, true)]
    [InlineData(1200, true)]
    public void A_hidden_pages_width_of_zero_is_not_laid_out(double width, bool expected) =>
        PageLayout.IsLayoutWidth(width).Should().Be(expected);
}
