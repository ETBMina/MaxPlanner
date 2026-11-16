using AwesomeAssertions;
using MaxPlanner.Presentation.Common;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>A trimmed label or caption shows its full text as a tooltip; untrimmed text has none (U-05).</summary>
public sealed class ToolTipWhenTrimmedTests
{
    [Theory]
    [InlineData(true, "Incomplete CRs whose planned end is before today", "Incomplete CRs whose planned end is before today")]
    [InlineData(false, "Incomplete CRs whose planned end is before today", null)]
    [InlineData(true, "", null)]
    [InlineData(true, null, null)]
    [InlineData(false, null, null)]
    public void The_tooltip_shows_only_for_trimmed_text(bool isTrimmed, string? text, string? expected) =>
        BindFunctions.ToolTipWhenTrimmed(isTrimmed, text).Should().Be(expected);
}
