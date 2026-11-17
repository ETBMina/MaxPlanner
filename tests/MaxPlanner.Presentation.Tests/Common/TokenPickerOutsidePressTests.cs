using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Controls;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// A single press outside the task editor's tag field takes focus out of it (the list closes, the focused look goes,
/// an empty input hides); a press on a suggestion, a chip or the field itself keeps the field's own behaviour, and a
/// picker that is not inline only closes its list.
/// </summary>
public sealed class TokenPickerOutsidePressTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, false)]
    public void The_picker_watches_presses_while_its_list_is_open_or_focus_is_in_an_inline_field(
        bool isListOpen, bool isInline, bool hasFocusInField, bool watches) =>
        TokenPickerRules.WatchesOutsidePress(isListOpen, isInline, hasFocusInField).Should().Be(watches);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_press_on_a_suggestion_is_a_pick(bool isInline) =>
        TokenPickerRules.OutsidePress(isInline, isInSuggestions: true, isInBox: false, isInField: false, isSameRoot: false)
            .Should().Be(OutsidePressStep.Pick);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_press_in_the_box_is_left_alone(bool isInline) =>
        TokenPickerRules.OutsidePress(isInline, isInSuggestions: false, isInBox: true, isInField: isInline, isSameRoot: true)
            .Should().Be(OutsidePressStep.None);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_press_under_another_root_is_left_alone(bool isInline) =>
        TokenPickerRules.OutsidePress(isInline, isInSuggestions: false, isInBox: false, isInField: false, isSameRoot: false)
            .Should().Be(OutsidePressStep.None);

    [Fact]
    public void A_press_on_a_chip_or_the_inline_field_itself_only_closes_the_list() =>
        TokenPickerRules.OutsidePress(isInline: true, isInSuggestions: false, isInBox: false, isInField: true, isSameRoot: true)
            .Should().Be(OutsidePressStep.CloseList);

    [Fact]
    public void A_press_outside_the_inline_field_leaves_it() =>
        TokenPickerRules.OutsidePress(isInline: true, isInSuggestions: false, isInBox: false, isInField: false, isSameRoot: true)
            .Should().Be(OutsidePressStep.LeaveField);

    [Fact]
    public void A_press_outside_a_picker_that_is_not_inline_only_closes_its_list() =>
        TokenPickerRules.OutsidePress(isInline: false, isInSuggestions: false, isInBox: false, isInField: false, isSameRoot: true)
            .Should().Be(OutsidePressStep.CloseList);
}
