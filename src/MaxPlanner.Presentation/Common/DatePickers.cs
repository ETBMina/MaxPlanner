using MaxPlanner.Domain.Calendar;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml.Controls;
using GlobalizationDayOfWeek = Windows.Globalization.DayOfWeek;

namespace MaxPlanner.Presentation.Common;

/// <summary>
/// What every date picker and calendar view in the app shares: the first day of the week comes from the app's workweek
/// (<see cref="Workweek.FirstDay"/>, the Calendar's "Workweek Sunday–Thursday"), not the system setting, and an empty
/// picker shows "Pick a date". XAML binds <see cref="FirstDayOfWeek"/> and <see cref="Placeholder"/>; pickers and
/// calendar views built in code call <c>Apply</c>.
/// </summary>
public static class DatePickers
{
    /// <summary>The first day of the week of the app's workweek.</summary>
    public static GlobalizationDayOfWeek FirstDayOfWeek => FirstDayOf(Workweek.FirstDay);

    /// <summary>"Pick a date", an empty picker's placeholder.</summary>
    public static string Placeholder => ResourceStrings.Shared.GetString("Common_DatePlaceholder");

    /// <summary>The picker's day of the week for a .NET one.</summary>
    public static GlobalizationDayOfWeek FirstDayOf(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => GlobalizationDayOfWeek.Sunday,
        DayOfWeek.Monday => GlobalizationDayOfWeek.Monday,
        DayOfWeek.Tuesday => GlobalizationDayOfWeek.Tuesday,
        DayOfWeek.Wednesday => GlobalizationDayOfWeek.Wednesday,
        DayOfWeek.Thursday => GlobalizationDayOfWeek.Thursday,
        DayOfWeek.Friday => GlobalizationDayOfWeek.Friday,
        DayOfWeek.Saturday => GlobalizationDayOfWeek.Saturday,
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, null),
    };

    /// <summary>Gives a picker built in code the workweek's first day and the placeholder.</summary>
    public static CalendarDatePicker Apply(CalendarDatePicker picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        picker.FirstDayOfWeek = FirstDayOfWeek;
        picker.PlaceholderText = Placeholder;
        return picker;
    }

    /// <summary>Gives a calendar view built in code the workweek's first day.</summary>
    public static CalendarView Apply(CalendarView calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        calendar.FirstDayOfWeek = FirstDayOfWeek;
        return calendar;
    }
}
