using AwesomeAssertions;
using MaxPlanner.Presentation.Board;

namespace MaxPlanner.Presentation.Tests.Board;

/// <summary>Design §4.2 (U-06): the ⋯ button never takes a line of its own, and a task title keeps the card's full width.</summary>
[Trait("Req", "B-07")]
public sealed class BoardCardLayoutTests
{
    [Fact]
    public void With_an_id_row_the_button_ends_it_and_the_title_spans_the_full_width()
    {
        BoardCardLayout.MenuRow(hasIdRow: true).Should().Be(BoardCardLayout.IdRow);
        BoardCardLayout.TitleColumnSpan(hasIdRow: true).Should().Be(2, "the button's column is free in the title row");
        BoardCardLayout.IdRowGap(hasIdRow: true).Should().Be(8);
    }

    [Fact]
    public void Without_an_id_row_the_button_ends_the_title_row_beside_the_title()
    {
        BoardCardLayout.MenuRow(hasIdRow: false).Should().Be(BoardCardLayout.TitleRow);
        BoardCardLayout.TitleColumnSpan(hasIdRow: false).Should().Be(1, "the button shares the title row");
        BoardCardLayout.IdRowGap(hasIdRow: false).Should().Be(0, "an absent ID row leaves no gap");
    }
}
