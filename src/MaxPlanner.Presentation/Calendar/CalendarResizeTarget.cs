using MaxPlanner.Domain.Calendar;

namespace MaxPlanner.Presentation.Calendar;

/// <summary>
/// The pure geometry of a bar edge drag on the Month/Week surface (B-08.8, design §4.4): which visible workday the
/// pointer is over, in any week row, and the range the drag proposes. Kept free of XAML so it can be tested.
/// </summary>
public static class CalendarResizeTarget
{
    /// <summary>
    /// The week row and day column under a pointer at (<paramref name="x"/>, <paramref name="y"/>) in surface
    /// coordinates. A pointer above the first row or below the last one takes the nearest row; left of the grid takes
    /// Sunday, and the Fri–Sat stub or anything to its right takes the row's Thursday (the nearest visible workday
    /// before the hidden days), so a drop never lands on a hidden day.
    /// </summary>
    /// <param name="rowTops">The top edge of each week row, in ascending order; a row ends where the next begins.</param>
    /// <param name="surfaceWidth">The width of the week rows, the Fri–Sat stub included.</param>
    /// <param name="stubWidth">The width of the Fri–Sat stub at the end of each row.</param>
    public static (int Week, int Column) CellAt(double x, double y, IReadOnlyList<double> rowTops, double surfaceWidth, double stubWidth)
    {
        ArgumentNullException.ThrowIfNull(rowTops);
        if (rowTops.Count == 0)
        {
            throw new ArgumentException("The surface has no week rows.", nameof(rowTops));
        }

        var week = 0;
        for (var i = 1; i < rowTops.Count && y >= rowTops[i]; i++)
        {
            week = i;
        }

        var columns = Workweek.VisibleDays.Count;
        var columnWidth = Math.Max(1, (surfaceWidth - stubWidth) / columns);
        var column = (int)Math.Clamp(Math.Floor(x / columnWidth), 0, columns - 1);
        return (week, column);
    }

    /// <summary>
    /// The first and last column of <paramref name="rowDays"/> (a week row's visible days, in column order) that
    /// <paramref name="start"/>–<paramref name="end"/> covers, or null when the range covers none of them.
    /// </summary>
    public static (int First, int Last)? CoveredColumns(IReadOnlyList<DateOnly> rowDays, DateOnly start, DateOnly end)
    {
        ArgumentNullException.ThrowIfNull(rowDays);
        var first = -1;
        var last = -1;
        for (var column = 0; column < rowDays.Count; column++)
        {
            if (rowDays[column] >= start && rowDays[column] <= end)
            {
                first = first < 0 ? column : first;
                last = column;
            }
        }

        return first < 0 ? null : (first, last);
    }

    /// <summary>
    /// The lane for a drag preview piece spanning <paramref name="first"/>–<paramref name="last"/> in a week row: the
    /// lowest of the row's <paramref name="laneCount"/> lanes that no other bar uses over those columns, so the
    /// preview hides no bar; when every lane is taken, <paramref name="preferred"/> (the dragged bar's lane), clamped
    /// to the row, where the preview overlays another bar (<c>Overlays</c>).
    /// </summary>
    /// <param name="occupied">The other bars of the row: lane and column span.</param>
    public static (int Lane, bool Overlays) PreviewLane(IEnumerable<(int Lane, int StartColumn, int EndColumn)> occupied, int first, int last, int laneCount, int preferred)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        var taken = occupied.Where(bar => bar.StartColumn <= last && bar.EndColumn >= first).Select(bar => bar.Lane).ToHashSet();
        for (var lane = 0; lane < laneCount; lane++)
        {
            if (!taken.Contains(lane))
            {
                return (lane, false);
            }
        }

        return (Math.Clamp(preferred, 0, Math.Max(0, laneCount - 1)), true);
    }

    /// <summary>
    /// The range proposed by dragging one endpoint of <paramref name="start"/>–<paramref name="end"/> to
    /// <paramref name="target"/>; the other endpoint never changes. Null when the range would be inverted: the drag
    /// is rejected and the bar snaps back (B-08.8).
    /// </summary>
    public static (DateOnly Start, DateOnly End)? Range(DateOnly start, DateOnly end, bool isStart, DateOnly target) =>
        isStart
            ? target <= end ? (target, end) : null
            : target >= start ? (start, target) : null;
}
