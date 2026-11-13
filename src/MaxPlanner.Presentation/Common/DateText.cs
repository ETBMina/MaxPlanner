using System.Globalization;

namespace MaxPlanner.Presentation.Common;

/// <summary>
/// The date texts the app shows, one method per format, all in the current culture. Machine keys and file names
/// (yyyyMMdd, yyyy-MM-dd, invariant culture) are not display text and do not use this.
/// </summary>
public static class DateText
{
    /// <summary>The short date ("d"), for example 11/12/2026.</summary>
    public static string ShortDate(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>The long date ("D"), for example Thursday, November 12, 2026.</summary>
    public static string LongDate(DateOnly date) => date.ToString("D", CultureInfo.CurrentCulture);

    /// <summary>The abbreviated month and day ("MMM d"), for example Nov 12.</summary>
    public static string MonthDay(DateOnly date) => date.ToString("MMM d", CultureInfo.CurrentCulture);

    /// <summary>The abbreviated month, day and year ("MMM d, yyyy"), for example Nov 12, 2026.</summary>
    public static string MonthDayYear(DateOnly date) => date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>
    /// A record's date shown as text (created, started, ended, planned, a date field's value), always with the year:
    /// <see cref="MonthDayYear(DateOnly)"/>, for example Nov 12, 2026. Date pickers keep their own format.
    /// </summary>
    public static string RecordDate(DateOnly date) => MonthDayYear(date);

    /// <summary>The <see cref="RecordDate(DateOnly)"/> of a local date and time's day, for example Nov 12, 2026.</summary>
    public static string RecordDate(DateTimeOffset dateTime) => RecordDate(DateOnly.FromDateTime(dateTime.DateTime));

    /// <summary>The abbreviated month and year ("MMM yyyy"), for example Nov 2026.</summary>
    public static string MonthYear(DateOnly date) => date.ToString("MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>The culture's month-day pattern ("M"), for example November 12.</summary>
    public static string LongMonthDay(DateOnly date) => date.ToString("M", CultureInfo.CurrentCulture);

    /// <summary>The short date and short time ("g"), for example 11/12/2026 3:45 PM.</summary>
    public static string ShortDateTime(DateTimeOffset dateTime) => dateTime.ToString("g", CultureInfo.CurrentCulture);
}
