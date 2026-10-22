using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Board;

/// <summary>
/// One Board column: the active tasks and Subtask cards in one status, ordered by rank then ID (B-07).
/// Owns its list subscription. Use on the UI thread.
/// </summary>
public sealed partial class BoardColumnViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly ProjectionView<WorkItemRecord, Guid, BoardCardViewModel> _view;

    internal BoardColumnViewModel(WorkspaceContext context, TaskStatusDefinition status, Func<WorkItemRecord, PriorityLevel> priorityLevel)
    {
        _context = context;
        StatusId = status.Id;
        var statusId = status.Id;
        _view = new ProjectionView<WorkItemRecord, Guid, BoardCardViewModel>(
            WorkItemRecord.Connect(context.Store),
            context.Store.Scheduler,
            context.UiScheduler,
            record => new BoardCardViewModel(record, context.Strings, priorityLevel),
            RankOrder.Instance,
            record => record.IsActive && record.Status.Id == statusId);
        _view.Initialized += (_, _) => Initialized?.Invoke(this, EventArgs.Empty);
        ((INotifyCollectionChanged)_view.Items).CollectionChanged += OnCardsChanged;
        Update(status);
    }

    internal event EventHandler? Initialized;

    public TaskStatusId StatusId { get; }

    public ReadOnlyObservableCollection<BoardCardViewModel> Cards => _view.Items;

    public bool IsInitialized => _view.IsInitialized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Name { get; private set; } = string.Empty;

    /// <summary>Segoe Fluent Icons from the status flags (design §6.2); empty when no flag applies.</summary>
    [ObservableProperty]
    public partial string Glyph { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string CountText { get; private set; } = "0";

    /// <summary>"Review column, 3 cards" (design §8.2).</summary>
    public string AccessibleName => _context.Strings.Format(Cards.Count == 1 ? "Board_ColumnNameOne" : "Board_ColumnName", Name, Cards.Count);

    public void Dispose()
    {
        ((INotifyCollectionChanged)_view.Items).CollectionChanged -= OnCardsChanged;
        _view.Dispose();
    }

    internal void Update(TaskStatusDefinition status)
    {
        Name = status.Name.Display;
        Glyph = status.IsCompleted ? "" : status.SetsStartDate ? "" : status.IsBacklog ? "" : string.Empty;
    }

    /// <summary>The visible card count. Filters arrive in Phase 11; then the total active count joins it (B-07).</summary>
    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CountText = Cards.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        CardsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal event EventHandler? CardsChanged;

    /// <summary>
    /// B-07: persisted rank, then ID with the .NET <see cref="Guid"/> comparison as the final tie-breaker,
    /// the same order the writer uses when it renumbers a column (slice 1d).
    /// </summary>
    private sealed class RankOrder : IComparer<BoardCardViewModel>
    {
        public static RankOrder Instance { get; } = new();

        public int Compare(BoardCardViewModel? x, BoardCardViewModel? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null || y is null)
            {
                return x is null ? -1 : 1;
            }

            var byRank = x.Model.Rank.CompareTo(y.Model.Rank);
            return byRank != 0 ? byRank : x.Model.Id.CompareTo(y.Model.Id);
        }
    }
}

/// <summary>
/// The Board page (B-07, design §4.2), Phase 2 subset: one read-only column per task status in configured
/// order, each updated incrementally from the shared store. Use on the UI thread.
/// </summary>
public sealed partial class BoardPageViewModel : ObservableObject, IPageViewModel
{
    private readonly WorkspaceContext _context;
    private readonly ObservableCollection<BoardColumnViewModel> _columns = [];
    private readonly IDisposable _workflowSubscription;
    private Dictionary<PriorityId, PriorityLevel> _priorityLevels = [];

    public BoardPageViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Columns = new ReadOnlyObservableCollection<BoardColumnViewModel>(_columns);
        _workflowSubscription = context.Store.Workflow.ObserveOn(context.UiScheduler).Subscribe(OnWorkflow);
    }

    public ReadOnlyObservableCollection<BoardColumnViewModel> Columns { get; }

    [ObservableProperty]
    public partial ViewState State { get; private set; } = ViewState.Loading;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    public void Dispose()
    {
        _workflowSubscription.Dispose();
        foreach (var column in _columns)
        {
            column.Dispose();
        }

        _columns.Clear();
    }

    /// <summary>
    /// B-04 statuses are configurable, so columns follow the workflow: new statuses add a column, removed ones
    /// drop theirs, renames and reorders update in place. Lookup changes arrive with a store batch.
    /// </summary>
    private void OnWorkflow(WorkflowSnapshot workflow)
    {
        _priorityLevels = PriorityLevels(workflow.Priorities);
        var statuses = workflow.TaskStatuses.OrderBy(s => s.Order).ToList();
        var removed = _columns.Where(c => statuses.All(s => s.Id != c.StatusId)).ToList();
        KeyedCollectionSync.Sync(
            _columns,
            statuses,
            status => status.Id,
            column => column.StatusId,
            status =>
            {
                var column = new BoardColumnViewModel(_context, status, LevelOf);
                column.Initialized += (_, _) => UpdateState();
                column.CardsChanged += (_, _) => UpdateState();
                return column;
            },
            (column, status) => column.Update(status));
        foreach (var column in removed)
        {
            column.Dispose();
        }

        UpdateState();
    }

    private PriorityLevel LevelOf(WorkItemRecord record) =>
        record.Task is { } task && _priorityLevels.TryGetValue(task.Priority.Id, out var level) ? level : PriorityLevel.Mid;

    /// <summary>Design §6.1: highest rank Level1, second Level2, lowest (with three or more) Lowest, the rest Mid.</summary>
    private static Dictionary<PriorityId, PriorityLevel> PriorityLevels(IReadOnlyList<PriorityDefinition> priorities)
    {
        var ordered = priorities.OrderBy(p => p.Order).ToList();
        var levels = new Dictionary<PriorityId, PriorityLevel>();
        for (var i = 0; i < ordered.Count; i++)
        {
            levels[ordered[i].Id] = i == 0 ? PriorityLevel.Level1
                : ordered.Count >= 3 && i == ordered.Count - 1 ? PriorityLevel.Lowest
                : i == 1 ? PriorityLevel.Level2
                : PriorityLevel.Mid;
        }

        return levels;
    }

    private void UpdateState()
    {
        // The Board keeps its columns visible even with no cards (design §4.2 Empty), so Empty only changes
        // the summary; the StateView covers loading and errors.
        var initialized = _columns.Count > 0 && _columns.All(c => c.IsInitialized);
        var cards = _columns.Sum(c => c.Cards.Count);
        State = initialized ? ViewState.Populated : ViewState.Loading;
        Summary = cards == 0
            ? _context.Strings.GetString("Board_SummaryEmpty")
            : _context.Strings.Format(cards == 1 ? "Board_SummaryOne" : "Board_Summary", cards, _columns.Count);
    }
}
