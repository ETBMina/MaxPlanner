using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Dialogs;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// An editor dialog opens with no visible focus: its own focus passes go to the neutral first focus (the dialog itself),
/// whatever device they report and however the dialog was opened, until new user input, a field request or the settled
/// open; then the first Tab goes to the first field.
/// </summary>
public sealed class FirstFocusWindowTests
{
    [Fact]
    public void A_click_opened_dialog_redirects_its_own_focus_and_ignores_the_release_of_the_opening_click()
    {
        var window = new FirstFocusWindow();
        window.ShouldRedirect(isNeutral: false).Should().BeTrue();
        window.OnInput(FirstFocusInput.PointerReleased, false).Should().BeFalse();
        window.ShouldRedirect(isNeutral: false).Should().BeTrue();
    }

    [Fact]
    public void A_touch_opened_dialog_keeps_redirecting_after_the_lifted_finger_until_the_open_settles()
    {
        var window = new FirstFocusWindow();
        window.OnInput(FirstFocusInput.PointerReleased, false).Should().BeFalse();
        window.ShouldRedirect(isNeutral: false).Should().BeTrue();
        window.Close();
        window.ShouldRedirect(isNeutral: false).Should().BeFalse();
    }

    [Fact]
    public void A_keyboard_opened_dialog_ignores_the_opening_keys_release_and_its_auto_repeat()
    {
        var window = new FirstFocusWindow();
        window.OnInput(FirstFocusInput.KeyUp, false).Should().BeFalse();
        window.OnInput(FirstFocusInput.KeyDown, isKeyRepeat: true).Should().BeFalse();
        window.ShouldRedirect(isNeutral: false).Should().BeTrue();
    }

    [Theory]
    [InlineData(FirstFocusInput.PointerPressed, false)]
    [InlineData(FirstFocusInput.KeyDown, false)]
    public void New_user_input_ends_the_opening(FirstFocusInput input, bool isKeyRepeat)
    {
        var window = new FirstFocusWindow();
        window.OnInput(input, isKeyRepeat).Should().BeTrue();
        window.ShouldRedirect(isNeutral: false).Should().BeFalse();
    }

    [Fact]
    public void Focus_already_on_the_neutral_first_focus_is_left_alone() =>
        new FirstFocusWindow().ShouldRedirect(isNeutral: true).Should().BeFalse();

    [Theory]
    [InlineData(FirstFocusInput.PointerPressed, false, true)]
    [InlineData(FirstFocusInput.PointerReleased, false, false)]
    [InlineData(FirstFocusInput.KeyDown, false, true)]
    [InlineData(FirstFocusInput.KeyDown, true, false)]
    [InlineData(FirstFocusInput.KeyUp, false, false)]
    public void Only_a_press_or_a_fresh_key_down_is_new_input(FirstFocusInput input, bool isKeyRepeat, bool ends) =>
        FirstFocusWindow.EndsWindow(input, isKeyRepeat).Should().Be(ends);

    [Fact]
    public void The_first_tab_goes_to_the_first_field() =>
        FirstFocusWindow.GoesToFirstField(FirstFocusMove.Next).Should().BeTrue();

    [Theory]
    [InlineData(FirstFocusMove.None)]
    [InlineData(FirstFocusMove.Previous)]
    [InlineData(FirstFocusMove.Directional)]
    public void A_click_shift_tab_arrow_or_field_error_request_keeps_its_own_target(FirstFocusMove move) =>
        FirstFocusWindow.GoesToFirstField(move).Should().BeFalse();
}
