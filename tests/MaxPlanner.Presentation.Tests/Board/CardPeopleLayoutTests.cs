using AwesomeAssertions;
using MaxPlanner.Presentation.Board;
using Windows.Foundation;

namespace MaxPlanner.Presentation.Tests.Board;

/// <summary>
/// The Board card avatar row's size depends only on its avatars, so a card's height is the same wherever the card is
/// in the bucket's viewport (the virtualizing StackLayout it replaces caused a layout cycle while scrolling).
/// </summary>
public sealed class CardPeopleLayoutTests
{
    [Fact]
    public void No_avatars_take_no_space() =>
        CardPeopleLayout.Extent([], 4).Should().Be(new Size(0, 0));

    [Fact]
    public void One_avatar_takes_its_own_size() =>
        CardPeopleLayout.Extent([new Size(20, 20)], 4).Should().Be(new Size(20, 20));

    [Fact]
    public void Avatars_sit_side_by_side_with_the_gap_between_them() =>
        CardPeopleLayout.Extent([new Size(20, 20), new Size(20, 20), new Size(20, 20)], 4).Should().Be(new Size(68, 20));

    [Fact]
    public void The_row_is_as_tall_as_its_tallest_child() =>
        CardPeopleLayout.Extent([new Size(20, 20), new Size(24, 24)], 4).Should().Be(new Size(48, 24));
}
