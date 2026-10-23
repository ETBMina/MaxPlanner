using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Shell;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Common.Filters;

/// <summary>An active-filter chip (design §2.6): "Squad: Retail Cards", removable, named "Remove filter Squad: Retail Cards".</summary>
public sealed class FilterChipViewModel
{
    private readonly Action _remove;

    internal FilterChipViewModel(string key, string label, string removeName, bool isDashboard, Action remove)
    {
        Key = key;
        Label = label;
        RemoveName = removeName;
        IsDashboard = isDashboard;
        _remove = remove;
    }

    /// <summary>The field key, the AutomationId suffix <c>{Page}_FilterChip_{Key}</c> (design §8.1).</summary>
    public string Key { get; }

    public string Label { get; }

    public string RemoveName { get; }

    /// <summary>The non-editable "From Dashboard" chip; removing it restores the page's previous filters (design §2.9).</summary>
    public bool IsDashboard { get; }

    public void Remove() => _remove();
}

/// <summary>
/// The page-facing part of one filter scope (B-10): search text, chips, the Filters badge count, the draft flyout,
/// Clear all, the invalid-saved-filter notice, and the Dashboard drill-down context (design §2.6, §2.9). The
/// shared controls bind to it. Use on the UI thread.
/// </summary>
public abstract partial class FilterScopeViewModel : ObservableObject, IDisposable
{
    private protected FilterScopeViewModel(WorkspaceContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Chips = new ReadOnlyObservableCollection<FilterChipViewModel>(ChipList);
    }

    /// <summary>Raised after the applied filter changed; pages re-evaluate their membership.</summary>
    public event EventHandler? Changed;

    public ReadOnlyObservableCollection<FilterChipViewModel> Chips { get; }

    /// <summary>The fields set (search excluded), for the Filters InfoBadge (design §2.3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFields), nameof(FiltersButtonName))]
    public partial int ActiveFieldCount { get; private set; }

    public bool HasActiveFields => ActiveFieldCount > 0;

    /// <summary>"Filters, 3 fields set" (the badge is in the accessible name, design §8.2).</summary>
    public string FiltersButtonName => ActiveFieldCount == 0
        ? Context.Strings.GetString("Filters_ButtonName")
        : Context.Strings.Format("Filters_ButtonNameCount", ActiveFieldCount);

    /// <summary>Search, any field, or a Dashboard context narrows the records.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChips))]
    public partial bool IsFiltered { get; private set; }

    public bool HasChips => IsFiltered;

    /// <summary>The page search box: applied immediately (outside the draft) and persisted with the scope.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsInvalidNoticeOpen { get; set; }

    [ObservableProperty]
    public partial string InvalidNoticeMessage { get; private set; } = string.Empty;

    /// <summary>A Dashboard drill-down replaced this scope's filters (design §2.9).</summary>
    [ObservableProperty]
    public partial bool IsFromDashboard { get; private set; }

    /// <summary>"Showing Dashboard results: Active tasks · Squad: Retail Cards".</summary>
    [ObservableProperty]
    public partial string DashboardMessage { get; private set; } = string.Empty;

    /// <summary>"Filter tasks — Board and Grid" or "Filter CRs — Calendar" (the flyout header).</summary>
    public abstract string ScopeTitle { get; }

    /// <summary>The page a drill-down into this scope lands on (Grid or Calendar).</summary>
    internal abstract AppPage Destination { get; }

    protected WorkspaceContext Context { get; }

    private protected ObservableCollection<FilterChipViewModel> ChipList { get; } = [];

    /// <summary>A fresh draft from the applied filter (Filters flyout opening).</summary>
    public abstract FilterDraftViewModel CreateDraft();

    /// <summary>Apply: the draft becomes the applied filter; the search text and Dashboard context are kept.</summary>
    public abstract void Apply(FilterDraftViewModel draft);

    /// <summary>Removing the "From Dashboard" chip, or Return/Back: the page's previous filters come back.</summary>
    public abstract void EndDashboardContext();

    public abstract void Dispose();

    /// <summary>Clear all filters (B-10): immediate, outside the flyout; it also ends a Dashboard context.</summary>
    [RelayCommand]
    public abstract void ClearAll();

    /// <summary>The InfoBar's Return to Dashboard (design §2.9).</summary>
    [RelayCommand]
    private void ReturnToDashboard() => Context.Navigator.Return(Destination);

    private protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private protected void SetDashboardState(bool isFromDashboard, string message)
    {
        IsFromDashboard = isFromDashboard;
        DashboardMessage = message;
    }

    private protected void SetSummary(int fieldCount, bool isFiltered)
    {
        ActiveFieldCount = fieldCount;
        IsFiltered = isFiltered;
    }

    private protected void ShowInvalid(IReadOnlyList<RemovedFilterKind> removed)
    {
        if (removed.Count == 0)
        {
            return;
        }

        var kinds = string.Join(", ", removed.Select(k => Context.Strings.GetString("Filters_Kind_" + k)));
        InvalidNoticeMessage = Context.Strings.Format("Filters_InvalidRemoved", kinds);
        IsInvalidNoticeOpen = true;
    }
}

/// <summary>
/// The typed scope: the applied spec, the spec saved before a Dashboard drill-down replaced it, persistence per
/// scope in <c>configuration.json</c>, and the startup check for saved values that no longer resolve (B-10).
/// </summary>
public abstract class FilterScopeViewModel<TSpec> : FilterScopeViewModel
    where TSpec : class
{
    private readonly IDisposable _workflowSubscription;
    private TSpec? _beforeDashboard;
    private string _dashboardLabel = string.Empty;
    private bool _isSettingSearch;

    private protected FilterScopeViewModel(WorkspaceContext context, string preferenceKey)
        : base(context)
    {
        PreferenceKey = preferenceKey;
        Spec = Load(context.Preferences.Current.ViewFilters?.GetValueOrDefault(preferenceKey));
        _isSettingSearch = true;
        SearchText = SearchOf(Spec);
        _isSettingSearch = false;
        _workflowSubscription = context.Store.Workflow.ObserveOn(context.UiScheduler).Subscribe(OnWorkflow);
        Refresh();
    }

    /// <summary>The applied filter. Immutable; replaced as a whole on every change.</summary>
    public TSpec Spec { get; private set; }

    protected string PreferenceKey { get; }

    protected WorkflowSnapshot? Workflow { get; private set; }

    /// <summary>
    /// B-10, design §2.9: a Dashboard drill-down replaces the filters (never intersects them with hidden old ones). The
    /// previous filters are kept for the "From Dashboard" chip, Return, and Back. A second drill-down into the same
    /// scope keeps the original previous filters.
    /// </summary>
    public void ReplaceWithDashboard(TSpec spec, string label)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!IsFromDashboard)
        {
            _beforeDashboard = Spec;
        }

        _dashboardLabel = label;
        SetSpec(spec);
    }

    public override void EndDashboardContext()
    {
        if (!IsFromDashboard || _beforeDashboard is not { } previous)
        {
            return;
        }

        _beforeDashboard = null;
        Context.Navigator.Forget(Destination);
        SetSpec(previous);
    }

    public override void ClearAll()
    {
        if (IsFromDashboard)
        {
            _beforeDashboard = null;
            Context.Navigator.Forget(Destination);
        }

        SetSpec(Empty);
    }

    public override void Apply(FilterDraftViewModel draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.CanApply)
        {
            SetSpec(FromDraft(draft, Spec));

            // Design §8.3: "Filters applied"; the page's own summary carries the counts.
            Context.Announce(Context.Strings.GetString("Filters_Applied"));
        }
    }

    public override void Dispose()
    {
        _workflowSubscription.Dispose();
        GC.SuppressFinalize(this);
    }

    private protected abstract TSpec Empty { get; }

    private protected abstract TSpec Load(ViewFilterPreference? preference);

    private protected abstract ViewFilterPreference Save(TSpec spec);

    private protected abstract string SearchOf(TSpec spec);

    private protected abstract TSpec WithSearch(TSpec spec, string search);

    private protected abstract int FieldCount(TSpec spec);

    private protected abstract bool IsActive(TSpec spec);

    /// <summary>The applied spec without one field (a chip's remove).</summary>
    private protected abstract TSpec Without(TSpec spec, string key);

    /// <summary>Field chips in display order as (key, label) pairs; search and Dashboard chips are added here.</summary>
    private protected abstract IEnumerable<(string Key, string Label)> FieldChips(TSpec spec);

    private protected abstract TSpec FromDraft(FilterDraftViewModel draft, TSpec current);

    private protected abstract (TSpec Spec, IReadOnlyList<RemovedFilterKind> Removed) Clean(TSpec spec, WorkflowSnapshot workflow);

    private protected void SetSpec(TSpec spec)
    {
        Spec = spec;
        _isSettingSearch = true;
        SearchText = SearchOf(spec);
        _isSettingSearch = false;
        Refresh();

        // The drill-down context is never persisted: a restart shows the page's own filters (B-10).
        var persisted = IsFromDashboard && _beforeDashboard is { } before ? before : spec;
        var saved = Save(persisted);
        Context.Preferences.Update(p => p with
        {
            ViewFilters = new Dictionary<string, ViewFilterPreference>(p.ViewFilters ?? new Dictionary<string, ViewFilterPreference>(), StringComparer.Ordinal)
            {
                [PreferenceKey] = saved,
            },
        });
        RaiseChanged();
    }

    private protected string Names<T>(IEnumerable<T> values, Func<T, string?> name) =>
        string.Join(", ", values.Select(v => name(v) ?? Context.Strings.GetString("Filters_Unknown")));

    private void OnWorkflow(WorkflowSnapshot workflow)
    {
        Workflow = workflow;
        var (cleaned, removed) = Clean(Spec, workflow);
        if (_beforeDashboard is { } before)
        {
            var (cleanedBefore, removedBefore) = Clean(before, workflow);
            _beforeDashboard = cleanedBefore;
            removed = [.. removed.Union(removedBefore)];
        }

        ShowInvalid(removed);
        if (removed.Count > 0)
        {
            SetSpec(cleaned);
        }
        else
        {
            // Lookup renames change chip text only.
            Refresh();
        }
    }

    /// <summary>The search box applies immediately (B-10: the draft covers the flyout fields only).</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(SearchText) && !_isSettingSearch && SearchText != SearchOf(Spec))
        {
            SetSpec(WithSearch(Spec, SearchText));
        }
    }

    private void Refresh()
    {
        var inContext = _beforeDashboard is not null;
        SetDashboardState(inContext, inContext ? DashboardText() : string.Empty);
        SetSummary(FieldCount(Spec), IsActive(Spec) || inContext);
        var chips = new List<FilterChipViewModel>();
        if (inContext)
        {
            var label = Context.Strings.Format("Filters_DashboardChip", _dashboardLabel);
            chips.Add(new FilterChipViewModel("Dashboard", label, Context.Strings.Format("Filters_RemoveChipName", label), true, EndDashboardContext));
        }

        if (SearchOf(Spec) is { Length: > 0 } search && search.Trim().Length > 0)
        {
            var label = Context.Strings.Format("Filters_SearchChip", search.Trim());
            chips.Add(new FilterChipViewModel("Search", label, Context.Strings.Format("Filters_RemoveChipName", label), false, () => SetSpec(WithSearch(Spec, string.Empty))));
        }

        foreach (var (key, label) in FieldChips(Spec))
        {
            chips.Add(new FilterChipViewModel(key, label, Context.Strings.Format("Filters_RemoveChipName", label), false, () => SetSpec(Without(Spec, key))));
        }

        ChipList.Clear();
        foreach (var chip in chips)
        {
            ChipList.Add(chip);
        }
    }

    private string DashboardText()
    {
        var parts = new List<string> { _dashboardLabel };
        parts.AddRange(FieldChips(Spec).Select(c => c.Label));
        return Context.Strings.Format("Filters_DashboardMessage", string.Join(" · ", parts));
    }
}
