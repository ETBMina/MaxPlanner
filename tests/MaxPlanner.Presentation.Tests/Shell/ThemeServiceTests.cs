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

    [Theory]
    [InlineData(ElementTheme.Light, TitleBarTheme.Light)]
    [InlineData(ElementTheme.Dark, TitleBarTheme.Dark)]
    public void Caption_buttons_follow_the_resolved_theme(ElementTheme actual, TitleBarTheme expected)
    {
        ThemeService.ToTitleBarTheme(actual).Should().Be(expected);
    }
}
