namespace MaxPlanner.Domain.Calendar;

/// <summary>A scheduled CR as the calendar sees it: an inclusive planned range.</summary>
public sealed record CalendarItem(Guid Id, DateOnly Start, DateOnly End)
{
    /// <summary>Full stored duration in days; the lane sort key, independent of what is visible.</summary>
    public int DurationDays => End.DayNumber - Start.DayNumber + 1;
}

/// <summary>
/// One bar piece inside one row. Columns index the row's visible days (0 = Sunday … 4 = Thursday),
/// both inclusive.
/// </summary>
/// <remarks>
/// B-08.6: the continuation cues mean the CR continues onto another visible day outside this piece,
/// possibly beyond the displayed grid. A range that only spills onto hidden Friday/Saturday shows no cue.
/// </remarks>
public sealed record CalendarSegment(
    Guid ItemId,
    int RowIndex,
    int Lane,
    int StartColumn,
    int EndColumn,
    bool ContinuesBefore,
    bool ContinuesAfter);

/// <summary>"+N more" for one visible day: N items cover the day but did not fit in the lanes.</summary>
public sealed record DayOverflow(DateOnly Day, int HiddenCount);

public sealed record CalendarLayout(
    IReadOnlyList<CalendarSegment> Segments,
    IReadOnlyList<DayOverflow> Overflows,
    IReadOnlyList<Guid> OutsideVisibleWorkdays);

/// <summary>
/// Deterministic bar layout for Month and Week modes (B-08.6–9). Pure, so it is tested without XAML; the
/// lane cap is an input (the view derives it from the row height), never schedule data (B-08.10).
/// </summary>
public static class CalendarLayoutPolicy
{
    public const int DefaultMaxLanes = 4;

    /// <param name="maxLanes">Lanes per week row; 0 sends every bar to "+N more" (a very short window, design §9).</param>
    public static CalendarLayout Layout(IReadOnlyList<CalendarItem> items, IReadOnlyList<WeekRow> rows, int maxLanes = DefaultMaxLanes)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLanes);

        // B-08.6: the lane order is fixed by the full stored range, not the clipped piece, so a long
        // CR keeps the top lanes in every row it crosses.
        var ordered = items
            .OrderByDescending(i => i.DurationDays)
            .ThenBy(i => i.Start)
            .ThenBy(i => i.Id)
            .ToList();

        var segments = new List<CalendarSegment>();
        var overflow = new Dictionary<DateOnly, int>();

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var visibleDays = rows[rowIndex].VisibleDays;
            var lanes = new List<bool[]>(); // per lane: which visible columns are occupied

            foreach (var item in ordered)
            {
                if (Clip(item, visibleDays) is not (int startColumn, int endColumn))
                {
                    continue;
                }

                var lane = FirstFreeLane(lanes, startColumn, endColumn);
                if (lane >= maxLanes)
                {
                    for (var column = startColumn; column <= endColumn; column++)
                    {
                        overflow[visibleDays[column]] = overflow.GetValueOrDefault(visibleDays[column]) + 1;
                    }

                    continue;
                }

                if (lane == lanes.Count)
                {
                    lanes.Add(new bool[visibleDays.Count]);
                }

                Array.Fill(lanes[lane], true, startColumn, endColumn - startColumn + 1);

                segments.Add(new CalendarSegment(
                    item.Id,
                    rowIndex,
                    lane,
                    startColumn,
                    endColumn,
                    ContinuesBefore: Workweek.PreviousVisibleDay(visibleDays[startColumn]) >= item.Start,
                    ContinuesAfter: Workweek.NextVisibleDay(visibleDays[endColumn]) <= item.End));
            }
        }

        return new CalendarLayout(
            segments,
            [.. overflow.OrderBy(o => o.Key).Select(o => new DayOverflow(o.Key, o.Value))],
            OutsideVisibleWorkdays(ordered, rows));
    }

    /// <summary>The visible-column span of <paramref name="item"/> in one row, or null if it shows no visible day there.</summary>
    private static (int Start, int End)? Clip(CalendarItem item, IReadOnlyList<DateOnly> visibleDays)
    {
        int? start = null;
        int? end = null;
        for (var column = 0; column < visibleDays.Count; column++)
        {
            if (visibleDays[column] >= item.Start && visibleDays[column] <= item.End)
            {
                start ??= column;
                end = column;
            }
        }

        return start is { } s && end is { } e ? (s, e) : null;
    }

    // Greedy first fit: the lowest lane with no occupied column in the piece's span. Pieces arrive in
    // duration order, not start order, so a later piece may sit before an earlier one in the same
    // lane; checking only a lane's last end would waste lanes and report false "+N more" overflow.
    private static int FirstFreeLane(List<bool[]> lanes, int startColumn, int endColumn)
    {
        for (var lane = 0; lane < lanes.Count; lane++)
        {
            if (Array.IndexOf(lanes[lane], true, startColumn, endColumn - startColumn + 1) < 0)
            {
                return lane;
            }
        }

        return lanes.Count;
    }

    /// <summary>
    /// B-08.7: items inside the displayed span that fall entirely on hidden days (e.g. a Friday–Saturday
    /// CR) have no bar, so they are reported for the "Outside visible workdays" indicator.
    /// </summary>
    private static List<Guid> OutsideVisibleWorkdays(List<CalendarItem> items, IReadOnlyList<WeekRow> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var spanStart = rows[0].WeekStart;
        var spanEnd = rows[^1].WeekEnd;
        return [.. items
            .Where(i => i.Start <= spanEnd && i.End >= spanStart && !HasVisibleDay(i))
            .Select(i => i.Id)];
    }

    private static bool HasVisibleDay(CalendarItem item)
    {
        for (var day = item.Start; day <= item.End; day = day.AddDays(1))
        {
            if (Workweek.IsVisible(day))
            {
                return true;
            }
        }

        return false;
    }
}
