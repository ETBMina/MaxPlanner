using AwesomeAssertions;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace MaxPlanner.Presentation.Tests.Shell;

[Trait("Req", "T-08")]
[Trait("Req", "AC-23")]
public sealed class ThemeServiceTests
{
    [Theory]
    [InlineData(ThemePreference.System, ElementTheme.Default)]
    [InlineData(ThemePreference.Light, ElementTheme.Light)]
    [InlineData(ThemePreference.Dark, ElementTheme.Dark)]
    public void System_follows_windows_and_manual_modes_force_the_theme(ThemePreference mode, ElementTheme expected)
    {
        ThemeService.ToElementTheme(mode).Should().Be(expected);
    }

    // Phase 2 screen review: app Light with Windows Dark left nearly invisible caption buttons, because the
    // caption theme was read from the root before it loaded. The setting now maps directly.
    [Theory]
    [InlineData(ThemePreference.System, TitleBarTheme.UseDefaultAppMode)]
    [InlineData(ThemePreference.Light, TitleBarTheme.Light)]
    [InlineData(ThemePreference.Dark, TitleBarTheme.Dark)]
    public void Caption_buttons_follow_the_selected_mode_and_system_follows_the_windows_app_mode(ThemePreference mode, TitleBarTheme expected)
    {
        ThemeService.ToTitleBarTheme(mode).Should().Be(expected);
    }
}
