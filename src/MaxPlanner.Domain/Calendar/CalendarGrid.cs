namespace MaxPlanner.Domain.Calendar;

/// <summary>
/// The release-1 workweek: weeks start on Sunday and Sunday–Thursday are shown; Friday and Saturday
/// are hidden but remain real consecutive dates (B-04, B-08.7). Fixed in release 1 (ADR 0020).
/// </summary>
public static class Workweek
{
    public const DayOfWeek FirstDay = DayOfWeek.Sunday;

    public static IReadOnlyList<DayOfWeek> VisibleDays { get; } =
        [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday];

    public static bool IsVisible(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday);

    /// <summary>The Sunday on or before <paramref name="day"/>.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(int)day.DayOfWeek);

    /// <summary>The first shown day after <paramref name="day"/>, skipping Friday and Saturday.</summary>
    public static DateOnly NextVisibleDay(DateOnly day)
    {
        do
        {
            day = day.AddDays(1);
        }
        while (!IsVisible(day));

        return day;
    }

    /// <summary>The last shown day before <paramref name="day"/>, skipping Friday and Saturday.</summary>
    public static DateOnly PreviousVisibleDay(DateOnly day)
    {
        do
        {
            day = day.AddDays(-1);
        }
        while (!IsVisible(day));

        return day;
    }
}

/// <summary>One calendar row: a Sunday-to-Saturday week, of which the workweek columns are shown.</summary>
public sealed record WeekRow(DateOnly WeekStart)
{
    public DateOnly WeekEnd => WeekStart.AddDays(6);

    /// <summary>The visible days in column order (column 0 = Sunday).</summary>
    public IReadOnlyList<DateOnly> VisibleDays => [.. Workweek.VisibleDays.Select(d => WeekStart.AddDays((int)d))];
}

/// <summary>Row sets for the Month and Week calendar modes (B-08.1–2).</summary>
public static class CalendarGrid
{
    /// <summary>Every week that intersects the month; days outside the month are styled as adjacent-month by the view.</summary>
    public static IReadOnlyList<WeekRow> Month(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var rows = new List<WeekRow>();
        for (var start = Workweek.WeekStart(first); start <= last; start = start.AddDays(7))
        {
            rows.Add(new WeekRow(start));
        }

        return rows;
    }

    public static IReadOnlyList<WeekRow> Week(DateOnly anyDayInWeek) => [new WeekRow(Workweek.WeekStart(anyDayInWeek))];

    /// <summary>
    /// The first day of the month <paramref name="offset"/> months away. Anchoring on day 1 means moving
    /// from a 31-day month to a shorter one can never skip a month (B-08.2).
    /// </summary>
    public static DateOnly ShiftMonth(DateOnly anyDayInMonth, int offset) =>
        new DateOnly(anyDayInMonth.Year, anyDayInMonth.Month, 1).AddMonths(offset);
}
