using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Controls;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// Empty values do not look like values: an editor fact without a value shows a muted "None" named with its label, and an
/// empty person dropdown shows a neutral person outline instead of a coloured avatar. x:Bind hands a null string over as
/// an empty one, so each decision treats null and empty the same.
/// </summary>
public sealed class EmptyValueRulesTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("T-1042", false)]
    public void A_fact_without_a_value_shows_none(string? value, bool isNone) =>
        FactValueRules.IsNone(value).Should().Be(isNone);

    [Theory]
    [InlineData("Task number", "Task number: none")]
    [InlineData(null, "None")]
    [InlineData("", "None")]
    public void An_empty_fact_is_named_with_its_label(string? label, string expected) =>
        FactValueRules.NoneName(label, "{0}: none", "None").Should().Be(expected);

    [Theory]
    [InlineData(true, null, false, true)]
    [InlineData(true, "", false, true)]
    [InlineData(true, "Alice Smith", true, false)]
    [InlineData(false, "", false, false)]
    [InlineData(false, "Alice Smith", false, false)]
    public void A_person_dropdown_shows_the_avatar_for_a_person_and_the_outline_for_nobody(
        bool isSingleSelection, string? shownName, bool avatar, bool outline)
    {
        TokenPickerRules.ShowsPersonAvatar(isSingleSelection, shownName).Should().Be(avatar);
        TokenPickerRules.ShowsEmptyPersonIcon(isSingleSelection, shownName).Should().Be(outline);
    }
}
