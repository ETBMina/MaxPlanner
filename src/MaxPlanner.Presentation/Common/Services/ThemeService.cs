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
/// caption buttons are drawn by Windows, so they follow the root's actual theme through
/// <see cref="AppWindowTitleBar.PreferredTheme"/>. High Contrast stays system-controlled.
/// Use it on the UI thread only.
/// </remarks>
public sealed class ThemeService
{
    private readonly IUiPreferencesStore _preferences;
    private FrameworkElement? _root;
    private AppWindowTitleBar? _titleBar;

    public ThemeService(IUiPreferencesStore preferences) =>
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));

    public ThemePreference Mode { get; private set; } = ThemePreference.System;

    public static ElementTheme ToElementTheme(ThemePreference mode) => mode switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <param name="actualTheme">The root's resolved theme, never <see cref="ElementTheme.Default"/> once loaded.</param>
    public static TitleBarTheme ToTitleBarTheme(ElementTheme actualTheme) =>
        actualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;

    public void Attach(FrameworkElement root, AppWindowTitleBar titleBar)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(titleBar);
        if (_root is not null)
        {
            _root.ActualThemeChanged -= OnActualThemeChanged;
        }

        _root = root;
        _titleBar = titleBar;
        _root.ActualThemeChanged += OnActualThemeChanged;
        Apply(Mode);
    }

    /// <summary>Applies <paramref name="mode"/> without saving it (startup applies the saved mode).</summary>
    public void Apply(ThemePreference mode)
    {
        Mode = mode;
        if (_root is null)
        {
            return;
        }

        _root.RequestedTheme = ToElementTheme(mode);
        SyncCaptionButtons();
    }

    /// <summary>Applies and persists a user's choice (the Settings page, Phase 9).</summary>
    public void Select(ThemePreference mode)
    {
        Apply(mode);
        _preferences.Update(p => p with { Theme = mode });
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => SyncCaptionButtons();

    private void SyncCaptionButtons()
    {
        if (_root is not null && _titleBar is not null)
        {
            _titleBar.PreferredTheme = ToTitleBarTheme(_root.ActualTheme);
        }
    }
}
