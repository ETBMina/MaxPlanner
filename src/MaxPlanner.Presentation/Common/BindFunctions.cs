using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MaxPlanner.Presentation.Common;

/// <summary>Static functions for <c>x:Bind</c>, used instead of value converters.</summary>
public static class BindFunctions
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Segoe Fluent Icons Warning (E7BA) or StatusErrorFull (EA39).</summary>
    public static string SeverityGlyph(bool isWarning) => isWarning ? "" : "";

    // Theme brushes looked up per call so the current theme's instance is used; the glyph repeats what the
    // adjacent text says, so its color never carries meaning on its own (P3).
    public static Brush SeverityBrush(bool isWarning) =>
        (Brush)Microsoft.UI.Xaml.Application.Current.Resources[isWarning ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush"];
}
