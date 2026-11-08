using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The Service chip icon (design §4.2, glyph table): an outlined rounded square holding a 2x2 grid of outlined rounded
/// squares. No Segoe Fluent glyph matches, so it is a vector drawn on a 16-unit grid with Fluent-weight strokes and
/// scaled to <see cref="FrameworkElement.Width"/>. The outlines are even-odd filled rings, so the icon paints with
/// <see cref="Control.Foreground"/> (set it from the chip text brush) in every theme, High Contrast included. Decorative.
/// </summary>
public sealed partial class ServiceIcon : UserControl
{
    private const double Stroke = 1.25;

    // Parsed per instance: a Geometry cannot be shared between elements.
    private static readonly string PathData = BuildPathData();

    public ServiceIcon()
    {
        Width = 12;
        Height = 12;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        var icon = new PathIcon { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), PathData) };
        icon.Foreground = Foreground;

        // Follows the chip brush without a runtime {Binding} (no reflection; trim- and AOT-safe).
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => icon.Foreground = Foreground);
        Content = new Viewbox { Child = new Border { Width = 16, Height = 16, Child = icon } };
    }

    private static string BuildPathData()
    {
        var path = new StringBuilder("F0 ");
        Ring(path, 0, 0, 16, 3.5);
        foreach (var x in (double[])[3.5, 8.75])
        {
            foreach (var y in (double[])[3.5, 8.75])
            {
                Ring(path, x, y, 3.75, 1.25);
            }
        }

        return path.ToString();
    }

    // An outline: the outer rounded square and the inner one inset by the stroke; even-odd fills only the band between.
    private static void Ring(StringBuilder path, double x, double y, double size, double radius)
    {
        RoundedSquare(path, x, y, size, radius);
        RoundedSquare(path, x + Stroke, y + Stroke, size - (2 * Stroke), Math.Max(0, radius - Stroke));
    }

    private static void RoundedSquare(StringBuilder path, double x, double y, double size, double r)
    {
        var c = CultureInfo.InvariantCulture;
        var right = x + size;
        var bottom = y + size;
        path.Append(c, $"M{x + r},{y} H{right - r} ");
        if (r > 0)
        {
            path.Append(c, $"A{r},{r} 0 0 1 {right},{y + r} ");
        }

        path.Append(c, $"V{bottom - r} ");
        if (r > 0)
        {
            path.Append(c, $"A{r},{r} 0 0 1 {right - r},{bottom} ");
        }

        path.Append(c, $"H{x + r} ");
        if (r > 0)
        {
            path.Append(c, $"A{r},{r} 0 0 1 {x},{bottom - r} ");
        }

        path.Append(c, $"V{y + r} ");
        if (r > 0)
        {
            path.Append(c, $"A{r},{r} 0 0 1 {x + r},{y} ");
        }

        path.Append("Z ");
    }
}
