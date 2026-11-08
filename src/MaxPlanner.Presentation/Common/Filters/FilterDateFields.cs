using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Common.Filters;

/// <summary>CalendarDatePicker values carry the local calendar day; the offset is never a business value (T-05).</summary>
internal static class PickerDates
{
    public static DateOnly? Day(DateTimeOffset? value) => value is { } date ? DateOnly.FromDateTime(date.DateTime) : null;

    public static DateTimeOffset? Picker(DateOnly? day) => day is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)) : null;
}

/// <summary>
/// One day cell of the range picker; its state is in its accessible name as well as its visuals. Days inside the range
/// (committed, or previewed while an end is being picked) sit on the accent band; the first and last day are light
/// rounded squares; today keeps its accent circle. A pick or preview updates the cells in place, so keyboard focus
/// stays on the day. Days of the neighbouring months only keep the grid's shape: they are not shown, so each day
/// appears (and has its AutomationId) once across the two months.
/// </summary>
public sealed partial class CalendarDayViewModel : ObservableObject
{
    private readonly string _todayState;
    private readonly string _selectedState;

    internal CalendarDayViewModel(DateOnly date, bool isInMonth, bool isToday, string todayState, string selectedState, Action<DateOnly> pick)
    {
        Date = date;
        IsInMonth = isInMonth;
        IsToday = isToday;
        _todayState = todayState;
        _selectedState = selectedState;
        PickCommand = new RelayCommand(() => pick(date));
    }

    public DateOnly Date { get; }

    public string Text => Date.Day.ToString(CultureInfo.CurrentCulture);

    public bool IsInMonth { get; }

    public bool IsToday { get; }

    /// <summary>Inside the shown range (committed or previewed), ends included: drawn on the band.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRangeMiddle), nameof(Name), nameof(ShowsBand), nameof(ShowsPlainText), nameof(ShowsBandText), nameof(ShowsEndText))]
    public partial bool IsInRange { get; private set; }

    /// <summary>The first or last day of the shown range: a light rounded square on the band.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRangeMiddle), nameof(ShowsBandText), nameof(ShowsEndText))]
    public partial bool IsRangeEnd { get; private set; }

    /// <summary>Inside the range but not an end.</summary>
    public bool IsRangeMiddle => IsInRange && !IsRangeEnd;

    /// <summary>The band runs behind the days of this month that are in the range.</summary>
    public bool ShowsBand => IsInRange && IsInMonth;

    // One of four text colours: plain, on the band, on an end square, or on today's accent circle.
    public bool ShowsPlainText => !IsToday && !IsInRange;

    public bool ShowsBandText => !IsToday && IsRangeMiddle;

    public bool ShowsEndText => !IsToday && IsRangeEnd;

    /// <summary>"Saturday, November 7, 2026, today, selected".</summary>
    public string Name
    {
        get
        {
            var parts = new List<string> { Date.ToString("D", CultureInfo.CurrentCulture) };
            if (IsToday)
            {
                parts.Add(_todayState);
            }

            if (IsInRange)
            {
                parts.Add(_selectedState);
            }

            return string.Join(", ", parts);
        }
    }

    public string AutomationId => "Filters_DateRange_Day_" + Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public IRelayCommand PickCommand { get; }

    internal void Show(DateOnly? from, DateOnly? to)
    {
        IsInRange = (from is not null || to is not null) && (from is null || Date >= from) && (to is null || Date <= to);
        IsRangeEnd = Date == from || Date == to;
    }
}

/// <summary>One month of the range picker: a title, the day names from Sunday, and six weeks of days.</summary>
public sealed class CalendarMonthViewModel(string title, IReadOnlyList<string> dayNames, IReadOnlyList<CalendarDayViewModel> days)
{
    public string Title { get; } = title;

    public IReadOnlyList<string> DayNames { get; } = dayNames;

    public IReadOnlyList<CalendarDayViewModel> Days { get; } = days;
}

/// <summary>
/// The "Customised" two-month range picker (design §2.6): previous/next month, today marked, the selection text, Clear
/// and Go to today. The first click picks a one-day range and the second extends it (either order), both inclusive,
/// so the range is never inverted. Between the two, the day under the pointer or keyboard focus previews the range.
/// Weeks start on Sunday (app convention). Every pick commits at once.
/// </summary>
public sealed partial class DateRangePickerViewModel : ObservableObject
{
    private readonly ILocalizedStrings _strings;
    private readonly string _todayState;
    private readonly string _selectedState;
    private Action? _changed;
    private DateOnly _firstMonth;
    private DateOnly? _previewDay;
    private List<CalendarMonthViewModel>? _months;

    internal DateRangePickerViewModel(DateOnly? from, DateOnly? to, DateOnly today, ILocalizedStrings strings)
    {
        _strings = strings;
        _todayState = strings.GetString("Filters_DayTodayState");
        _selectedState = strings.GetString("Filters_DaySelectedState");
        Today = today;
        From = from;
        To = to;
        _firstMonth = MonthOf(from ?? to ?? today);
    }

    public DateOnly Today { get; }

    public DateOnly? From { get; private set; }

    public DateOnly? To { get; private set; }

    public bool HasRange => From is not null || To is not null;

    /// <summary>The first day is picked and the next pick sets the other end.</summary>
    public bool IsPickingEnd { get; private set; }

    /// <summary>"Nov 1, 2026 – Nov 7, 2026" (the user's culture); empty without a range.</summary>
    public string RangeText => FilterText.Range(_strings, From, To);

    /// <summary>
    /// The footer under the first month, in the culture's short date format: "03/11/2026 – Select an end date" while
    /// the end is being picked, "03/11/2026 – 13/11/2026" once committed, empty without a range.
    /// </summary>
    public string SelectionText => (IsPickingEnd, From, To) switch
    {
        (true, { } from, _) => _strings.Format("Filters_RangeSelectEnd", Short(from)),
        (false, { } from, { } to) => _strings.Format("Filters_RangeSelection", Short(from), Short(to)),
        _ => RangeText,
    };

    /// <summary>The two months shown side by side; built when the picker is first shown (most menus never open it).</summary>
    public IReadOnlyList<CalendarMonthViewModel> Months => _months ??= Build();

    public void Pick(DateOnly day)
    {
        if (IsPickingEnd && From is { } start)
        {
            IsPickingEnd = false;
            Set(day < start ? day : start, day < start ? start : day);
        }
        else
        {
            IsPickingEnd = true;
            Set(day, day);
        }

        _changed?.Invoke();
    }

    /// <summary>
    /// While the end is being picked, shows the range from the picked day to <paramref name="day"/> on the band without
    /// committing it; null (the pointer left the days) shows the committed range again.
    /// </summary>
    public void Preview(DateOnly? day)
    {
        _previewDay = IsPickingEnd ? day : null;
        ShowDays();
    }

    internal void Attach(Action changed) => _changed = changed;

    /// <summary>Shows a range without raising the change callback (Clear all, another choice took over).</summary>
    internal void Load(DateOnly? from, DateOnly? to)
    {
        IsPickingEnd = false;
        Set(from, to);
    }

    [RelayCommand]
    private void PreviousMonth() => ShowMonth(_firstMonth.AddMonths(-1));

    [RelayCommand]
    private void NextMonth() => ShowMonth(_firstMonth.AddMonths(1));

    [RelayCommand]
    private void GoToToday() => ShowMonth(MonthOf(Today));

    [RelayCommand(CanExecute = nameof(HasRange))]
    private void ClearRange()
    {
        Load(null, null);
        _changed?.Invoke();
    }

    private static DateOnly MonthOf(DateOnly day) => new(day.Year, day.Month, 1);

    private static string Short(DateOnly day) => day.ToString("d", CultureInfo.CurrentCulture);

    private void ShowMonth(DateOnly month)
    {
        _firstMonth = month;
        _months = Build();
        OnPropertyChanged(nameof(Months));
    }

    private void Set(DateOnly? from, DateOnly? to)
    {
        From = from;
        To = to;
        _previewDay = null;
        OnPropertyChanged(nameof(From));
        OnPropertyChanged(nameof(To));
        OnPropertyChanged(nameof(HasRange));
        OnPropertyChanged(nameof(IsPickingEnd));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(SelectionText));
        ClearRangeCommand.NotifyCanExecuteChanged();
        ShowDays();
    }

    /// <summary>The range the days show: the preview while the end is being picked, otherwise the committed one.</summary>
    private (DateOnly? From, DateOnly? To) Shown() =>
        _previewDay is { } day && From is { } start ? (day < start ? day : start, day < start ? start : day) : (From, To);

    private void ShowDays()
    {
        var (from, to) = Shown();
        foreach (var day in _months?.SelectMany(m => m.Days) ?? [])
        {
            day.Show(from, to);
        }
    }

    private List<CalendarMonthViewModel> Build() => [Month(_firstMonth), Month(_firstMonth.AddMonths(1))];

    private CalendarMonthViewModel Month(DateOnly first)
    {
        var culture = CultureInfo.CurrentCulture;
        var start = DatePresetRanges.WeekStart(first);
        var (from, to) = Shown();
        var days = new List<CalendarDayViewModel>(42);
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var day = new CalendarDayViewModel(date, date.Month == first.Month, date == Today, _todayState, _selectedState, Pick);
            day.Show(from, to);
            days.Add(day);
        }

        var dayNames = culture.DateTimeFormat.ShortestDayNames;
        return new CalendarMonthViewModel(first.ToString("Y", culture), [.. Enumerable.Range(0, 7).Select(d => dayNames[d])], days);
    }
}

/// <summary>
/// A date submenu (Start date, End date, Planned dates, a Date custom field): the rolling presets and No date
/// (multi-select, a union) and Customised, one inclusive range from the picker (design §2.6).
/// </summary>
public sealed partial class FilterDateFieldViewModel : ObservableObject, IFilterEditor
{
    private readonly (DatePresets Preset, FilterChoiceViewModel Choice)[] _presets;
    private readonly FilterChoiceViewModel _noDate;
    private readonly ILocalizedStrings _strings;

    internal FilterDateFieldViewModel(string key, DateRangeFilter? filter, DateOnly today, ILocalizedStrings strings, Action changed)
    {
        Key = key;
        _strings = strings;
        _presets = [.. DatePresetRanges.All.Select(p => (p, new FilterChoiceViewModel(
            Guid.Empty, strings.GetString("Filters_Date" + p), "Filters_" + key + "_" + p, filter?.Presets.HasFlag(p) == true, PresetGlyph(p))))];
        _noDate = new FilterChoiceViewModel(Guid.Empty, strings.GetString("Filters_DateNoDate"), "Filters_" + key + "_NoDate", filter?.IncludeUndated == true, "");
        Presets = [.. _presets.Select(p => p.Choice), _noDate];
        foreach (var choice in Presets)
        {
            choice.Attach(_ => changed());
        }

        Range = new DateRangePickerViewModel(filter?.From, filter?.To, today, strings);
        Range.Attach(() =>
        {
            NotifyRange();
            changed();
        });
    }

    public string Key { get; }

    public string CustomisedAutomationId => "Filters_" + Key + "_Customised";

    /// <summary>Past, Today, Tomorrow, This week, Next week, Future, No date.</summary>
    public IReadOnlyList<FilterChoiceViewModel> Presets { get; }

    public DateRangePickerViewModel Range { get; }

    /// <summary>A Customised range is chosen; the Customised item shows a CheckMark and the range.</summary>
    public bool IsCustom => Range.HasRange;

    public string CustomText => Range.RangeText;

    /// <summary>"Customised" or "Customised, Nov 1, 2026 – Nov 7, 2026" (the item's accessible name).</summary>
    public string CustomisedName => IsCustom ? _strings.Format("Filters_CustomisedName", CustomText) : _strings.GetString("Filters_DateCustomised");

    public DateRangeFilter Filter => new(
        Range.From,
        Range.To,
        _noDate.IsChecked,
        _presets.Where(p => p.Choice.IsChecked).Aggregate(DatePresets.None, (all, p) => all | p.Preset));

    public bool IsSet => Filter.IsSet;

    public void Clear()
    {
        foreach (var choice in Presets)
        {
            choice.SetQuietly(false);
        }

        Range.Load(null, null);
        NotifyRange();
    }

    /// <summary>Segoe Fluent Icons: Warning for Past (as in the reference), day and week calendars, Calendar for Future.</summary>
    private static string PresetGlyph(DatePresets preset) => preset switch
    {
        DatePresets.Past => "",
        DatePresets.Today or DatePresets.Tomorrow => "",
        DatePresets.ThisWeek or DatePresets.NextWeek => "",
        _ => "",
    };

    private void NotifyRange()
    {
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(CustomText));
        OnPropertyChanged(nameof(CustomisedName));
    }
}

/// <summary>
/// The Active date submenu (was "Period"): Last 7 days, Last 30 days, or a Customised range, one at a time. Choosing
/// none is All time. A record matches when its active span overlaps the range (Application <see cref="PeriodFilter"/>).
/// </summary>
public sealed partial class ActiveDateFieldViewModel : ObservableObject, IFilterEditor
{
    private readonly Action _changed;
    private readonly ILocalizedStrings _strings;

    internal ActiveDateFieldViewModel(PeriodFilter? period, DateOnly today, ILocalizedStrings strings, Action changed)
    {
        _changed = changed;
        _strings = strings;
        Last7Days = new FilterChoiceViewModel(Guid.Empty, strings.GetString("Filters_PeriodLast7Days"), "Filters_ActiveDate_Last7Days", period?.Preset == PeriodPreset.Last7Days, "");
        Last30Days = new FilterChoiceViewModel(Guid.Empty, strings.GetString("Filters_PeriodLast30Days"), "Filters_ActiveDate_Last30Days", period?.Preset == PeriodPreset.Last30Days, "");
        Choices = [Last7Days, Last30Days];
        var custom = period is { Preset: PeriodPreset.Custom };
        Range = new DateRangePickerViewModel(custom ? period!.From : null, custom ? period!.To : null, today, strings);
        Last7Days.Attach(c => OnPresetToggled(c, Last30Days));
        Last30Days.Attach(c => OnPresetToggled(c, Last7Days));
        Range.Attach(() =>
        {
            Last7Days.SetQuietly(false);
            Last30Days.SetQuietly(false);
            NotifyRange();
            _changed();
        });
    }

    public string Key => FilterKeys.ActiveDate;

    public FilterChoiceViewModel Last7Days { get; }

    public FilterChoiceViewModel Last30Days { get; }

    public IReadOnlyList<FilterChoiceViewModel> Choices { get; }

    public DateRangePickerViewModel Range { get; }

    public bool IsCustom => Range.HasRange;

    public string CustomText => Range.RangeText;

    /// <summary>"Customised" or "Customised, Nov 1, 2026 – Nov 7, 2026" (the item's accessible name).</summary>
    public string CustomisedName => IsCustom ? _strings.Format("Filters_CustomisedName", CustomText) : _strings.GetString("Filters_DateCustomised");

    /// <summary>The chosen range, or null for All time.</summary>
    public PeriodFilter? Filter =>
        Last7Days.IsChecked ? PeriodFilter.Last7Days
        : Last30Days.IsChecked ? PeriodFilter.Last30Days
        : Range.HasRange ? PeriodFilter.Custom(Range.From, Range.To)
        : null;

    public bool IsSet => Filter is not null;

    public void Clear()
    {
        Last7Days.SetQuietly(false);
        Last30Days.SetQuietly(false);
        Range.Load(null, null);
        NotifyRange();
    }

    private void OnPresetToggled(FilterChoiceViewModel choice, FilterChoiceViewModel other)
    {
        if (choice.IsChecked)
        {
            other.SetQuietly(false);
            Range.Load(null, null);
            NotifyRange();
        }

        _changed();
    }

    private void NotifyRange()
    {
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(CustomText));
        OnPropertyChanged(nameof(CustomisedName));
    }
}

/// <summary>
/// The Dashboard's inline Active date box (was "Period"): All time, Last 7 days, Last 30 days, or a custom inclusive
/// From/To range. An inverted custom range shows the inline error and is never applied (B-10).
/// </summary>
public sealed partial class PeriodFieldViewModel : ObservableObject
{
    private const int CustomIndex = (int)PeriodPreset.Custom;
    private readonly Action? _changed;
    private readonly string _invertedError;
    private bool _isLoading;
    private int _selectedIndex;

    internal PeriodFieldViewModel(ILocalizedStrings strings, PeriodFilter? period, Action? changed)
    {
        ArgumentNullException.ThrowIfNull(strings);
        _invertedError = strings.GetString("Filters_InvertedRange");
        Load(period);
        _changed = changed;
    }

    /// <summary>
    /// The ComboBox index, in <see cref="PeriodPreset"/> order (the items are declared in XAML). A ComboBox reports
    /// -1 while its items are replaced or not yet there; such a write is ignored, so it can never clear the range.
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value is < 0 or > CustomIndex || !SetProperty(ref _selectedIndex, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(Error));
            OnPropertyChanged(nameof(HasError));
            Notify();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    public partial DateTimeOffset? From { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    public partial DateTimeOffset? To { get; set; }

    /// <summary>Custom range is chosen: the From and To pickers are shown.</summary>
    public bool IsCustom => SelectedIndex == CustomIndex;

    /// <summary>The chosen range, or null for All time and a custom range without dates.</summary>
    public PeriodFilter? Filter
    {
        get
        {
            var period = SelectedIndex switch
            {
                1 => PeriodFilter.Last7Days,
                2 => PeriodFilter.Last30Days,
                CustomIndex => PeriodFilter.Custom(PickerDates.Day(From), PickerDates.Day(To)),
                _ => PeriodFilter.AllTime,
            };
            return period.IsSet ? period : null;
        }
    }

    public bool HasError => Filter is { IsInverted: true };

    public string Error => HasError ? _invertedError : string.Empty;

    /// <summary>The visible text of a set Active date: the preset name, or the custom range.</summary>
    internal static string Text(ILocalizedStrings strings, PeriodFilter period) => period.Preset switch
    {
        PeriodPreset.Last7Days => strings.GetString("Filters_PeriodLast7Days"),
        PeriodPreset.Last30Days => strings.GetString("Filters_PeriodLast30Days"),
        _ => FilterText.Range(strings, period.From, period.To),
    };

    /// <summary>Shows <paramref name="period"/> without raising the change callback.</summary>
    internal void Load(PeriodFilter? period)
    {
        _isLoading = true;
        SelectedIndex = (int)(period?.Preset ?? PeriodPreset.AllTime);
        From = PickerDates.Picker(period?.From);
        To = PickerDates.Picker(period?.To);
        _isLoading = false;
    }

    partial void OnFromChanged(DateTimeOffset? value) => Notify();

    partial void OnToChanged(DateTimeOffset? value) => Notify();

    private void Notify()
    {
        if (!_isLoading)
        {
            _changed?.Invoke();
        }
    }
}
