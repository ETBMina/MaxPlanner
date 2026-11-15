using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// The facts of a record editor's info box (Task, CR, Subtask card): each child is one fact (a label over its value),
/// placed in reading order across <see cref="Columns"/> equal columns. Collapsed facts take no cell, so a fact that does
/// not apply (Created on a new record) leaves no gap. The editor sets <see cref="Columns"/> from the window width
/// (<see cref="EditorFactsLayout.Columns"/>); the column count never depends on the content, so the height is stable for
/// a given width.
/// </summary>
public sealed partial class EditorFactsPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(EditorFactsPanel), new PropertyMetadata(EditorFactsLayout.WideColumns, (d, _) => ((EditorFactsPanel)d).InvalidateMeasure()));

    private const double ColumnSpacing = 16;
    private const double RowSpacing = 12;

    // Each row's height from the last measure; arrange places by them.
    private readonly List<double> _rowHeights = [];

    /// <summary>The number of columns; at least one.</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Max(1, Columns);
        var infinite = double.IsInfinity(availableSize.Width);
        var cellWidth = infinite ? double.PositiveInfinity : CellWidth(availableSize.Width, columns);
        _rowHeights.Clear();
        double widest = 0;
        var index = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(default);
                continue;
            }

            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            var row = EditorFactsLayout.Row(index, columns);
            if (row == _rowHeights.Count)
            {
                _rowHeights.Add(0);
            }

            _rowHeights[row] = Math.Max(_rowHeights[row], child.DesiredSize.Height);
            widest = Math.Max(widest, child.DesiredSize.Width);
            index++;
        }

        var height = _rowHeights.Count == 0 ? 0 : _rowHeights.Sum() + (RowSpacing * (_rowHeights.Count - 1));
        var width = infinite ? (widest * columns) + (ColumnSpacing * (columns - 1)) : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, Columns);
        var cellWidth = CellWidth(finalSize.Width, columns);
        var index = 0;
        double top = 0;
        var currentRow = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(default);
                continue;
            }

            var row = EditorFactsLayout.Row(index, columns);
            if (row >= _rowHeights.Count)
            {
                child.Arrange(default);
                continue;
            }

            for (; currentRow < row; currentRow++)
            {
                top += _rowHeights[currentRow] + RowSpacing;
            }

            var left = EditorFactsLayout.Column(index, columns) * (cellWidth + ColumnSpacing);
            child.Arrange(new Rect(left, top, cellWidth, _rowHeights[row]));
            index++;
        }

        return finalSize;
    }

    private static double CellWidth(double width, int columns) => Math.Max(0, (width - (ColumnSpacing * (columns - 1))) / columns);
}

/// <summary>The layout decisions of <see cref="EditorFactsPanel"/>, kept free of WinUI so tests can check them.</summary>
public static class EditorFactsLayout
{
    /// <summary>The columns at L.</summary>
    public const int WideColumns = 4;

    /// <summary>The columns below L.</summary>
    public const int NarrowColumns = 2;

    /// <summary>Design §5.1: four columns at L (window width), two below.</summary>
    public static int Columns(double windowWidth) => windowWidth >= Breakpoints.Large ? WideColumns : NarrowColumns;

    /// <summary>The row of the shown fact at <paramref name="index"/> (reading order, collapsed facts not counted).</summary>
    public static int Row(int index, int columns) => index / Math.Max(1, columns);

    /// <summary>The column of the shown fact at <paramref name="index"/>.</summary>
    public static int Column(int index, int columns) => index % Math.Max(1, columns);
}
