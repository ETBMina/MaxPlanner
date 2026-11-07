using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Tasks;

namespace MaxPlanner.Presentation.Common.Filters;

/// <summary>One lookup chip in the Filters flyout (design §2.6): a checked chip shows a CheckMark as well as its fill.</summary>
public sealed partial class FilterChoiceViewModel : ObservableObject
{
    private readonly Action _changed;

    internal FilterChoiceViewModel(Guid id, string label, string automationId, bool isChecked, Action changed)
    {
        Id = id;
        Label = label;
        AutomationId = automationId;
        IsChecked = isChecked;
        _changed = changed;
    }

    /// <summary>The stable lookup ID; <see cref="Guid.Empty"/> is "General" in a squad group.</summary>
    public Guid Id { get; }

    public string Label { get; }

    public string AutomationId { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => _changed?.Invoke();
}

/// <summary>A lookup field (Status, Priority, Squad, CR Owner, or a Lookup custom field) as chips; values within it are OR.</summary>
public sealed class FilterChoiceGroupViewModel(string key, string header, IReadOnlyList<FilterChoiceViewModel> choices)
{
    public string Key { get; } = key;

    public string Header { get; } = header;

    public IReadOnlyList<FilterChoiceViewModel> Choices { get; } = choices;

    public IReadOnlyList<Guid> CheckedIds => [.. Choices.Where(c => c.IsChecked).Select(c => c.Id)];

    internal void Clear()
    {
        foreach (var choice in Choices)
        {
            choice.IsChecked = false;
        }
    }
}

/// <summary>Designers, Developers, or Tags as a people/tags picker; Tags also offer Any/All selected (design §2.6).</summary>
public sealed partial class FilterPickerFieldViewModel : ObservableObject
{
    internal FilterPickerFieldViewModel(string key, string header, string automationId, TokenPickerViewModel picker, bool showsMatchMode, bool matchAll)
    {
        Key = key;
        Header = header;
        AutomationId = automationId;
        Picker = picker;
        ShowsMatchMode = showsMatchMode;
        MatchModeIndex = matchAll ? 1 : 0;
    }

    public string Key { get; }

    public string Header { get; }

    public string AutomationId { get; }

    public TokenPickerViewModel Picker { get; }

    public bool ShowsMatchMode { get; }

    /// <summary>0 = Any selected, 1 = All selected (RadioButtons.SelectedIndex).</summary>
    [ObservableProperty]
    public partial int MatchModeIndex { get; set; }
}

/// <summary>A "contains" text field: Service or a text custom field.</summary>
public sealed partial class FilterTextFieldViewModel : ObservableObject
{
    private readonly Action _changed;

    internal FilterTextFieldViewModel(string key, string header, string automationId, string? text, Action changed)
    {
        Key = key;
        Header = header;
        AutomationId = automationId;
        Text = text ?? string.Empty;
        _changed = changed;
    }

    public string Key { get; }

    public string Header { get; }

    public string AutomationId { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    partial void OnTextChanged(string value) => _changed?.Invoke();
}

/// <summary>An inclusive From/To date field with "Include records without a {field}" and the inverted-range error (B-10).</summary>
public sealed partial class FilterDateFieldViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly string _invertedError;

    internal FilterDateFieldViewModel(string key, string header, string includeLabel, string automationId, DateRangeFilter? range, string invertedError, Action changed)
    {
        Key = key;
        Header = header;
        IncludeLabel = includeLabel;
        AutomationId = automationId;
        _invertedError = invertedError;
        From = Picker(range?.From);
        To = Picker(range?.To);
        IncludeUndated = range?.IncludeUndated ?? false;
        _changed = changed;
    }

    public string Key { get; }

    public string Header { get; }

    public string IncludeLabel { get; }

    public string AutomationId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    public partial DateTimeOffset? From { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    public partial DateTimeOffset? To { get; set; }

    [ObservableProperty]
    public partial bool IncludeUndated { get; set; }

    public DateRangeFilter Range => new(Day(From), Day(To), IncludeUndated);

    public bool HasError => Range.IsInverted;

    public string Error => HasError ? _invertedError : string.Empty;

    internal void Clear()
    {
        From = null;
        To = null;
        IncludeUndated = false;
    }

    partial void OnFromChanged(DateTimeOffset? value) => _changed?.Invoke();

    partial void OnToChanged(DateTimeOffset? value) => _changed?.Invoke();

    partial void OnIncludeUndatedChanged(bool value) => _changed?.Invoke();

    // CalendarDatePicker dates carry the local calendar day; the offset is never a business value (T-05).
    internal static DateOnly? Day(DateTimeOffset? value) => value is { } date ? DateOnly.FromDateTime(date.DateTime) : null;

    internal static DateTimeOffset? Picker(DateOnly? day) => day is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)) : null;
}

/// <summary>
/// The Period field of the Filters flyout and the Dashboard: All time, Last 7 days, Last 30 days, or a custom
/// inclusive From/To range. An inverted custom range shows the inline error and is never applied (B-10).
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
    /// -1 while its items are replaced or not yet there; such a write is ignored, so it can never clear the period.
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

    /// <summary>The chosen period, or null for All time and a custom range without dates.</summary>
    public PeriodFilter? Filter
    {
        get
        {
            var period = SelectedIndex switch
            {
                1 => PeriodFilter.Last7Days,
                2 => PeriodFilter.Last30Days,
                CustomIndex => PeriodFilter.Custom(FilterDateFieldViewModel.Day(From), FilterDateFieldViewModel.Day(To)),
                _ => PeriodFilter.AllTime,
            };
            return period.IsSet ? period : null;
        }
    }

    public bool HasError => Filter is { IsInverted: true };

    public string Error => HasError ? _invertedError : string.Empty;

    /// <summary>The visible text of a set period: the preset name, or the custom range.</summary>
    internal static string Text(ILocalizedStrings strings, PeriodFilter period) => period.Preset switch
    {
        PeriodPreset.Last7Days => strings.GetString("Filters_PeriodLast7Days"),
        PeriodPreset.Last30Days => strings.GetString("Filters_PeriodLast30Days"),
        _ => FilterText.Range(strings, new DateRangeFilter(period.From, period.To, IncludeUndated: false)),
    };

    /// <summary>Shows <paramref name="period"/> without raising the change callback.</summary>
    internal void Load(PeriodFilter? period)
    {
        _isLoading = true;
        SelectedIndex = (int)(period?.Preset ?? PeriodPreset.AllTime);
        From = FilterDateFieldViewModel.Picker(period?.From);
        To = FilterDateFieldViewModel.Picker(period?.To);
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

/// <summary>Min/Max for a Number custom field (NumberBox uses NaN for empty).</summary>
public sealed partial class FilterNumberFieldViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly string _invertedError;

    internal FilterNumberFieldViewModel(string key, string header, string automationId, NumberRangeFilter? range, string invertedError, Action changed)
    {
        Key = key;
        Header = header;
        AutomationId = automationId;
        _invertedError = invertedError;
        Min = range?.Min is { } min ? (double)min : double.NaN;
        Max = range?.Max is { } max ? (double)max : double.NaN;
        _changed = changed;
    }

    public string Key { get; }

    public string Header { get; }

    public string AutomationId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    public partial double Min { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    public partial double Max { get; set; }

    public NumberRangeFilter Range => new(Value(Min), Value(Max));

    public bool HasError => Range.IsInverted;

    public string Error => HasError ? _invertedError : string.Empty;

    internal void Clear()
    {
        Min = double.NaN;
        Max = double.NaN;
    }

    partial void OnMinChanged(double value) => _changed?.Invoke();

    partial void OnMaxChanged(double value) => _changed?.Invoke();

    private static decimal? Value(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? null : (decimal)Math.Clamp(value, (double)decimal.MinValue / 2, (double)decimal.MaxValue / 2);
}

/// <summary>An entry of the CR picker: "Any CR", "No CR", or one CR (design §2.6).</summary>
public sealed record FilterCrChoice(CrLinkFilter Filter, string Display)
{
    public override string ToString() => Display;
}

/// <summary>The task CR field: an AutoSuggestBox over the current CRs plus the two special entries.</summary>
public sealed partial class FilterCrFieldViewModel : ObservableObject
{
    private readonly Func<string, IReadOnlyList<FilterCrChoice>> _suggest;
    private readonly Action _changed;

    internal FilterCrFieldViewModel(string header, FilterCrChoice selected, Func<string, IReadOnlyList<FilterCrChoice>> suggest, Action changed)
    {
        Header = header;
        _suggest = suggest;
        Selected = selected;
        Text = selected.Display;
        _changed = changed;
    }

    public string Header { get; }

    public ObservableCollection<FilterCrChoice> Suggestions { get; } = [];

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial FilterCrChoice Selected { get; private set; }

    /// <summary>The user typed: refresh the suggestions (typing alone never changes the chosen CR).</summary>
    public void UpdateSuggestions(string query)
    {
        Suggestions.Clear();
        foreach (var choice in _suggest(query ?? string.Empty))
        {
            Suggestions.Add(choice);
        }
    }

    public void Choose(FilterCrChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Selected = choice;
        Text = choice.Display;
        _changed?.Invoke();
    }
}

/// <summary>
/// The Filters flyout draft (B-10, design §2.6): Apply commits it, Cancel or light dismiss discards it, and Clear
/// clears only the draft. The header counts the fields set in the draft. Use on the UI thread.
/// </summary>
public sealed partial class FilterDraftViewModel : ObservableObject
{
    private readonly ILocalizedStrings _strings;
    private readonly string _scopeTitle;
    private readonly Func<FilterDraftViewModel, int> _countFields;
    private bool _isBuilding = true;

    internal FilterDraftViewModel(ILocalizedStrings strings, string scopeTitle, Func<FilterDraftViewModel, int> countFields)
    {
        _strings = strings;
        _scopeTitle = scopeTitle;
        _countFields = countFields;
    }

    public List<FilterChoiceGroupViewModel> ChoiceGroups { get; } = [];

    public List<FilterPickerFieldViewModel> PickerFields { get; } = [];

    public FilterCrFieldViewModel? CrField { get; internal set; }

    public bool HasCrField => CrField is not null;

    public List<FilterTextFieldViewModel> TextFields { get; } = [];

    public List<FilterDateFieldViewModel> DateFields { get; } = [];

    /// <summary>The Period field (start date in the period); separate from the date range fields.</summary>
    public PeriodFieldViewModel? Period { get; internal set; }

    public List<FilterNumberFieldViewModel> NumberFields { get; } = [];

    /// <summary>"Filter tasks — Board and Grid · 3 fields set".</summary>
    [ObservableProperty]
    public partial string HeaderText { get; private set; } = string.Empty;

    /// <summary>B-10: an inverted range shows its inline error and disables Apply.</summary>
    [ObservableProperty]
    public partial bool CanApply { get; private set; } = true;

    internal Action Changed => OnFieldChanged;

    internal void EndBuild()
    {
        _isBuilding = false;
        foreach (var field in PickerFields)
        {
            field.Picker.Changed += (_, _) => OnFieldChanged();
            field.PropertyChanged += (_, _) => OnFieldChanged();
        }

        OnFieldChanged();
    }

    /// <summary>Clear (design §2.6): clears the draft only; nothing changes until Apply.</summary>
    [RelayCommand]
    private void Clear()
    {
        _isBuilding = true;
        foreach (var group in ChoiceGroups)
        {
            group.Clear();
        }

        foreach (var field in PickerFields)
        {
            field.Picker.Load([]);
            field.MatchModeIndex = 0;
        }

        CrField?.Choose(new FilterCrChoice(CrLinkFilter.Any, _strings.GetString("Filters_AnyCr")));
        foreach (var field in TextFields)
        {
            field.Text = string.Empty;
        }

        foreach (var field in DateFields)
        {
            field.Clear();
        }

        Period?.Load(null);

        foreach (var field in NumberFields)
        {
            field.Clear();
        }

        _isBuilding = false;
        OnFieldChanged();
    }

    private void OnFieldChanged()
    {
        if (_isBuilding)
        {
            return;
        }

        var count = _countFields(this);
        HeaderText = _strings.Format(count == 1 ? "Filters_HeaderOne" : "Filters_Header", _scopeTitle, count);
        CanApply = DateFields.All(f => !f.HasError) && NumberFields.All(f => !f.HasError) && Period is not { HasError: true };
    }
}
