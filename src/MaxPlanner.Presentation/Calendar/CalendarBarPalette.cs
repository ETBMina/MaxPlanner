using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MaxPlanner.Presentation.Calendar;

/// <summary>The visual state of a CR bar (design §6.3).</summary>
internal enum CalendarBarState
{
    Scheduled,
    Overdue,
    Completed,
}

/// <summary>The brushes one bar applies: its fill at rest, on hover and when pressed, and its stroke.</summary>
internal readonly record struct CalendarBarColors(Brush Fill, Brush Hover, Brush Pressed, Brush Stroke);

/// <summary>
/// Design §6.3: the categorical bar palette. Each CR keeps one palette entry (fill + stripe) chosen from its Id,
/// so its bars match on every week row and in Month and Week. An instance holds the brushes of one Light or Dark
/// theme dictionary; High Contrast has no instance because bars then use the system brushes of their styles.
/// </summary>
/// <remarks>
/// The brushes are read from <c>Themes/AppResources.xaml</c> once per theme and set as local values, so they do not
/// follow a theme change by themselves; <see cref="CalendarMonthView"/> resolves a new palette and re-renders.
/// </remarks>
internal sealed class CalendarBarPalette
{
    /// <summary>The number of palette entries; <c>CalendarBarFill{i}</c>/<c>CalendarBarStripe{i}</c> exist for each.</summary>
    public const int Count = 10;

    public const string HighContrastKey = "HighContrast";

    private const double HoverBlend = 0.2;
    private const double PressedBlend = 0.32;
    private const double CompletedOpacity = 0.5;

    private readonly ResourceDictionary _theme;
    private readonly Dictionary<(int Index, CalendarBarState State), CalendarBarColors> _colors = [];

    private CalendarBarPalette(ResourceDictionary theme) => _theme = theme;

    /// <summary>The theme dictionary key for <paramref name="theme"/>; High Contrast wins over Light/Dark (design §3).</summary>
    public static string ThemeKey(ElementTheme theme, bool isHighContrast) =>
        isHighContrast ? HighContrastKey : theme == ElementTheme.Dark ? "Dark" : "Light";

    /// <summary>The palette of <paramref name="themeKey"/>, or <see langword="null"/> in High Contrast or when the resources are missing.</summary>
    public static CalendarBarPalette? Resolve(string themeKey) =>
        themeKey != HighContrastKey && FindTheme(Microsoft.UI.Xaml.Application.Current.Resources, themeKey) is { } theme
            ? new CalendarBarPalette(theme)
            : null;

    /// <summary>
    /// The palette entry of a CR. A fixed FNV-1a hash over the Guid bytes keeps the color stable across runs;
    /// <see cref="string.GetHashCode()"/> is randomized per process and would recolor every CR on each start.
    /// </summary>
    public static int IndexOf(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var hash = 2166136261u;
        foreach (var value in bytes)
        {
            hash = (hash ^ value) * 16777619u;
        }

        return (int)(hash % Count);
    }

    public CalendarBarColors ColorsFor(Guid id, CalendarBarState state)
    {
        var key = (IndexOf(id), state);
        if (_colors.TryGetValue(key, out var colors))
        {
            return colors;
        }

        var fill = ((SolidColorBrush)_theme["CalendarBarFill" + key.Item1]).Color;
        var stripe = ((SolidColorBrush)_theme["CalendarBarStripe" + key.Item1]).Color;

        // Completed bars keep their CR's hue but fade it; the check glyph and secondary text carry the state (P3).
        var opacity = state == CalendarBarState.Completed ? CompletedOpacity : 1;
        Brush stroke = state == CalendarBarState.Overdue
            ? (Brush)_theme["CalendarOverdueBarStrokeBrush"]
            : new SolidColorBrush(stripe) { Opacity = opacity };
        colors = new CalendarBarColors(
            new SolidColorBrush(fill) { Opacity = opacity },
            new SolidColorBrush(Blend(fill, stripe, HoverBlend)) { Opacity = opacity },
            new SolidColorBrush(Blend(fill, stripe, PressedBlend)) { Opacity = opacity },
            stroke);
        _colors[key] = colors;
        return colors;
    }

    private static Color Blend(Color from, Color to, double amount) =>
        ColorHelper.FromArgb(255, Mix(from.R, to.R, amount), Mix(from.G, to.G, amount), Mix(from.B, to.B, amount));

    private static byte Mix(byte from, byte to, double amount) => (byte)Math.Round(from + ((to - from) * amount));

    // App.xaml merges AppResources.xaml, so its theme dictionaries sit in a merged dictionary, not the root.
    private static ResourceDictionary? FindTheme(ResourceDictionary dictionary, string themeKey)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var value) && value is ResourceDictionary theme && theme.ContainsKey("CalendarBarFill0"))
        {
            return theme;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (FindTheme(merged, themeKey) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
