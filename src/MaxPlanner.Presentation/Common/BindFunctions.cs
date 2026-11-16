using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MaxPlanner.Presentation.Common;

/// <summary>Static functions for <c>x:Bind</c>, used instead of value converters.</summary>
public static class BindFunctions
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleAll(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    public static bool All(bool first, bool second) => first && second;

    public static bool Not(bool value) => !value;

    /// <summary>True for a non-empty message, e.g. to open an InfoBar while its text is set.</summary>
    public static bool HasText(string? value) => !string.IsNullOrEmpty(value);

    /// <summary>A tooltip only when there is text: an empty string would still open an empty tooltip.</summary>
    public static object? ToolTipOrNull(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>The full text as a tooltip only while the TextBlock trims it (bind to its <c>IsTextTrimmed</c>).</summary>
    public static object? ToolTipWhenTrimmed(bool isTrimmed, string? text) => isTrimmed ? ToolTipOrNull(text) : null;

    /// <summary>A repeated item's AutomationId: <c>{prefix}{Guid:N}</c> (design §8.1).</summary>
    public static string RecordId(string prefix, Guid id) => prefix + id.ToString("N");

    /// <summary>The priority chip style for a rank token (design §6.1); styles use theme resources, so they follow theme changes.</summary>
    public static Style PriorityChipStyle(PriorityLevel level) =>
        (Style)Microsoft.UI.Xaml.Application.Current.Resources[level switch
        {
            PriorityLevel.Level1 => "PriorityLevel1ChipStyle",
            PriorityLevel.Level2 => "PriorityLevel2ChipStyle",
            PriorityLevel.Lowest => "PriorityLowestChipStyle",
            _ => "PriorityMidChipStyle",
        }];

    /// <summary>Important, ChevronUp, Remove (dash), ChevronDown (design §6.1).</summary>
    public static string PriorityGlyph(PriorityLevel level) => WorkflowGlyphs.Priority(level);

    /// <summary>The AutomationId of a conflict row's Use mine box (design §8.1: stable keys, never display text).</summary>
    public static string UseMineId(string key) => "Conflict_UseMineCheck_" + key;

    /// <summary>Segoe Fluent Icons Warning (E7BA) or StatusErrorFull (EA39).</summary>
    public static string SeverityGlyph(bool isWarning) => isWarning ? "" : "";

    // A style, not a brush: its ThemeResource setter follows the window's theme and High Contrast (design §3). The
    // glyph repeats what the adjacent text says, so its color never carries meaning on its own (P3).
    public static Style SeverityGlyphStyle(bool isWarning) =>
        (Style)Microsoft.UI.Xaml.Application.Current.Resources[isWarning ? "WarningGlyphStyle" : "ErrorGlyphStyle"];
}
