using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// Owns the one Grid cell under the mouse or pen (design §4.3 picker affordance). The rows area reports every pointer
/// move, handled or not, and the tracker moves the hover from the old cell to the new one, so at most one cell is
/// hovered at any time and a missed per-cell exit can never leave a glyph behind. The page clears it on scroll,
/// wheel, hide, and when the pointer leaves the rows area; touch never hovers.
/// </summary>
internal sealed class GridHoverTracker
{
    private readonly UIElement _rowsArea;
    private GridCell? _hovered;

    public GridHoverTracker(UIElement rowsArea)
    {
        _rowsArea = rowsArea;
        var update = new PointerEventHandler(OnPointerMoved);
        rowsArea.AddHandler(UIElement.PointerEnteredEvent, update, handledEventsToo: true);
        rowsArea.AddHandler(UIElement.PointerMovedEvent, update, handledEventsToo: true);
        rowsArea.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnPointerExited), handledEventsToo: true);
        var clear = new PointerEventHandler((_, _) => Clear());
        rowsArea.AddHandler(UIElement.PointerCanceledEvent, clear, handledEventsToo: true);
        rowsArea.AddHandler(UIElement.PointerCaptureLostEvent, clear, handledEventsToo: true);
        rowsArea.AddHandler(UIElement.PointerWheelChangedEvent, clear, handledEventsToo: true);
    }

    public void Clear() => SetHovered(null);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) =>
        SetHovered(e.Pointer.PointerDeviceType == PointerDeviceType.Touch ? null : CellAncestor(e.OriginalSource as DependencyObject));

    /// <summary>
    /// Exits bubble from every child (text, glyph square, cell), so an exit alone says nothing about where the pointer
    /// is now: the cell actually under it is hit-tested instead. Touch lifts and a pen leaving range clear the hover.
    /// </summary>
    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch || !e.Pointer.IsInRange)
        {
            Clear();
            return;
        }

        GridCell? under = null;
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(e.GetCurrentPoint(null).Position, _rowsArea))
        {
            if (element is GridCell cell)
            {
                under = cell;
                break;
            }
        }

        SetHovered(under);
    }

    private void SetHovered(GridCell? cell)
    {
        // A recycled cell drops its own hover, so the same cell is marked again when the tracker still points at it.
        if (ReferenceEquals(cell, _hovered) && cell?.IsPointerOverCell != false)
        {
            return;
        }

        if (!ReferenceEquals(cell, _hovered))
        {
            _hovered?.SetPointerOver(false);
        }

        _hovered = cell;
        cell?.SetPointerOver(true);
    }

    private GridCell? CellAncestor(DependencyObject? element)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, _rowsArea); current = VisualTreeHelper.GetParent(current))
        {
            if (current is GridCell cell)
            {
                return cell;
            }
        }

        return null;
    }
}
