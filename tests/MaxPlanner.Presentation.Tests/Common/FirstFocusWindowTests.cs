using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Dialogs;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// The first-field redirect of an opening editor dialog never depends on the device a focus change reports: however the
/// dialog was opened, its own focus passes go to the first field until new user input, a field request or the settled open.
/// </summary>
public sealed class FirstFocusWindowTests
{
    [Fact]
    public void A_click_opened_dialog_redirects_its_own_focus_and_ignores_the_release_of_the_opening_click()
    {
        var window = new FirstFocusWindow();
        window.ShouldRedirect(isFirstField: false).Should().BeTrue();
        window.OnInput(FirstFocusInput.PointerReleased, false).Should().BeFalse();
        window.ShouldRedirect(isFirstField: false).Should().BeTrue();
    }

    [Fact]
    public void A_touch_opened_dialog_keeps_redirecting_after_the_lifted_finger_until_the_open_settles()
    {
        var window = new FirstFocusWindow();
        window.OnInput(FirstFocusInput.PointerReleased, false).Should().BeFalse();
        window.ShouldRedirect(isFirstField: false).Should().BeTrue();
        window.Close();
        window.ShouldRedirect(isFirstField: false).Should().BeFalse();
    }

    [Fact]
    public void A_keyboard_opened_dialog_ignores_the_opening_keys_release_and_its_auto_repeat()
    {
        var window = new FirstFocusWindow();
        window.OnInput(FirstFocusInput.KeyUp, false).Should().BeFalse();
        window.OnInput(FirstFocusInput.KeyDown, isKeyRepeat: true).Should().BeFalse();
        window.ShouldRedirect(isFirstField: false).Should().BeTrue();
    }

    [Theory]
    [InlineData(FirstFocusInput.PointerPressed, false)]
    [InlineData(FirstFocusInput.KeyDown, false)]
    public void New_user_input_ends_the_redirect(FirstFocusInput input, bool isKeyRepeat)
    {
        var window = new FirstFocusWindow();
        window.OnInput(input, isKeyRepeat).Should().BeTrue();
        window.ShouldRedirect(isFirstField: false).Should().BeFalse();
    }

    [Fact]
    public void Focus_already_on_the_first_field_is_left_alone() =>
        new FirstFocusWindow().ShouldRedirect(isFirstField: true).Should().BeFalse();

    [Theory]
    [InlineData(FirstFocusInput.PointerPressed, false, true)]
    [InlineData(FirstFocusInput.PointerReleased, false, false)]
    [InlineData(FirstFocusInput.KeyDown, false, true)]
    [InlineData(FirstFocusInput.KeyDown, true, false)]
    [InlineData(FirstFocusInput.KeyUp, false, false)]
    public void Only_a_press_or_a_fresh_key_down_is_new_input(FirstFocusInput input, bool isKeyRepeat, bool ends) =>
        FirstFocusWindow.EndsWindow(input, isKeyRepeat).Should().Be(ends);
}
