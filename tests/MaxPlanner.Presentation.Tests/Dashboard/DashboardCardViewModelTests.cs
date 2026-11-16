using AwesomeAssertions;
using MaxPlanner.Presentation.Dashboard;

namespace MaxPlanner.Presentation.Tests.Dashboard;

/// <summary>Design §4.1 (U-05): every summary card has the same anatomy; a card without a caption leaves the caption line out.</summary>
[Trait("Req", "B-15")]
public sealed class DashboardCardViewModelTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Not completed · parent not archived", true)]
    public void The_caption_line_shows_only_with_a_caption(string? caption, bool expected)
    {
        var card = new DashboardCardViewModel("Dashboard_TestCard", "Label", _ => { }) { Caption = caption! };

        card.HasCaption.Should().Be(expected);
    }

    [Fact]
    public void Setting_the_caption_notifies_has_caption()
    {
        var card = new DashboardCardViewModel("Dashboard_TestCard", "Label", _ => { });
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        card.Caption = "of 32 total";

        changed.Should().Contain(nameof(DashboardCardViewModel.HasCaption));
    }
}
