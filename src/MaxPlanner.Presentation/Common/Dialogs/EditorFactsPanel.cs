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

    // Each child's cell and each row's height from the last measure; arrange places by them.
    private readonly List<double> _rowHeights = [];
    private (int Row, int Column)?[] _cells = [];

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
        _cells = EditorFactsLayout.Cells(Children.Select(c => c.Visibility != Visibility.Collapsed).ToArray(), columns);
        _rowHeights.Clear();
        double widest = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (_cells[i] is not { } cell)
            {
                child.Measure(default);
                continue;
            }

            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            if (cell.Row == _rowHeights.Count)
            {
                _rowHeights.Add(0);
            }

            _rowHeights[cell.Row] = Math.Max(_rowHeights[cell.Row], child.DesiredSize.Height);
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        var height = _rowHeights.Count == 0 ? 0 : _rowHeights.Sum() + (RowSpacing * (_rowHeights.Count - 1));
        var width = infinite ? (widest * columns) + (ColumnSpacing * (columns - 1)) : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, Columns);
        var cellWidth = CellWidth(finalSize.Width, columns);
        var tops = new double[_rowHeights.Count];
        for (var r = 1; r < tops.Length; r++)
        {
            tops[r] = tops[r - 1] + _rowHeights[r - 1] + RowSpacing;
        }

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (i >= _cells.Length || _cells[i] is not { } cell || cell.Row >= tops.Length)
            {
                child.Arrange(default);
                continue;
            }

            child.Arrange(new Rect(cell.Column * (cellWidth + ColumnSpacing), tops[cell.Row], cellWidth, _rowHeights[cell.Row]));
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

    /// <summary>
    /// The cell of each fact in reading order. A hidden fact gets none and takes no cell, so the shown facts after it
    /// close up and leave no gap.
    /// </summary>
    /// <param name="isShown">For each fact in order, whether it is shown.</param>
    /// <param name="columns">The number of columns; below one counts as one.</param>
    public static (int Row, int Column)?[] Cells(IReadOnlyList<bool> isShown, int columns)
    {
        ArgumentNullException.ThrowIfNull(isShown);
        var cells = new (int Row, int Column)?[isShown.Count];
        var index = 0;
        for (var i = 0; i < isShown.Count; i++)
        {
            if (isShown[i])
            {
                cells[i] = (Row(index, columns), Column(index, columns));
                index++;
            }
        }

        return cells;
    }

    /// <summary>The row of the shown fact at <paramref name="index"/> (reading order, collapsed facts not counted).</summary>
    public static int Row(int index, int columns) => index / Math.Max(1, columns);

    /// <summary>The column of the shown fact at <paramref name="index"/>.</summary>
    public static int Column(int index, int columns) => index % Math.Max(1, columns);
}
