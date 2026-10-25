using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Calendar;

namespace MaxPlanner.Presentation.Calendar;

/// <summary>One visible workday cell of a week row (B-08.1–2, design §4.4).</summary>
/// <param name="Column">0 = Sunday … 4 = Thursday.</param>
/// <param name="IsInPeriod">False for adjacent-month days in Month mode (styled as such, B-08.2).</param>
/// <param name="Items">Every shown CR whose range covers the day, in lane order: the "+N more" flyout lists them all (B-08.9).</param>
/// <param name="HiddenCount">The N of "+N more": CRs on this day that did not fit in the lanes.</param>
public sealed record CalendarDayCell(DateOnly Date, int Column, bool IsInPeriod, bool IsToday, IReadOnlyList<ChangeRequestReadModel> Items, int HiddenCount);

/// <summary>One bar piece in one week row (B-08.6).</summary>
/// <param name="SegmentStart">The first visible day of this piece; part of the bar's AutomationId (design §8.1).</param>
/// <param name="ContinuesBefore">The CR continues onto an earlier visible day outside this piece (continuation cue).</param>
/// <param name="ContinuesAfter">The CR continues onto a later visible day outside this piece.</param>
public sealed record CalendarBar(
    ChangeRequestReadModel Cr,
    int Lane,
    int StartColumn,
    int EndColumn,
    DateOnly SegmentStart,
    bool ContinuesBefore,
    bool ContinuesAfter)
{
    /// <summary>The start edge handle sits only on the piece that shows the real start side (B-08.8).</summary>
    public bool ShowStartHandle => !ContinuesBefore;

    public bool ShowEndHandle => !ContinuesAfter;
}

/// <param name="OutsideWorkdays">CRs wholly on this row's hidden Friday/Saturday (B-08.7 "Outside visible workdays").</param>
public sealed record CalendarWeek(WeekRow Row, IReadOnlyList<CalendarDayCell> Days, IReadOnlyList<CalendarBar> Bars, IReadOnlyList<ChangeRequestReadModel> OutsideWorkdays);

/// <summary>The laid-out Month or Week surface. Rebuilt from committed read models; never edited in place.</summary>
public sealed record CalendarSurface(IReadOnlyList<CalendarWeek> Weeks, bool IsWeekMode, int MaxLanes)
{
    public static CalendarSurface Empty { get; } = new([], false, CalendarLayoutPolicy.DefaultMaxLanes);
}

/// <summary>
/// Builds the Calendar surface from CR read models with <see cref="CalendarLayoutPolicy"/> (B-08.6–9). Pure, so it
/// can be tested without XAML; window size only changes the lane cap, never schedule data (B-08.10).
/// </summary>
public static class CalendarSurfaceBuilder
{
    /// <param name="changeRequests">The CRs that pass the Calendar search; unscheduled ones are ignored here.</param>
    /// <param name="periodMonth">The displayed month in Month mode (its first day); null in Week mode, where every day is in period.</param>
    public static CalendarSurface Build(
        IReadOnlyList<ChangeRequestReadModel> changeRequests,
        IReadOnlyList<WeekRow> rows,
        DateOnly? periodMonth,
        DateOnly today,
        int maxLanes = CalendarLayoutPolicy.DefaultMaxLanes)
    {
        ArgumentNullException.ThrowIfNull(changeRequests);
        ArgumentNullException.ThrowIfNull(rows);
        var scheduled = changeRequests
            .Where(cr => cr.PlannedStart is not null && cr.PlannedEnd is not null)
            .ToDictionary(cr => cr.Id.Value);
        var items = scheduled.Values.Select(cr => new CalendarItem(cr.Id.Value, cr.PlannedStart!.Value, cr.PlannedEnd!.Value)).ToList();
        var layout = CalendarLayoutPolicy.Layout(items, rows, maxLanes);
        var hidden = layout.Overflows.ToDictionary(o => o.Day, o => o.HiddenCount);

        var weeks = new List<CalendarWeek>(rows.Count);
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var visibleDays = row.VisibleDays;
            var bars = layout.Segments
                .Where(s => s.RowIndex == rowIndex)
                .OrderBy(s => s.StartColumn)
                .ThenBy(s => s.Lane)
                .Select(s => new CalendarBar(scheduled[s.ItemId], s.Lane, s.StartColumn, s.EndColumn, visibleDays[s.StartColumn], s.ContinuesBefore, s.ContinuesAfter))
                .ToList();

            var days = new List<CalendarDayCell>(visibleDays.Count);
            for (var column = 0; column < visibleDays.Count; column++)
            {
                var date = visibleDays[column];
                var onDay = bars.Where(b => b.StartColumn <= column && b.EndColumn >= column).OrderBy(b => b.Lane).Select(b => b.Cr)
                    .Concat(scheduled.Values.Where(cr => Covers(cr, date) && !bars.Any(b => b.Cr.Id == cr.Id)).OrderBy(cr => cr.Number, StringComparer.CurrentCultureIgnoreCase))
                    .ToList();
                var inPeriod = periodMonth is not { } month || (date.Year == month.Year && date.Month == month.Month);
                days.Add(new CalendarDayCell(date, column, inPeriod, date == today, onDay, hidden.GetValueOrDefault(date)));
            }

            var friday = row.WeekStart.AddDays(5);
            var outside = layout.OutsideVisibleWorkdays
                .Select(id => scheduled[id])
                .Where(cr => cr.PlannedStart <= row.WeekEnd && cr.PlannedEnd >= friday)
                .OrderBy(cr => cr.PlannedStart)
                .ThenBy(cr => cr.Number, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            weeks.Add(new CalendarWeek(row, days, bars, outside));
        }

        return new CalendarSurface(weeks, periodMonth is null, maxLanes);
    }

    /// <summary>B-08.6: planned ranges are inclusive at both ends.</summary>
    public static bool Covers(ChangeRequestReadModel cr, DateOnly day) =>
        cr.PlannedStart is { } start && cr.PlannedEnd is { } end && start <= day && day <= end;
}
