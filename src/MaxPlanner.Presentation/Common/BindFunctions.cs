using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MaxPlanner.Presentation.Common;

/// <summary>Static functions for <c>x:Bind</c>, used instead of value converters.</summary>
public static class BindFunctions
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleAll(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Visible when any of three conditions holds, e.g. a header row that is empty unless something is open.</summary>
    public static Visibility VisibleAny(bool first, bool second, bool third) => first || second || third ? Visibility.Visible : Visibility.Collapsed;

    public static bool All(bool first, bool second) => first && second;

    public static bool Not(bool value) => !value;

    /// <summary>True for a non-empty message, e.g. to open an InfoBar while its text is set.</summary>
    public static bool HasText(string? value) => !string.IsNullOrEmpty(value);

    /// <summary>A repeated item's AutomationId: <c>{prefix}{Guid:N}</c> (design §8.1).</summary>
    public static string RecordId(string prefix, Guid id) => prefix + id.ToString("N");

    /// <summary>Segoe Fluent Icons Warning (E7BA) or StatusErrorFull (EA39).</summary>
    public static string SeverityGlyph(bool isWarning) => isWarning ? "" : "";

    // A style, not a brush: its ThemeResource setter follows the window's theme and High Contrast (design §3). The
    // glyph repeats what the adjacent text says, so its color never carries meaning on its own (P3).
    public static Style SeverityGlyphStyle(bool isWarning) =>
        (Style)Microsoft.UI.Xaml.Application.Current.Resources[isWarning ? "WarningGlyphStyle" : "ErrorGlyphStyle"];
}
