using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// Lays out one row's cells (or the header cells) side by side with the widths of the displayed columns, so the
/// header and every realized row share one layout. The frozen first column is translated by the horizontal
/// scroll offset and drawn above the others, which keeps it in view at S without a second scroll region.
/// </summary>
internal static class GridColumnLayout
{
    public const double RowHeight = 44;

    public static Size Measure(UIElementCollection children, IReadOnlyList<GridColumnViewModel> columns, double height)
    {
        var width = 0d;
        for (var i = 0; i < children.Count; i++)
        {
            var columnWidth = i < columns.Count ? columns[i].Width : 0;
            children[i].Measure(new Size(columnWidth, height));
            width += columnWidth;
        }

        return new Size(width, height);
    }

    public static void Arrange(UIElementCollection children, IReadOnlyList<GridColumnViewModel> columns, double height, double frozenOffset, bool isFrozen)
    {
        var x = 0d;
        for (var i = 0; i < children.Count; i++)
        {
            var columnWidth = i < columns.Count ? columns[i].Width : 0;
            children[i].Arrange(new Rect(x, 0, columnWidth, height));
            if (i == 0)
            {
                children[i].RenderTransform = isFrozen && frozenOffset > 0 ? new TranslateTransform { X = frozenOffset } : null;
                Canvas.SetZIndex(children[i], isFrozen ? 1 : 0);
            }

            x += columnWidth;
        }
    }
}
