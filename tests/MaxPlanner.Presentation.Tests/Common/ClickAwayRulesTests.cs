using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Dialogs;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// One press on an editor's content that takes no focus moves focus from the field that has it to the dialog's neutral
/// focus; a press on another control, inside the focused field, or in a popup leaves focus where WinUI put it.
/// </summary>
public sealed class ClickAwayRulesTests
{
    [Fact]
    public void A_press_on_content_that_takes_no_focus_leaves_the_focused_field() =>
        ClickAwayRules.RestsFocusAfterPress(isPressOnDialogContent: true, wasFieldFocused: true, isPressInFocusedField: false, isFocusUnchanged: true)
            .Should().BeTrue();

    [Fact]
    public void A_press_on_a_control_that_takes_focus_keeps_that_focus() =>
        ClickAwayRules.RestsFocusAfterPress(isPressOnDialogContent: true, wasFieldFocused: true, isPressInFocusedField: false, isFocusUnchanged: false)
            .Should().BeFalse();

    [Fact]
    public void A_press_inside_the_focused_field_keeps_focus_there() =>
        ClickAwayRules.RestsFocusAfterPress(isPressOnDialogContent: true, wasFieldFocused: true, isPressInFocusedField: true, isFocusUnchanged: true)
            .Should().BeFalse();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_press_in_a_popup_is_left_alone(bool isFocusUnchanged) =>
        ClickAwayRules.RestsFocusAfterPress(isPressOnDialogContent: false, wasFieldFocused: true, isPressInFocusedField: false, isFocusUnchanged)
            .Should().BeFalse();

    [Fact]
    public void A_press_while_focus_is_already_neutral_or_outside_the_dialog_changes_nothing() =>
        ClickAwayRules.RestsFocusAfterPress(isPressOnDialogContent: true, wasFieldFocused: false, isPressInFocusedField: false, isFocusUnchanged: true)
            .Should().BeFalse();
}
