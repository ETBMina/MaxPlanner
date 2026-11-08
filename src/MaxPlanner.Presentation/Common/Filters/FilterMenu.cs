using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Common.Filters;

/// <summary>The colour of a menu item's glyph; the item's text always names the value, so colour is never the only cue.</summary>
public enum FilterGlyphTone
{
    Default,

    /// <summary>The first priority rank (design §6.1).</summary>
    Critical,

    /// <summary>The second priority rank.</summary>
    Caution,

    /// <summary>A completed status (design §6.2).</summary>
    Success,
}

/// <summary>One submenu of the filter menu (design §2.6). Every change commits to the scope at once.</summary>
public interface IFilterEditor
{
    /// <summary>The field key (<see cref="FilterKeys"/>), also the AutomationId part <c>Filters_{Key}_…</c>.</summary>
    string Key { get; }

    /// <summary>The field narrows the records; the row shows it.</summary>
    bool IsSet { get; }

    /// <summary>Clear all: the field goes back to "no filter".</summary>
    void Clear();
}

/// <summary>
/// One checkable submenu item (design §2.6): a checked item shows a CheckMark as well as any colour, so state is never
/// colour alone. Toggling it commits at once.
/// </summary>
public sealed partial class FilterChoiceViewModel : ObservableObject
{
    private Action<FilterChoiceViewModel>? _toggled;

    internal FilterChoiceViewModel(Guid id, string label, string automationId, bool isChecked, string glyph = "", FilterGlyphTone tone = FilterGlyphTone.Default)
    {
        Id = id;
        Label = label;
        AutomationId = automationId;
        IsChecked = isChecked;
        Glyph = glyph;
        Tone = tone;
    }

    /// <summary>The stable lookup ID; <see cref="Guid.Empty"/> is "General" in a squad list.</summary>
    public Guid Id { get; }

    public string Label { get; }

    public string AutomationId { get; }

    /// <summary>The value's own icon (priority, status, date preset), or empty.</summary>
    public string Glyph { get; }

    public bool HasGlyph => Glyph.Length > 0 && !IsPerson;

    /// <summary>A person (CR Owner, Designer, Developer): the item shows the person's avatar before the name.</summary>
    public bool IsPerson { get; init; }

    public FilterGlyphTone Tone { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    internal void Attach(Action<FilterChoiceViewModel> toggled) => _toggled = toggled;

    /// <summary>Shows a state without raising the toggle callback (single-choice sync and Clear all).</summary>
    internal void SetQuietly(bool value)
    {
        var toggled = _toggled;
        _toggled = null;
        IsChecked = value;
        _toggled = toggled;
    }

    partial void OnIsCheckedChanged(bool value) => _toggled?.Invoke(this);
}

/// <summary>
/// A multi-select list: Status, Priority, Squad, CR Owner, Designer, Developer, Tags, or a Lookup custom field. Values
/// within it are OR. Tags add the Any/All selected choice (B-10).
/// </summary>
public sealed partial class FilterChoiceGroupViewModel : ObservableObject, IFilterEditor
{
    private readonly Action _changed;

    internal FilterChoiceGroupViewModel(string key, IReadOnlyList<FilterChoiceViewModel> choices, Action changed, bool showsMatchMode = false, bool matchAll = false)
    {
        Key = key;
        Choices = choices;
        ShowsMatchMode = showsMatchMode;
        MatchModeIndex = matchAll ? 1 : 0;
        _changed = changed;
        foreach (var choice in choices)
        {
            choice.Attach(_ => _changed());
        }
    }

    public string Key { get; }

    public IReadOnlyList<FilterChoiceViewModel> Choices { get; }

    /// <summary>Nothing to choose from (for example no tags yet); the submenu says so.</summary>
    public bool IsEmpty => Choices.Count == 0;

    public bool ShowsMatchMode { get; }

    /// <summary>0 = Any selected, 1 = All selected (RadioButtons.SelectedIndex); other writes are ignored.</summary>
    [ObservableProperty]
    public partial int MatchModeIndex { get; set; }

    public bool MatchAll => MatchModeIndex == 1;

    public IReadOnlyList<Guid> CheckedIds => [.. Choices.Where(c => c.IsChecked).Select(c => c.Id)];

    public bool IsSet => Choices.Any(c => c.IsChecked);

    public void Clear()
    {
        foreach (var choice in Choices)
        {
            choice.SetQuietly(false);
        }

        MatchModeIndex = 0;
    }

    partial void OnMatchModeIndexChanged(int oldValue, int newValue)
    {
        if (newValue is < 0 or > 1)
        {
            // A RadioButtons control reports -1 while its items are replaced; that never changes the mode.
            MatchModeIndex = oldValue;
            return;
        }

        // Restoring the mode after an ignored write (oldValue -1) changes nothing, so it does not commit.
        if (oldValue is 0 or 1 && IsSet)
        {
            _changed?.Invoke();
        }
    }
}

/// <summary>
/// A "contains" box: Service or a Text, Multiline or URL custom field. It applies on Enter, when the box loses focus,
/// or when the menu closes (<see cref="Commit"/>), not on every keystroke.
/// </summary>
public sealed partial class FilterTextFieldViewModel : ObservableObject, IFilterEditor
{
    private readonly Action _changed;

    internal FilterTextFieldViewModel(string key, string? text, Action changed)
    {
        Key = key;
        Text = text ?? string.Empty;
        CommittedText = Text.Trim();
        _changed = changed;
    }

    public string Key { get; }

    public string AutomationId => "Filters_" + Key + "_ContainsBox";

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>The trimmed text the scope has; it changes only through <see cref="Commit"/>.</summary>
    public string CommittedText { get; private set; }

    public bool IsSet => CommittedText.Length > 0;

    /// <summary>Applies the typed text when it differs from the applied one.</summary>
    public void Commit()
    {
        var text = Text.Trim();
        if (text != CommittedText)
        {
            CommittedText = text;
            _changed();
        }
    }

    public void Clear()
    {
        Text = string.Empty;
        CommittedText = string.Empty;
    }
}

/// <summary>Min/Max for a Number custom field (NumberBox uses NaN for empty). An inverted range shows its error and is not applied.</summary>
public sealed partial class FilterNumberFieldViewModel : ObservableObject, IFilterEditor
{
    private readonly Action _changed;
    private readonly string _invertedError;

    internal FilterNumberFieldViewModel(string key, NumberRangeFilter? range, string invertedError, Action changed)
    {
        Key = key;
        _invertedError = invertedError;
        Min = range?.Min is { } min ? (double)min : double.NaN;
        Max = range?.Max is { } max ? (double)max : double.NaN;
        _changed = changed;
    }

    public string Key { get; }

    public string MinAutomationId => "Filters_" + Key + "_Min";

    public string MaxAutomationId => "Filters_" + Key + "_Max";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    public partial double Min { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    public partial double Max { get; set; }

    public NumberRangeFilter Range => new(Value(Min), Value(Max));

    public bool HasError => Range.IsInverted;

    public string Error => HasError ? _invertedError : string.Empty;

    public bool IsSet => Range.IsSet && !HasError;

    public void Clear()
    {
        Min = double.NaN;
        Max = double.NaN;
    }

    partial void OnMinChanged(double value) => _changed?.Invoke();

    partial void OnMaxChanged(double value) => _changed?.Invoke();

    private static decimal? Value(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? null : (decimal)Math.Clamp(value, (double)decimal.MinValue / 2, (double)decimal.MaxValue / 2);
}

/// <summary>A CR search result (design §2.6).</summary>
public sealed record FilterCrChoice(CrLinkFilter Filter, string Display)
{
    public override string ToString() => Display;
}

/// <summary>The task CR field: Any CR and No CR (one of them, or a found CR), plus a search box over the current CRs.</summary>
public sealed partial class FilterCrFieldViewModel : ObservableObject, IFilterEditor
{
    private readonly Func<string, IReadOnlyList<FilterCrChoice>> _suggest;
    private readonly Action _changed;

    internal FilterCrFieldViewModel(FilterCrChoice selected, string anyLabel, string noneLabel, Func<string, IReadOnlyList<FilterCrChoice>> suggest, Action changed)
    {
        _suggest = suggest;
        _changed = changed;
        AnyChoice = new FilterChoiceViewModel(Guid.Empty, anyLabel, "Filters_Cr_Any", false);
        NoneChoice = new FilterChoiceViewModel(Guid.Empty, noneLabel, "Filters_Cr_None", false);
        Selected = selected;
        Show(selected);
        AnyChoice.Attach(OnAnyToggled);
        NoneChoice.Attach(OnNoneToggled);
    }

    public string Key => FilterKeys.Cr;

    public FilterChoiceViewModel AnyChoice { get; }

    public FilterChoiceViewModel NoneChoice { get; }

    public ObservableCollection<FilterCrChoice> Suggestions { get; } = [];

    /// <summary>The search box text: the chosen CR, or empty for Any CR and No CR.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial FilterCrChoice Selected { get; private set; }

    public bool IsSet => Selected.Filter.IsSet;

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
        Show(choice);
        _changed();
    }

    /// <summary>
    /// Enter in the search box: the chosen suggestion, else the first suggestion containing the typed text. Empty text
    /// with no chosen suggestion changes nothing.
    /// </summary>
    public void Submit(string? query, FilterCrChoice? chosen)
    {
        if (chosen is not null)
        {
            Choose(chosen);
        }
        else if (!string.IsNullOrWhiteSpace(query)
            && Suggestions.FirstOrDefault(s => s.Display.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)) is { } best)
        {
            Choose(best);
        }
    }

    public void Clear() => Show(new FilterCrChoice(CrLinkFilter.Any, AnyChoice.Label));

    private void Show(FilterCrChoice choice)
    {
        Selected = choice;
        Text = choice.Filter.Mode == CrLinkMode.Specific ? choice.Display : string.Empty;
        AnyChoice.SetQuietly(choice.Filter.Mode == CrLinkMode.Any);
        NoneChoice.SetQuietly(choice.Filter.Mode == CrLinkMode.NoCr);
    }

    // Any CR cannot be unchecked by itself: it is what "no CR filter" looks like.
    private void OnAnyToggled(FilterChoiceViewModel any)
    {
        if (any.IsChecked)
        {
            Choose(new FilterCrChoice(CrLinkFilter.Any, AnyChoice.Label));
        }
        else if (Selected.Filter.Mode == CrLinkMode.Any)
        {
            any.SetQuietly(true);
        }
    }

    private void OnNoneToggled(FilterChoiceViewModel none) =>
        Choose(none.IsChecked ? new FilterCrChoice(CrLinkFilter.NoCr, NoneChoice.Label) : new FilterCrChoice(CrLinkFilter.Any, AnyChoice.Label));
}

/// <summary>A top-level row of the filter menu: icon, label and chevron; it opens its editor as a submenu.</summary>
public sealed partial class FilterMenuRowViewModel : ObservableObject
{
    private readonly ILocalizedStrings _strings;

    internal FilterMenuRowViewModel(IFilterEditor editor, string label, string glyph, ILocalizedStrings strings)
    {
        Editor = editor;
        Label = label;
        Glyph = glyph;
        _strings = strings;
        IsSet = editor.IsSet;
    }

    public IFilterEditor Editor { get; }

    public string Key => Editor.Key;

    public string Label { get; }

    public string Glyph { get; }

    public string AutomationId => "Filters_Row_" + Key;

    /// <summary>The field narrows the records: the row shows an accent dot and says so in its name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool IsSet { get; private set; }

    public string AccessibleName => IsSet ? _strings.Format("Filters_RowSetName", Label) : Label;

    internal void Refresh() => IsSet = Editor.IsSet;
}

/// <summary>
/// The Filter by menu (design §2.6, owner decision 2026-11-07): one row per field, each with a submenu. There is no
/// draft: every check, choice, committed text or range commits the whole menu to the scope at once, which persists it.
/// Clear all clears every field with one commit. Use on the UI thread.
/// </summary>
public sealed partial class FilterMenuViewModel : ObservableObject
{
    private readonly ILocalizedStrings _strings;
    private readonly Action<FilterMenuViewModel> _commit;
    private bool _isBuilding = true;

    internal FilterMenuViewModel(ILocalizedStrings strings, Action<FilterMenuViewModel> commit)
    {
        _strings = strings;
        _commit = commit;
    }

    /// <summary>
    /// The scope changed from outside the menu (Clear all filters, a chip, a drill-down, a removed lookup): this menu
    /// is stale and closes, so a later toggle cannot write its old fields back.
    /// </summary>
    public event EventHandler? Detached;

    public List<FilterMenuRowViewModel> Rows { get; } = [];

    /// <summary>The menu no longer shows the applied filter; its changes are ignored.</summary>
    public bool IsDetached { get; private set; }

    /// <summary>A field is set; the Clear all link is enabled.</summary>
    [ObservableProperty]
    public partial bool CanClearAll { get; private set; }

    /// <summary>The callback every editor raises when its value changed.</summary>
    internal Action Changed => OnChanged;

    internal void Add(IFilterEditor editor, string label, string glyph) => Rows.Add(new FilterMenuRowViewModel(editor, label, glyph, _strings));

    internal T? Find<T>(string key)
        where T : class, IFilterEditor =>
        Rows.Select(r => r.Editor).OfType<T>().FirstOrDefault(e => e.Key == key);

    internal IEnumerable<T> All<T>()
        where T : IFilterEditor =>
        Rows.Select(r => r.Editor).OfType<T>();

    internal void EndBuild()
    {
        _isBuilding = false;
        Refresh();
    }

    internal void Detach()
    {
        if (!IsDetached)
        {
            IsDetached = true;
            Detached?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Applies text typed into any contains box (the menu or a submenu is closing).</summary>
    public void CommitPendingText()
    {
        foreach (var field in All<FilterTextFieldViewModel>())
        {
            field.Commit();
        }
    }

    /// <summary>Clear all (design §2.6): clears every field of the menu and commits once.</summary>
    [RelayCommand]
    private void ClearAll()
    {
        _isBuilding = true;
        foreach (var row in Rows)
        {
            row.Editor.Clear();
        }

        _isBuilding = false;
        OnChanged();
    }

    private void OnChanged()
    {
        if (_isBuilding || IsDetached)
        {
            return;
        }

        Refresh();
        _commit(this);
    }

    private void Refresh()
    {
        foreach (var row in Rows)
        {
            row.Refresh();
        }

        CanClearAll = Rows.Any(r => r.IsSet);
    }
}
