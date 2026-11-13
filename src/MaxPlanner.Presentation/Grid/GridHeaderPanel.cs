using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MaxPlanner.Presentation.Grid;

/// <summary>The header cells, laid out with the same widths and frozen column as the rows.</summary>
public sealed partial class GridHeaderPanel : Panel
{
    private IReadOnlyList<GridColumnViewModel> _columns = [];
    private double _frozenOffset;
    private bool _isFrozen;

    internal void Update(IReadOnlyList<GridColumnViewModel> columns, double frozenOffset, bool isFrozen)
    {
        _columns = columns;
        _frozenOffset = frozenOffset;
        _isFrozen = isFrozen;
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize) => GridColumnLayout.Measure(Children, _columns, 40);

    protected override Size ArrangeOverride(Size finalSize)
    {
        GridColumnLayout.Arrange(Children, _columns, 40, _frozenOffset, _isFrozen);
        return new Size(finalSize.Width, 40);
    }
}
