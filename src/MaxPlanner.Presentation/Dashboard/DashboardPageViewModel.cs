using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Dashboard;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Scheduling;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Dashboard;

/// <summary>
/// The Dashboard (B-15, AC-18–21, design §4.1): summary cards, three native bar charts, the Overdue CRs and Unlinked
/// tasks lists, and the Squad/Priority/CR Owner filters. Every value comes from <see cref="DashboardCalculator"/> over
/// one consistent store state and carries the drill-down that shows exactly what it counts. Use on the UI thread.
/// </summary>
public sealed partial class DashboardPageViewModel : ObservableObject, IPageViewModel
{
    private const string PreferenceKey = "dashboard";

    private readonly WorkspaceContext _context;
    private readonly BehaviorSubject<DashboardFilter> _filter;
    private readonly IDisposable _results;
    private readonly IDisposable _state;
    private readonly IDisposable _workflowSubscription;
    private readonly ObservableCollection<DashboardOption> _squads = [];
    private readonly ObservableCollection<DashboardOption> _priorities = [];
    private readonly ObservableCollection<DashboardOption> _owners = [];
    private readonly ObservableCollection<OverdueRowViewModel> _overdue = [];
    private readonly ObservableCollection<UnlinkedRowViewModel> _unlinked = [];
    private readonly DashboardLinkViewModel _doneThisWeek;
    private readonly DashboardLinkViewModel _doneThisMonth;
    private DashboardResult? _result;
    private bool _isSyncingOptions;

    public DashboardPageViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var strings = context.Strings;
        _filter = new BehaviorSubject<DashboardFilter>(LoadFilter(context.Preferences.Current));
        SquadOptions = new ReadOnlyObservableCollection<DashboardOption>(_squads);
        PriorityOptions = new ReadOnlyObservableCollection<DashboardOption>(_priorities);
        OwnerOptions = new ReadOnlyObservableCollection<DashboardOption>(_owners);
        OverdueRows = new ReadOnlyObservableCollection<OverdueRowViewModel>(_overdue);
        UnlinkedRows = new ReadOnlyObservableCollection<UnlinkedRowViewModel>(_unlinked);

        ActiveTasks = Card("Dashboard_ActiveTasksCard", "Dashboard_ActiveTasks");
        CompletedTasks = Card("Dashboard_CompletedTasksCard", "Dashboard_CompletedTasks");
        UnscheduledTasks = Card("Dashboard_UnscheduledTasksCard", "Dashboard_UnscheduledTasks");
        OverdueCrs = Card("Dashboard_OverdueCrsCard", "Dashboard_OverdueCrs");
        ActiveSubtaskCards = Card("Dashboard_ActiveSubtaskCardsCard", "Dashboard_ActiveSubtaskCards");
        Reviews = Card("Dashboard_ReviewsCard", "Dashboard_Reviews");
        _doneThisWeek = new DashboardLinkViewModel("Dashboard_ReviewsWeekLink", new RelayCommand(() => DrillDown(_result?.Reviews.DoneThisWeek.DrillDown, strings.GetString("Dashboard_DrillReviewsWeek"))));
        _doneThisMonth = new DashboardLinkViewModel("Dashboard_ReviewsMonthLink", new RelayCommand(() => DrillDown(_result?.Reviews.DoneThisMonth.DrillDown, strings.GetString("Dashboard_DrillReviewsMonth"))));
        Reviews.Links.Add(_doneThisWeek);
        Reviews.Links.Add(_doneThisMonth);
        Cards = [ActiveTasks, CompletedTasks, UnscheduledTasks, OverdueCrs, ActiveSubtaskCards, Reviews];

        StatusChart = new BarChartViewModel(
            strings, strings.GetString("Dashboard_StatusChartTitle"), string.Empty, "Dashboard_StatusBar_", "Dashboard_UnitTask", "Dashboard_OpensGrid", "Dashboard_DrillStatus", OnBarDrillDown);
        SquadChart = new BarChartViewModel(
            strings, strings.GetString("Dashboard_SquadChartTitle"), strings.GetString("Dashboard_SquadFootnote"), "Dashboard_SquadBar_", "Dashboard_UnitTask", "Dashboard_OpensGrid", "Dashboard_DrillSquad", OnBarDrillDown);
        ReviewChart = new BarChartViewModel(
            strings, strings.GetString("Dashboard_ReviewChartTitle"), strings.GetString("Dashboard_ReviewFootnote"), "Dashboard_ReviewDevBar_", "Dashboard_UnitReview", "Dashboard_OpensReviews", "Dashboard_DrillReviewDeveloper", OnBarDrillDown);

        var store = context.Store;
        var labels = new DashboardLabels(strings.GetString("Squad_General"), strings.GetString("Dashboard_Unassigned"));

        // ADR 0010: compute on the store scheduler, where the caches are consistent after each batch notification,
        // then show the result on the UI thread. A day change arrives as a batch too, so overdue CRs and the review
        // week/month counts refresh together at midnight, on resume, and on a time-zone change (AC-21).
        // T-11: a burst of batches (import, resync, quick successive commits) is coalesced into one recompute of the
        // newest state rather than one per batch; a single commit still recomputes immediately.
        _results = store.Committed.Select(_ => Unit.Default)
            .StartWith(Unit.Default)
            .SubscribeOn(store.Scheduler)
            .CombineLatest(_filter.ObserveOn(store.Scheduler), (_, filter) => filter)
            .CoalesceLatest(store.Scheduler)
            .Select(filter => (Result: store.CurrentWorkflow is { } workflow ? Compute(store, workflow, filter, labels) : null, store.Today))
            .Where(r => r.Result is not null)
            .ObserveOn(context.UiScheduler)
            .Subscribe(r => Apply(r.Result!, r.Today));
        _state = store.State.ObserveOn(context.UiScheduler).Subscribe(OnStoreState);
        _workflowSubscription = store.Workflow.ObserveOn(context.UiScheduler).Subscribe(OnWorkflow);
    }

    public IReadOnlyList<DashboardCardViewModel> Cards { get; }

    public DashboardCardViewModel ActiveTasks { get; }

    public DashboardCardViewModel CompletedTasks { get; }

    public DashboardCardViewModel UnscheduledTasks { get; }

    public DashboardCardViewModel OverdueCrs { get; }

    public DashboardCardViewModel ActiveSubtaskCards { get; }

    public DashboardCardViewModel Reviews { get; }

    public BarChartViewModel StatusChart { get; }

    public BarChartViewModel SquadChart { get; }

    public BarChartViewModel ReviewChart { get; }

    public ReadOnlyObservableCollection<DashboardOption> SquadOptions { get; }

    public ReadOnlyObservableCollection<DashboardOption> PriorityOptions { get; }

    public ReadOnlyObservableCollection<DashboardOption> OwnerOptions { get; }

    public ReadOnlyObservableCollection<OverdueRowViewModel> OverdueRows { get; }

    public ReadOnlyObservableCollection<UnlinkedRowViewModel> UnlinkedRows { get; }

    [ObservableProperty]
    public partial DashboardOption? SelectedSquad { get; set; }

    [ObservableProperty]
    public partial DashboardOption? SelectedPriority { get; set; }

    [ObservableProperty]
    public partial DashboardOption? SelectedOwner { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearFiltersCommand))]
    public partial bool IsFiltered { get; private set; }

    [ObservableProperty]
    public partial ViewState State { get; private set; } = ViewState.Loading;

    /// <summary>No tasks and no CRs at all (design §4.1 Empty: "Start by creating a CR or a task").</summary>
    [ObservableProperty]
    public partial bool IsDatasetEmpty { get; private set; }

    [ObservableProperty]
    public partial bool HasData { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>"Wednesday, October 21, 2026 · non-archived records only" (design §4.1).</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string OverdueCaption { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string UnlinkedCaption { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasOverdue { get; private set; }

    [ObservableProperty]
    public partial bool HasUnlinked { get; private set; }

    /// <summary>"No overdue CRs", or "No records match these filters" while filtered (design §4.1 Filtered-empty).</summary>
    [ObservableProperty]
    public partial string OverdueEmptyText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string UnlinkedEmptyText { get; private set; } = string.Empty;

    /// <summary>The page scroll offset, kept across a drill-down and restored on Back (AC-20).</summary>
    public double ScrollOffset { get; set; }

    public void OpenOverdue(OverdueRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _ = _context.ChangeRequests.OpenAsync(new ChangeRequestId(row.Id));
    }

    public void OpenUnlinked(UnlinkedRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _ = _context.Tasks.OpenAsync(new TaskId(row.Id));
    }

    public void Dispose()
    {
        _results.Dispose();
        _state.Dispose();
        _workflowSubscription.Dispose();
        _filter.Dispose();
    }

    [RelayCommand(CanExecute = nameof(IsFiltered))]
    private void ClearFilters()
    {
        _isSyncingOptions = true;
        SelectedSquad = _squads.FirstOrDefault();
        SelectedPriority = _priorities.FirstOrDefault();
        SelectedOwner = _owners.FirstOrDefault();
        _isSyncingOptions = false;
        PushFilter();
    }

    [RelayCommand]
    private Task NewChangeRequestAsync() => _context.ChangeRequests.NewAsync();

    [RelayCommand]
    private Task NewTaskAsync() => _context.Tasks.NewAsync();

    [RelayCommand]
    private void ViewAllOverdue() => DrillDown(_result?.OverdueList.ViewAll, _context.Strings.GetString("Dashboard_OverdueCrs"));

    [RelayCommand]
    private void ViewAllUnlinked() => DrillDown(_result?.UnlinkedList.ViewAll, _context.Strings.GetString("Dashboard_UnlinkedTitle"));

    partial void OnSelectedSquadChanged(DashboardOption? value) => PushFilter();

    partial void OnSelectedPriorityChanged(DashboardOption? value) => PushFilter();

    partial void OnSelectedOwnerChanged(DashboardOption? value) => PushFilter();

    private static DashboardResult Compute(WorkspaceProjectionStore store, WorkflowSnapshot workflow, DashboardFilter filter, DashboardLabels labels) =>
        DashboardCalculator.Calculate(
            new DashboardInput([.. store.Tasks.Items], [.. store.SubtaskCards.Items], [.. store.ChangeRequests.Items], [.. store.ReviewItems.Items], workflow),
            filter,
            store.Today,
            labels);

    private static DashboardFilter LoadFilter(UiPreferences preferences)
    {
        var fields = preferences.ViewFilters?.GetValueOrDefault(PreferenceKey)?.Fields;
        Guid? Read(string key) => fields?.GetValueOrDefault(key) is [var value, ..] && Guid.TryParse(value, out var id) ? id : null;
        var squad = fields?.GetValueOrDefault("squad") is ["general", ..] ? new DashboardSquad(null)
            : Read("squad") is { } squadId ? new DashboardSquad(new SquadId(squadId)) : null;
        return new DashboardFilter(
            squad,
            Read("priority") is { } priority ? new PriorityId(priority) : null,
            Read("owner") is { } owner ? new PersonId(owner) : null);
    }

    private DashboardCardViewModel Card(string automationId, string labelKey) =>
        new(automationId, _context.Strings.GetString(labelKey), card => DrillDown(card.DrillDown, card.DrillLabel));

    private void OnBarDrillDown(BarRowViewModel row) => DrillDown(row.DrillDown, row.DrillLabel);

    /// <summary>B-15.6, design §2.9: task metrics → Grid, Overdue CRs → Calendar › Records, reviews → Checklists › Reviews.</summary>
    private void DrillDown(DashboardDrillDown? drillDown, string label)
    {
        if (drillDown is not null)
        {
            _context.Navigator.RequestDrillDown(new DrillDownRequest(drillDown, label));
        }
    }

    private void PushFilter()
    {
        if (_isSyncingOptions)
        {
            return;
        }

        var filter = new DashboardFilter(
            SelectedSquad is { IsAll: false } squad ? new DashboardSquad(squad.Id is { } id ? new SquadId(id) : null) : null,
            SelectedPriority is { Id: { } priority } ? new PriorityId(priority) : null,
            SelectedOwner is { Id: { } owner } ? new PersonId(owner) : null);
        IsFiltered = filter.IsActive;
        if (filter == _filter.Value)
        {
            return;
        }

        _filter.OnNext(filter);
        var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (filter.Squad is { } s)
        {
            fields["squad"] = [s.Id?.Value.ToString("N", CultureInfo.InvariantCulture) ?? "general"];
        }

        if (filter.Priority is { } p)
        {
            fields["priority"] = [p.Value.ToString("N", CultureInfo.InvariantCulture)];
        }

        if (filter.Owner is { } o)
        {
            fields["owner"] = [o.Value.ToString("N", CultureInfo.InvariantCulture)];
        }

        _context.Preferences.Update(prefs => prefs with
        {
            ViewFilters = new Dictionary<string, ViewFilterPreference>(prefs.ViewFilters ?? new Dictionary<string, ViewFilterPreference>(), StringComparer.Ordinal)
            {
                [PreferenceKey] = new ViewFilterPreference(null, fields),
            },
        });
    }

    /// <summary>
    /// Lookups are configurable (B-04), so the options follow the workflow; a saved choice that no longer exists falls
    /// back to "All" (B-10: never hide every record because of a removed value).
    /// </summary>
    private void OnWorkflow(WorkflowSnapshot workflow)
    {
        var strings = _context.Strings;
        var current = _filter.Value;
        _isSyncingOptions = true;
        Replace(_squads, [new DashboardOption(null, strings.GetString("Dashboard_AllSquads")), .. workflow.Squads.OrderBy(s => s.Order).Select(s => new DashboardOption(s.Id.Value, s.Name.Display)), new DashboardOption(null, strings.GetString("Squad_General"), IsGeneral: true)]);
        Replace(_priorities, [new DashboardOption(null, strings.GetString("Dashboard_AllPriorities")), .. workflow.Priorities.OrderBy(p => p.Order).Select(p => new DashboardOption(p.Id.Value, p.Name.Display))]);
        Replace(_owners, [new DashboardOption(null, strings.GetString("Dashboard_AllOwners")), .. workflow.People.OrderBy(p => p.Name.Display, StringComparer.CurrentCultureIgnoreCase).Select(p => new DashboardOption(p.Id.Value, p.Name.Display))]);
        SelectedSquad = current.Squad is { } squad
            ? _squads.FirstOrDefault(o => squad.Id is { } id ? o.Id == id.Value : o.IsGeneral) ?? _squads[0]
            : _squads[0];
        SelectedPriority = _priorities.FirstOrDefault(o => current.Priority is { } p && o.Id == p.Value) ?? _priorities[0];
        SelectedOwner = _owners.FirstOrDefault(o => current.Owner is { } p && o.Id == p.Value) ?? _owners[0];
        _isSyncingOptions = false;
        PushFilter();
    }

    private static void Replace(ObservableCollection<DashboardOption> target, List<DashboardOption> options)
    {
        if (target.SequenceEqual(options))
        {
            return;
        }

        target.Clear();
        foreach (var option in options)
        {
            target.Add(option);
        }
    }

    private void OnStoreState(ProjectionState state)
    {
        // Loading and errors are distinct from a successful zero (B-15.7): an error before any result is the Error state.
        if (state.Status == ProjectionStatus.Faulted && _result is null)
        {
            State = ViewState.Error;
        }
    }

    private void Apply(DashboardResult result, DateOnly today)
    {
        _result = result;
        var s = _context.Strings;
        string Count(int value) => value.ToString(CultureInfo.CurrentCulture);
        Subtitle = s.Format("Dashboard_Subtitle", today.ToString("D", CultureInfo.CurrentCulture));

        SetCard(ActiveTasks, result.ActiveTasks, s.Format("Dashboard_ActiveCaption", Count(result.TotalTasks)), "Dashboard_OpensGrid", s.GetString("Dashboard_ActiveTasks"));
        SetCard(CompletedTasks, result.CompletedTasks, string.Empty, "Dashboard_OpensGrid", s.GetString("Dashboard_CompletedTasks"));
        SetCard(UnscheduledTasks, result.UnscheduledTasks, s.GetString("Dashboard_UnscheduledCaption"), "Dashboard_OpensGrid", s.GetString("Dashboard_UnscheduledTasks"));
        SetCard(OverdueCrs, result.OverdueCrs, s.GetString("Dashboard_OverdueCaption"), "Dashboard_OpensRecords", s.GetString("Dashboard_OverdueCrs"));
        OverdueCrs.HasBadge = result.OverdueCrs.Count > 0;
        SetCard(ActiveSubtaskCards, result.ActiveSubtaskCards, s.GetString("Dashboard_ActiveCardsCaption"), "Dashboard_OpensGrid", s.GetString("Dashboard_ActiveSubtaskCards"));
        SetCard(Reviews, result.Reviews.Open, s.Format("Dashboard_ReviewsCaption", Count(result.Reviews.DoneThisWeek.Count), Count(result.Reviews.DoneThisMonth.Count)), "Dashboard_OpensReviews", s.GetString("Dashboard_DrillOpenReviews"));
        _doneThisWeek.Text = s.Format("Dashboard_ReviewsWeekLink", Count(result.Reviews.DoneThisWeek.Count));
        _doneThisMonth.Text = s.Format("Dashboard_ReviewsMonthLink", Count(result.Reviews.DoneThisMonth.Count));

        StatusChart.Update(result.TasksByStatus);
        SquadChart.Update(result.TasksBySquad);
        ReviewChart.Update(result.OpenReviewsByDeveloper);

        ReplaceRows(_overdue, [.. result.OverdueList.Preview.Select(r => new OverdueRowViewModel(r, s))]);
        ReplaceRows(_unlinked, [.. result.UnlinkedList.Preview.Select(t => new UnlinkedRowViewModel(t, s))]);
        HasOverdue = _overdue.Count > 0;
        HasUnlinked = _unlinked.Count > 0;
        OverdueCaption = s.Format("Dashboard_ShowingOf", Count(_overdue.Count), Count(result.OverdueList.TotalCount));
        UnlinkedCaption = s.Format("Dashboard_ShowingOf", Count(_unlinked.Count), Count(result.UnlinkedList.TotalCount));
        OverdueEmptyText = s.GetString(IsFiltered ? "Dashboard_NoFilteredRecords" : "Dashboard_NoOverdue");
        UnlinkedEmptyText = s.GetString(IsFiltered ? "Dashboard_NoFilteredRecords" : "Dashboard_NoUnlinked");

        // Design §4.1 Empty offers New CR and New task, so the page shows its own empty panel instead of the StateView's
        // single action; zeros are shown only after the snapshot loaded (B-15.7).
        IsDatasetEmpty = result.IsDatasetEmpty;
        HasData = !result.IsDatasetEmpty;
        State = ViewState.Populated;
        Summary = s.Format("Dashboard_Summary", Count(result.ActiveTasks.Count), Count(result.TotalTasks), Count(result.OverdueCrs.Count), Count(result.Reviews.Open.Count));
    }

    private void SetCard(DashboardCardViewModel card, DashboardMetric metric, string caption, string opensKey, string drillLabel)
    {
        var s = _context.Strings;
        card.Value = metric.Count.ToString(CultureInfo.CurrentCulture);
        card.Caption = caption;
        card.DrillLabel = drillLabel;
        card.SetDrillDown(metric.DrillDown);
        card.AccessibleName = string.Join(", ", new[] { card.Label, card.Value, caption }.Where(p => p.Length > 0)) + ". " + s.GetString(opensKey);
    }

    // The lists show at most ten rows and are rebuilt per result; rows are immutable snapshots of one result.
    private static void ReplaceRows<T>(ObservableCollection<T> target, List<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }
}
