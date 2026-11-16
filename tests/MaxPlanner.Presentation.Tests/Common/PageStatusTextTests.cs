using AwesomeAssertions;
using MaxPlanner.Presentation.Common;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>Design §2.8 (U-10): the status bar's left text never repeats the page subtitle.</summary>
public sealed class PageStatusTextTests
{
    [Theory]
    [InlineData("300 cards in 8 buckets", "300 cards in 8 buckets", "")]
    [InlineData("Filtered: 12 of 40 cards", "40 cards in 8 buckets", "Filtered: 12 of 40 cards")]
    [InlineData("Workweek Sunday–Thursday · Friday and Saturday hidden", "", "Workweek Sunday–Thursday · Friday and Saturday hidden")]
    [InlineData("Changes apply immediately", null, "Changes apply immediately")]
    [InlineData("", "Subtitle", "")]
    [InlineData(null, "Subtitle", "")]
    [InlineData(null, null, "")]
    [InlineData("", "", "")]
    public void The_status_shows_only_what_the_subtitle_does_not_say(string? status, string? subtitle, string expected) =>
        PageStatusText.ForStatusBar(status, subtitle).Should().Be(expected);

    [Fact]
    public void A_status_that_differs_only_in_case_still_shows() =>
        PageStatusText.ForStatusBar("Filtered", "filtered").Should().Be("Filtered");
}
