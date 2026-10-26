using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Shell;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Checklists;

/// <summary>A task offered by "Add to task…" (B-09): its effective title and reference.</summary>
public sealed record TaskChoice(TaskId Id, string Title, string Secondary)
{
    public override string ToString() => Title;
}

/// <summary>
/// The Checklists page (B-09, B-17, design §4.5): one surface with the General to-dos card first, the pinned Reviews
/// card second, then one group per active task with open checklist items (newest parent first, then ID). Search
/// filters every card. Use on the UI thread.
/// </summary>
public sealed partial class ChecklistsPageViewModel : ObservableObject, IPageViewModel
{
    /// <summary>The fixed cards before the task groups in <see cref="Cards"/>: General to-dos, then Reviews.</summary>
    private const int PinnedCardCount = 2;
    private const string PreferenceKey = "checklists";

    private readonly WorkspaceContext _context;
    private readonly ProjectionView<TaskReadModel, TaskId, ChecklistGroupViewModel> _view;
    private readonly ObservableCollection<object> _cards = [];
    private readonly ChecklistCompletionTracker _completions = new();

    // B-09: groups kept shown while the user stays on the page although they have no open item (their last item
    // was checked here, or "Add to task…" chose them). Cleared when the page is left, so the next visit hides them.
    private readonly HashSet<TaskId> _keepVisible = [];

    private readonly IDisposable _commits;
    private string? _searchBeforeDashboard;
    private bool _isRevealReviewsPending;

    public ChecklistsPageViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        General = new GeneralTodosViewModel(context);
        Reviews = new ReviewsViewModel(context);
        _view = new ProjectionView<TaskReadModel, TaskId, ChecklistGroupViewModel>(
            context.Store.Tasks,
            context.Store.Scheduler,
            context.UiScheduler,
            CreateGroup,
            NewestFirst.Instance,
            GroupFilter(string.Empty));
        _view.Initialized += (_, _) => UpdateState();
        _cards.Add(General);
        _cards.Add(Reviews);
        Cards = new ReadOnlyObservableCollection<object>(_cards);
        ((INotifyCollectionChanged)_view.Items).CollectionChanged += OnGroupsChanged;

        // The store's batch notification is queued after the group updates of the same batch, so counts read
        // the updated groups.
        _commits = context.Store.Committed.ObserveOn(context.UiScheduler).Subscribe(_ => UpdateState());

        // B-10: Checklists keeps its own search scope, persisted independently of the task and CR scopes.
        if (context.Preferences.Current.ViewFilters?.GetValueOrDefault(PreferenceKey)?.SearchText is { Length: > 0 } saved)
        {
            SearchText = saved;
        }

        UpdateState();
    }

    /// <summary>Raised to focus an Add box: <see cref="GeneralTodosViewModel.GroupKey"/> or a task ID ("N" format).</summary>
    public event EventHandler<string>? FocusAddBoxRequested;

    /// <summary>The visible task groups (B-09: active tasks with open items, or kept visible).</summary>
    public ReadOnlyObservableCollection<ChecklistGroupViewModel> Groups => _view.Items;

    /// <summary>General to-dos, Reviews, then the task groups, in display order (design §4.5).</summary>
    public ReadOnlyObservableCollection<object> Cards { get; }

    public GeneralTodosViewModel General { get; }

    public ReviewsViewModel Reviews { get; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ViewState State { get; private set; } = ViewState.Loading;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Open items across General to-dos, active task groups and reviews (subtitle and nav badge).</summary>
    [ObservableProperty]
    public partial int OpenCount { get; private set; }

    /// <summary>B-10: "No items match 'abc'" is distinct from an empty card.</summary>
    [ObservableProperty]
    public partial bool HasNoMatches { get; private set; }

    [ObservableProperty]
    public partial string NoMatchesText { get; private set; } = string.Empty;

    /// <summary>A Dashboard review drill-down is shown (design §2.9).</summary>
    [ObservableProperty]
    public partial bool IsFromDashboard { get; private set; }

    /// <summary>"Showing Dashboard results: Open reviews · Developer: Sam".</summary>
    [ObservableProperty]
    public partial string DashboardMessage { get; private set; } = string.Empty;

    /// <summary>The removable "From Dashboard: Open reviews" chip text.</summary>
    [ObservableProperty]
    public partial string DashboardChipText { get; private set; } = string.Empty;

    public string DashboardChipName => _context.Strings.Format("Filters_RemoveChipName", DashboardChipText);

    /// <summary>
    /// B-09 group visibility: an active task shows while it has an open checklist item, or while kept visible for
    /// this visit; while searching, its title, service, CR or task number, or one of its items must also match.
    /// </summary>
    public static bool IsGroupVisible(TaskReadModel task, string query, IReadOnlySet<TaskId> keepVisible)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(keepVisible);
        return !task.IsArchived
            && (task.ChecklistDone < task.ChecklistTotal || keepVisible.Contains(task.Id))
            && (query.Length == 0
                || task.EffectiveTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (task.Service?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (task.Cr.CrNumber?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (task.Cr.TaskNumber?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || task.ChecklistItems.Any(i => i.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
    }

    /// <summary>
    /// Review drill-down (B-15.3): the Checklists page with the metric's review scope on the Reviews card, replacing
    /// the search; the previous search comes back when the context ends (design §2.9). The view brings the Reviews
    /// card into view when the page is shown (<see cref="TakeRevealReviews"/>).
    /// </summary>
    public void ShowDashboardReviews(ReviewFilterSpec spec, string label)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!IsFromDashboard)
        {
            _searchBeforeDashboard = SearchText;
        }

        IsFromDashboard = true;
        DashboardChipText = _context.Strings.Format("Filters_DashboardChip", label);
        DashboardMessage = _context.Strings.Format("Filters_DashboardMessage", label);
        OnPropertyChanged(nameof(DashboardChipName));
        SearchText = string.Empty;
        Reviews.ApplyDashboard(spec);
        _isRevealReviewsPending = true;
        UpdateState();
    }

    /// <summary>True once after a review drill-down: the view then scrolls the Reviews card into view.</summary>
    public bool TakeRevealReviews()
    {
        var pending = _isRevealReviewsPending;
        _isRevealReviewsPending = false;
        return pending;
    }

    /// <summary>Removing the "From Dashboard" chip, Return, or Back: the previous search is restored.</summary>
    public void EndDashboardContext()
    {
        if (!IsFromDashboard)
        {
            return;
        }

        IsFromDashboard = false;
        DashboardMessage = string.Empty;
        DashboardChipText = string.Empty;
        Reviews.ApplyDashboard(null);
        _context.Navigator.Forget(AppPage.Checklists);
        SearchText = _searchBeforeDashboard ?? string.Empty;
        _searchBeforeDashboard = null;
        UpdateState();
    }

    /// <summary>The page was left (the shell keeps it cached): groups kept visible for this visit are released.</summary>
    public void OnPageLeft()
    {
        if (_keepVisible.Count > 0)
        {
            _keepVisible.Clear();
            _view.SetFilter(GroupFilter(SearchText.Trim()));
        }
    }

    /// <summary>The "Add to task…" picker: active tasks whose title or reference contains the query.</summary>
    public IReadOnlyList<TaskChoice> TaskChoices(string query)
    {
        var q = (query ?? string.Empty).Trim();
        return [.. _context.Store.Tasks.Items
            .Where(t => !t.IsArchived)
            .Where(t => q.Length == 0
                || t.EffectiveTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || (t.Cr.CrNumber?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (t.Service?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(20)
            .Select(t => new TaskChoice(t.Id, t.EffectiveTitle, string.Join(" · ", new[] { t.Cr.CrNumber, t.Service, t.Status.Name }.Where(p => !string.IsNullOrEmpty(p)))))];
    }

    /// <summary>
    /// "Add to task…" (B-09): shows the task's group for this visit even with no open items, then focuses its Add
    /// box. The search is cleared so the group cannot be hidden by it.
    /// </summary>
    public void AddToTask(TaskId taskId)
    {
        _keepVisible.Add(taskId);
        if (SearchText.Length > 0)
        {
            SearchText = string.Empty;
        }
        else
        {
            ApplyFilter();
        }

        FocusAddBoxRequested?.Invoke(this, taskId.Value.ToString("N"));
    }

    public void Dispose()
    {
        _commits.Dispose();
        ((INotifyCollectionChanged)_view.Items).CollectionChanged -= OnGroupsChanged;
        _view.Dispose();
        General.Dispose();
        Reviews.Dispose();
    }

    /// <summary>Accent "New to-do" (Ctrl+N in Checklists): focuses the General to-dos Add box (design §4.5).</summary>
    [RelayCommand]
    private void NewTodo() => FocusAddBoxRequested?.Invoke(this, GeneralTodosViewModel.GroupKey);

    [RelayCommand]
    private void ClearSearch()
    {
        _searchBeforeDashboard = null;
        SearchText = string.Empty;
        EndDashboardContext();
    }

    [RelayCommand]
    private void RemoveDashboardContext() => EndDashboardContext();

    [RelayCommand]
    private void ReturnToDashboard() => _context.Navigator.Return(AppPage.Checklists);

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        if (!IsFromDashboard)
        {
            var saved = new ViewFilterPreference(value.Trim().Length > 0 ? value.Trim() : null, new Dictionary<string, IReadOnlyList<string>>());
            _context.Preferences.Update(p => p with
            {
                ViewFilters = new Dictionary<string, ViewFilterPreference>(p.ViewFilters ?? new Dictionary<string, ViewFilterPreference>(), StringComparer.Ordinal)
                {
                    [PreferenceKey] = saved,
                },
            });
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        _view.SetFilter(GroupFilter(query));
        General.ApplySearch(query);
        Reviews.ApplySearch(query);
        foreach (var group in Groups)
        {
            group.ApplySearch(query);
        }

        UpdateState();
    }

    // The filter runs on the store scheduler, so it gets a snapshot of the keep-visible set, never the live set.
    private Func<TaskReadModel, bool> GroupFilter(string query)
    {
        var keepVisible = new HashSet<TaskId>(_keepVisible);
        return task => IsGroupVisible(task, query, keepVisible);
    }

    // A group's item command keeps the group shown for this visit. The new filter is queued on the store scheduler
    // before the command commits, so the commit that leaves no open item is already filtered with it.
    private void KeepVisible(TaskId taskId)
    {
        if (_keepVisible.Add(taskId))
        {
            _view.SetFilter(GroupFilter(SearchText.Trim()));
        }
    }

    private ChecklistGroupViewModel CreateGroup(TaskReadModel task)
    {
        var group = new ChecklistGroupViewModel(task, _context, _completions, KeepVisible);
        group.ApplySearch(SearchText.Trim());
        return group;
    }

    // Keeps Cards = [General, Reviews, ...Groups] in step with the group list's own notifications.
    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                for (var i = 0; i < e.NewItems.Count; i++)
                {
                    _cards.Insert(e.NewStartingIndex + PinnedCardCount + i, e.NewItems[i]!);
                }

                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                for (var i = 0; i < e.OldItems.Count; i++)
                {
                    _cards.RemoveAt(e.OldStartingIndex + PinnedCardCount);
                }

                break;
            case NotifyCollectionChangedAction.Move:
                _cards.Move(e.OldStartingIndex + PinnedCardCount, e.NewStartingIndex + PinnedCardCount);
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems is not null:
                _cards[e.NewStartingIndex + PinnedCardCount] = e.NewItems[0]!;
                break;
            default:
                _cards.Clear();
                _cards.Add(General);
                _cards.Add(Reviews);
                foreach (var group in Groups)
                {
                    _cards.Add(group);
                }

                break;
        }

        UpdateState();
    }

    private void UpdateState()
    {
        State = !_view.IsInitialized ? ViewState.Loading : ViewState.Populated;
        var openItems = General.OpenCount + _context.Store.Tasks.Items.Where(t => !t.IsArchived).Sum(t => t.ChecklistTotal - t.ChecklistDone);
        OpenCount = openItems + Reviews.OpenCount;
        var query = SearchText.Trim();
        var matches = General.MatchCount + Reviews.MatchCount + Groups.Sum(g => g.OpenItems.Count + g.CompletedItems.Count);
        var strings = _context.Strings;
        HasNoMatches = query.Length > 0 && matches == 0 && Groups.Count == 0;
        NoMatchesText = strings.Format("Checklists_NoMatches", query);
        Summary = strings.Format(OpenCount == 1 ? "Checklists_SummaryOne" : "Checklists_Summary", OpenCount);
    }

    /// <summary>B-09: task groups are ordered newest parent first, then ID.</summary>
    private sealed class NewestFirst : IComparer<ChecklistGroupViewModel>
    {
        public static NewestFirst Instance { get; } = new();

        public int Compare(ChecklistGroupViewModel? x, ChecklistGroupViewModel? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null || y is null)
            {
                return x is null ? -1 : 1;
            }

            var byCreated = y.Model.CreatedAtUtc.CompareTo(x.Model.CreatedAtUtc);
            return byCreated != 0 ? byCreated : x.Model.Id.Value.CompareTo(y.Model.Id.Value);
        }
    }
}
