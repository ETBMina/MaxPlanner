using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Common;

/// <summary>
/// What every date picker in the app shares: an empty picker shows "Pick a date". XAML binds
/// <see cref="Placeholder"/>; pickers built in code call <see cref="Apply(CalendarDatePicker)"/>.
/// </summary>
public static class DatePickers
{
    /// <summary>"Pick a date", an empty picker's placeholder.</summary>
    public static string Placeholder => ResourceStrings.Shared.GetString("Common_DatePlaceholder");

    /// <summary>Gives a picker built in code the placeholder.</summary>
    public static CalendarDatePicker Apply(CalendarDatePicker picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        picker.PlaceholderText = Placeholder;
        return picker;
    }
}
