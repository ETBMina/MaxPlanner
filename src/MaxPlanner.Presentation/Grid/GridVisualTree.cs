using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MaxPlanner.Presentation.Grid;

internal static class GridVisualTree
{
    public static IGridCellHost? FindHost(DependencyObject start)
    {
        for (var current = VisualTreeHelper.GetParent(start); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is IGridCellHost host)
            {
                return host;
            }
        }

        return null;
    }
}
