using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Board;

/// <summary>Where "Move to ▸" puts a card in the destination column (design §4.2 card menu).</summary>
public enum BoardEdge
{
    Top,
    Bottom,
}

/// <summary>Asks the view to focus a card after a move (design §7.7: focus follows the moved card).</summary>
public sealed record BoardFocusRequest(BoardColumnViewModel Column, BoardCardViewModel Card);

/// <summary>
/// One Board column: the active tasks and Subtask cards in one status, ordered by rank then ID (B-07).
/// Owns its list subscription. Use on the UI thread.
/// </summary>
public sealed partial class BoardColumnViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly ProjectionView<WorkItemRecord, Guid, BoardCardViewModel> _view;
    private readonly TaskStatusId _statusId;
    private string _query = string.Empty;

    internal BoardColumnViewModel(WorkspaceContext context, TaskStatusDefinition status, Func<WorkItemRecord, PriorityLevel> priorityLevel, string query)
    {
        _context = context;
        StatusId = status.Id;
        _statusId = status.Id;
        _query = query;
        _view = new ProjectionView<WorkItemRecord, Guid, BoardCardViewModel>(
            WorkItemRecord.Connect(context.Store),
            context.Store.Scheduler,
            context.UiScheduler,
            record => new BoardCardViewModel(record, context.Strings, priorityLevel),
            RankOrder.Instance,
            Predicate(query));
        _view.Initialized += (_, _) => Initialized?.Invoke(this, EventArgs.Empty);
        ((INotifyCollectionChanged)_view.Items).CollectionChanged += OnCardsChanged;
        Update(status);
    }

    internal event EventHandler? Initialized;

    internal event EventHandler? CardsChanged;

    public TaskStatusId StatusId { get; }

    public ReadOnlyObservableCollection<BoardCardViewModel> Cards => _view.Items;

    public bool IsInitialized => _view.IsInitialized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(AddButtonName), nameof(PickerText), nameof(DropCaption), nameof(AppendCaption))]
    public partial string Name { get; private set; } = string.Empty;

    /// <summary>Segoe Fluent Icons from the status flags (design §6.2); empty when no flag applies.</summary>
    [ObservableProperty]
    public partial string Glyph { get; private set; } = string.Empty;

    /// <summary>"6", or "4 of 6" while the Board is filtered: visible / total active (B-07).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(PickerText))]
    public partial string CountText { get; private set; } = "0";

    /// <summary>"Review column, 3 cards" (design §8.2).</summary>
    public string AccessibleName => _context.Strings.Format(Cards.Count == 1 ? "Board_ColumnNameOne" : "Board_ColumnName", Name, Cards.Count);

    /// <summary>"New task in In Progress": the column + button (design §4.2).</summary>
    public string AddButtonName => _context.Strings.Format("Board_ColumnAddName", Name);

    /// <summary>"Column: In Progress (6)": the S-breakpoint column picker (design §4.2).</summary>
    public string PickerText => _context.Strings.Format("Board_ColumnPickerItem", Name, CountText);

    /// <summary>"Drop to move to Review" (design §4.2 drop feedback).</summary>
    public string DropCaption => _context.Strings.Format("Board_DropCaption", Name);

    /// <summary>"Will be added to the end of Review" (filtered drop, B-07).</summary>
    public string AppendCaption => _context.Strings.Format("Board_DropAppendCaption", Name);

    public override string ToString() => PickerText;

    public void Dispose()
    {
        ((INotifyCollectionChanged)_view.Items).CollectionChanged -= OnCardsChanged;
        _view.Dispose();
    }

    internal void Update(TaskStatusDefinition status)
    {
        Name = status.Name.Display;
        Glyph = status.IsCompleted ? "" : status.SetsStartDate ? "" : status.IsBacklog ? "" : string.Empty;
    }

    internal void SetQuery(string query)
    {
        _query = query;
        _view.SetFilter(Predicate(query));
        UpdateCount();
    }

    /// <summary>Recounts the hidden total; filtered-out cards change without touching the visible list.</summary>
    internal void UpdateCount()
    {
        var visible = Cards.Count;
        if (_query.Length == 0)
        {
            CountText = visible.ToString(CultureInfo.CurrentCulture);
            return;
        }

        var total = _context.Store.Tasks.Items.Count(t => !t.IsArchived && t.Status.Id == _statusId)
            + _context.Store.SubtaskCards.Items.Count(c => !c.IsParentArchived && c.Status.Id == _statusId);
        CountText = _context.Strings.Format("Board_CountFiltered", visible, total);
    }

    private Func<WorkItemRecord, bool> Predicate(string query)
    {
        var statusId = _statusId;
        return record => record.IsActive && record.Status.Id == statusId && BoardSearch.Matches(record, query);
    }

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateCount();
        OnPropertyChanged(nameof(AccessibleName));
        CardsChanged?.Invoke(this, EventArgs.Empty);
    }

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
/// B-10 task text search as the Board applies it until the shared filter panel arrives (Phase 11): a
/// case-insensitive substring over the effective title, CR and task number, squad, owner, service, people,
/// tags, and notes. Subtask cards match their subtitle, notes, and their parent's displayed values.
/// </summary>
public static class BoardSearch
{
    public static bool Matches(WorkItemRecord record, string query)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var q = query.Trim();
        bool Has(string? value) => value is not null && value.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        var cr = record.Cr;
        if (Has(cr.CrNumber) || Has(cr.TaskNumber) || Has(cr.SquadName) || Has(cr.OwnerName))
        {
            return true;
        }

        if (record.Task is { } task)
        {
            return Has(task.EffectiveTitle) || Has(task.Service) || Has(task.Notes)
                || task.Designers.Any(p => Has(p.Name)) || task.Developers.Any(p => Has(p.Name)) || task.Tags.Any(t => Has(t.Name));
        }

        var card = record.Card!;
        return Has(card.Subtitle) || Has(card.Notes) || Has(card.ParentTitle) || Has(card.ParentService);
    }
}

/// <summary>
/// The Board page (B-07, B-16, design §4.2): one column per task status in configured order, updated
/// incrementally from the shared store; drag/drop, keyboard and menu moves through the move use cases;
/// the card menu; task search with the filtered-append rule; the S-breakpoint column picker. A card moves
/// on screen only after its move committed, so a failed move leaves the committed position (B-07). Use on
/// the UI thread.
/// </summary>
public sealed partial class BoardPageViewModel : ObservableObject, IPageViewModel
{
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkspaceContext _context;
    private readonly ObservableCollection<BoardColumnViewModel> _columns = [];
    private readonly IDisposable _workflowSubscription;
    private readonly IDisposable _commits;
    private Dictionary<PriorityId, PriorityLevel> _priorityLevels = [];

    public BoardPageViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Columns = new ReadOnlyObservableCollection<BoardColumnViewModel>(_columns);
        _workflowSubscription = context.Store.Workflow.ObserveOn(context.UiScheduler).Subscribe(OnWorkflow);

        // Hidden cards change the "4 of 6" totals without touching the visible lists (B-07 counts).
        _commits = context.Store.Committed.ObserveOn(context.UiScheduler).Subscribe(_ => OnCommitted());
    }

    /// <summary>Raised after a move, so the view focuses the moved card in its new position (design §7.7).</summary>
    public event EventHandler<BoardFocusRequest>? FocusRequested;

    public ReadOnlyObservableCollection<BoardColumnViewModel> Columns { get; }

    [ObservableProperty]
    public partial ViewState State { get; private set; } = ViewState.Loading;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Task-scope search (B-10). While it is set, reordering within a column is off (B-07).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered), nameof(CanReorder))]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The column shown at the S breakpoint (design §4.2: one column per viewport with ‹ ›).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousColumnCommand), nameof(NextColumnCommand))]
    public partial BoardColumnViewModel? SelectedColumn { get; set; }

    public bool IsFiltered => SearchText.Trim().Length > 0;

    /// <summary>B-07 design default: positional reordering is off while search or filters are active.</summary>
    public bool CanReorder => !IsFiltered;

    public bool IsEditingBlocked => _context.IsEditingBlocked;

    /// <summary>
    /// A drag/drop at <paramref name="insertIndex"/> of <paramref name="target"/>'s visible cards (design §9).
    /// Filtered: a status change appends to the end of the full column and reordering is refused (B-07).
    /// </summary>
    public Task<bool> DropAsync(BoardCardViewModel card, BoardColumnViewModel target, int insertIndex)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(target);
        var source = ColumnOf(card);
        if (IsFiltered)
        {
            return source == target ? RefuseReorder() : MoveAsync(card, target, BoardPlacement.AppendToEnd);
        }

        return PlaceAtAsync(card, target, insertIndex);
    }

    /// <summary>Move to ▸ {status} Top/Bottom from the card menu. While filtered both append to the end (B-07).</summary>
    public Task<bool> MoveToColumnAsync(BoardCardViewModel card, BoardColumnViewModel target, BoardEdge edge)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(target);
        if (IsFiltered)
        {
            return ColumnOf(card) == target ? RefuseReorder() : MoveAsync(card, target, BoardPlacement.AppendToEnd);
        }

        return PlaceAtAsync(card, target, edge == BoardEdge.Top ? 0 : int.MaxValue);
    }

    /// <summary>Alt+Shift+Left/Right (design §7.3): the previous/next status, appended to the end (B-07).</summary>
    public Task<bool> MoveToAdjacentColumnAsync(BoardCardViewModel card, int direction)
    {
        ArgumentNullException.ThrowIfNull(card);
        var index = ColumnOf(card) is { } source ? _columns.IndexOf(source) : -1;
        var target = index + Math.Sign(direction);
        return index < 0 || target < 0 || target >= _columns.Count
            ? Task.FromResult(false)
            : MoveAsync(card, _columns[target], BoardPlacement.AppendToEnd);
    }

    /// <summary>Alt+Shift+Up/Down and Move up/down (design §7.3): reorder within the column; off while filtered.</summary>
    public Task<bool> MoveWithinColumnAsync(BoardCardViewModel card, int delta)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (ColumnOf(card) is not { } column)
        {
            return Task.FromResult(false);
        }

        if (IsFiltered)
        {
            return RefuseReorder();
        }

        var index = column.Cards.IndexOf(card) + Math.Sign(delta);
        return index < 0 || index >= column.Cards.Count ? Task.FromResult(false) : PlaceAtAsync(card, column, index);
    }

    public bool CanMoveUp(BoardCardViewModel card) => CanReorder && ColumnOf(card) is { } c && c.Cards.IndexOf(card) > 0;

    public bool CanMoveDown(BoardCardViewModel card) => CanReorder && ColumnOf(card) is { } c && c.Cards.IndexOf(card) < c.Cards.Count - 1;

    /// <summary>Open (Enter): the task editor, or the Subtask card editor for a Subtask card (design §5.2).</summary>
    public Task OpenAsync(BoardCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card.Model.Card is { } subtaskCard ? _context.Tasks.OpenSubtaskCardAsync(subtaskCard.Id) : _context.Tasks.OpenAsync(card.TaskId);
    }

    /// <summary>A Subtask card's "Subtask of …" link opens its parent task (B-16.5).</summary>
    public Task OpenParentAsync(BoardCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return _context.Tasks.OpenAsync(card.TaskId);
    }

    /// <summary>The card's CR reference link opens the CR, where the inherited values are edited (B-02.4).</summary>
    public Task OpenCrAsync(BoardCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card.Model.Cr.ChangeRequestId is { } id ? _context.ChangeRequests.OpenAsync(id) : Task.CompletedTask;
    }

    /// <summary>Accent New task (default status) or a column's + (that status, B-02.1).</summary>
    public Task NewTaskAsync(BoardColumnViewModel? column = null) => _context.Tasks.NewAsync(column?.StatusId);

    public Task CopyAsync(BoardCardViewModel card) => card.Model.Task is { } task ? _context.Tasks.CopyAsync(task.Id) : Task.CompletedTask;

    public Task AddSubtaskCardAsync(BoardCardViewModel card) => card.Model.Task is { } task ? _context.Tasks.AddSubtaskCardAsync(task) : Task.CompletedTask;

    public Task<bool> ArchiveAsync(BoardCardViewModel card) => card.Model.Task is { } task ? _context.Tasks.ArchiveAsync(task) : Task.FromResult(false);

    public Task<bool> DeleteAsync(BoardCardViewModel card) => card.Model switch
    {
        { Task: { } task } => _context.Tasks.DeleteAsync(task),
        { Card: { } subtaskCard } => _context.Tasks.DeleteSubtaskCardAsync(subtaskCard),
        _ => Task.FromResult(false),
    };

    /// <summary>The column that shows <paramref name="card"/>, or null when it is no longer visible.</summary>
    public BoardColumnViewModel? ColumnOf(BoardCardViewModel card) => _columns.FirstOrDefault(c => c.Cards.Contains(card));

    public void Dispose()
    {
        _commits.Dispose();
        _workflowSubscription.Dispose();
        foreach (var column in _columns)
        {
            column.Dispose();
        }

        _columns.Clear();
    }

    [RelayCommand]
    private Task NewTask() => NewTaskAsync();

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousColumn() => SelectedColumn = _columns[_columns.IndexOf(SelectedColumn!) - 1];

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextColumn() => SelectedColumn = _columns[_columns.IndexOf(SelectedColumn!) + 1];

    private bool CanGoPrevious() => SelectedColumn is { } c && _columns.IndexOf(c) > 0;

    private bool CanGoNext() => SelectedColumn is { } c && _columns.IndexOf(c) < _columns.Count - 1;

    partial void OnSearchTextChanged(string value)
    {
        foreach (var column in _columns)
        {
            column.SetQuery(value.Trim());
        }

        UpdateState();
    }

    /// <summary>
    /// Places the card before the card now at <paramref name="index"/> of the destination's visible list
    /// (the moved card itself excluded). Unfiltered, the visible list is the full column, so its neighbours'
    /// ranks are exact; the writer respaces the full column when they leave no gap (B-07).
    /// </summary>
    private Task<bool> PlaceAtAsync(BoardCardViewModel card, BoardColumnViewModel target, int index)
    {
        var others = target.Cards.Where(c => c.Key != card.Key).ToList();
        index = Math.Clamp(index, 0, others.Count);
        if (ColumnOf(card) == target && target.Cards.IndexOf(card) == index)
        {
            return Task.FromResult(true);
        }

        var before = index > 0 ? others[index - 1].Model.Rank : (long?)null;
        var after = index < others.Count ? others[index].Model.Rank : (long?)null;
        return MoveAsync(card, target, BoardPlacement.Between(before, after));
    }

    private Task<bool> RefuseReorder()
    {
        _context.Announce(_context.Strings.GetString("Board_ReorderOff"), isFailure: true);
        return Task.FromResult(false);
    }

    /// <summary>
    /// Runs the move use case for the card's kind (status, B-04 dates, and rank in one transaction). The card
    /// stays where it is until the result is displayed; on failure it simply stays, the save status says
    /// Failed, and the failure is announced (B-07, design §4.2, §8.3).
    /// </summary>
    private async Task<bool> MoveAsync(BoardCardViewModel card, BoardColumnViewModel target, BoardPlacement placement)
    {
        var source = ColumnOf(card);
        if (_context.IsEditingBlocked)
        {
            _context.Announce(_context.Strings.GetString("Error_EditingBlocked"), isFailure: true);
            return false;
        }

        var model = card.Model;
        var operation = OperationId.New();
        card.IsMoving = true;
        _context.SaveStatus.Begin();
        var result = model switch
        {
            { Task: { } task } => await _context.UseCases.MoveTask.ExecuteAsync(
                new MoveTaskCommand(operation, task.Id, task.Revision, target.StatusId, placement), CancellationToken.None).ConfigureAwait(true),
            { Card: { } subtaskCard } => await _context.UseCases.MoveSubtaskCard.ExecuteAsync(
                new MoveSubtaskCardCommand(operation, subtaskCard.Id, subtaskCard.Revision, target.StatusId, placement), CancellationToken.None).ConfigureAwait(true),
            _ => throw new InvalidOperationException("A Board card is a task or a Subtask card."),
        };
        if (!result.IsSuccess)
        {
            card.IsMoving = false;
            var failure = CommandFailure.From(result.Error!, operation, card.IsTask ? "Record_Task" : "Record_SubtaskCard", _context.Strings);
            _context.SaveStatus.Fail(failure);
            _context.Announce(_context.Strings.Format("Board_MoveFailed", card.Title, source?.Name ?? model.Status.Name) + " " + failure.Message, isFailure: true);
            return false;
        }

        _context.SaveStatus.Complete();
        await _context.WhenDisplayedAsync(result.Value.CommitSequence, DisplayTimeout).ConfigureAwait(true);
        card.IsMoving = false;
        if (target.Cards.FirstOrDefault(c => c.Key == card.Key) is { } moved)
        {
            _context.Announce(_context.Strings.Format("Board_Moved", moved.Title, target.Name, target.Cards.IndexOf(moved) + 1, target.Cards.Count));
            FocusRequested?.Invoke(this, new BoardFocusRequest(target, moved));
        }
        else
        {
            // Saved, but hidden by the current search (B-10): the save message says so.
            _context.Announce(_context.Strings.Format("Board_MovedHidden", card.Title, target.Name));
        }

        return true;
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
                var column = new BoardColumnViewModel(_context, status, LevelOf, SearchText.Trim());
                column.Initialized += (_, _) => UpdateState();
                column.CardsChanged += (_, _) => UpdateState();
                return column;
            },
            (column, status) => column.Update(status));
        foreach (var column in removed)
        {
            column.Dispose();
        }

        if (SelectedColumn is null || !_columns.Contains(SelectedColumn))
        {
            SelectedColumn = _columns.FirstOrDefault();
        }

        PreviousColumnCommand.NotifyCanExecuteChanged();
        NextColumnCommand.NotifyCanExecuteChanged();
        UpdateState();
    }

    private void OnCommitted()
    {
        if (IsFiltered)
        {
            foreach (var column in _columns)
            {
                column.UpdateCount();
            }

            UpdateState();
        }
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
        if (IsFiltered)
        {
            var total = _context.Store.Tasks.Items.Count(t => !t.IsArchived) + _context.Store.SubtaskCards.Items.Count(c => !c.IsParentArchived);
            Summary = _context.Strings.Format("Board_SummaryFiltered", cards, total);
            return;
        }

        Summary = cards == 0
            ? _context.Strings.GetString("Board_SummaryEmpty")
            : _context.Strings.Format(cards == 1 ? "Board_SummaryOne" : "Board_Summary", cards, _columns.Count);
    }
}
