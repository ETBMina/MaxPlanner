using MaxPlanner.Application.Preferences;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace MaxPlanner.Presentation.Common.Services;

/// <summary>
/// Applies the System/Light/Dark appearance mode to the whole window (T-08, AC-23, design §3 rule 1).
/// </summary>
/// <remarks>
/// The mode is set on the window's root element, so every themed resource below it updates in place.
/// System maps to <see cref="ElementTheme.Default"/>, which follows Windows while the app runs. The
/// caption buttons are drawn by Windows, so the same choice is mapped directly to
/// <see cref="AppWindowTitleBar.PreferredTheme"/>: reading the root's resolved theme instead was wrong
/// before the root loaded and left dark caption glyphs on a light app (Phase 2 screen review). High
/// Contrast stays system-controlled. Use it on the UI thread only.
/// </remarks>
public sealed class ThemeService
{
    private readonly IUiPreferencesStore _preferences;
    private FrameworkElement? _root;
    private AppWindowTitleBar? _titleBar;

    public ThemeService(IUiPreferencesStore preferences) =>
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));

    public ThemePreference Mode { get; private set; } = ThemePreference.System;

    /// <summary>The element theme for dialogs and popups, which are outside the root's subtree (design §3 rule 1).</summary>
    public ElementTheme ElementTheme => ToElementTheme(Mode);

    public static ElementTheme ToElementTheme(ThemePreference mode) => mode switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>
    /// The caption-button theme for an appearance mode. System uses the Windows app mode, so the buttons
    /// follow a Windows theme change without the app having to observe it.
    /// </summary>
    public static TitleBarTheme ToTitleBarTheme(ThemePreference mode) => mode switch
    {
        ThemePreference.Light => TitleBarTheme.Light,
        ThemePreference.Dark => TitleBarTheme.Dark,
        _ => TitleBarTheme.UseDefaultAppMode,
    };

    public void Attach(FrameworkElement root, AppWindowTitleBar titleBar)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(titleBar);
        _root = root;
        _titleBar = titleBar;
        Apply(Mode);
    }

    /// <summary>Applies <paramref name="mode"/> without saving it (startup applies the saved mode).</summary>
    public void Apply(ThemePreference mode)
    {
        Mode = mode;
        if (_root is not null)
        {
            _root.RequestedTheme = ToElementTheme(mode);
        }

        if (_titleBar is not null)
        {
            _titleBar.PreferredTheme = ToTitleBarTheme(mode);
        }
    }

    /// <summary>Applies and persists a user's choice (the Settings page, Phase 10).</summary>
    public void Select(ThemePreference mode)
    {
        Apply(mode);
        _preferences.Update(p => p with { Theme = mode });
    }
}
