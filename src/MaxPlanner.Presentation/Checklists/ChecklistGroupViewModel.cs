using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Checklists;

/// <summary>
/// A task's checklist group on the Checklists page (B-09, design §4.5): header, progress, incomplete items, a
/// Completed section, and the Add an item box. Commands go through the checklist use cases. Use on the UI thread.
/// </summary>
/// <remarks>
/// Checklist items are edited through their task's revision (T-05), so commands of one group run one at a
/// time (<see cref="ChecklistCommandQueue"/>), each after the previous result has reached the store.
/// </remarks>
public sealed partial class ChecklistGroupViewModel : ProjectionItemViewModel<TaskReadModel, TaskId>, IChecklistItemOwner
{
    private readonly WorkspaceContext _context;
    private readonly ChecklistCommandQueue _queue;
    private readonly ChecklistCompletionTracker _completions;
    private readonly Action<TaskId> _keepVisible;
    private readonly ObservableCollection<ChecklistItemViewModel> _openItems = [];
    private readonly ObservableCollection<ChecklistItemViewModel> _completedItems = [];
    private string _query = string.Empty;

    /// <param name="completions">The page's completion recency, shared by every group (B-09 Completed order).</param>
    /// <param name="keepVisible">Called before an item command, so the group stays shown when it leaves no open item.</param>
    public ChecklistGroupViewModel(TaskReadModel model, WorkspaceContext context, ChecklistCompletionTracker completions, Action<TaskId> keepVisible)
        : base(model)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _completions = completions ?? throw new ArgumentNullException(nameof(completions));
        _keepVisible = keepVisible ?? throw new ArgumentNullException(nameof(keepVisible));
        _queue = new ChecklistCommandQueue(context, "Record_Task");
        OpenItems = new ReadOnlyObservableCollection<ChecklistItemViewModel>(_openItems);
        CompletedItems = new ReadOnlyObservableCollection<ChecklistItemViewModel>(_completedItems);
        SyncItems();
    }

    public override TaskId Key => Model.Id;

    public bool CanPopOut => true;

    public ReadOnlyObservableCollection<ChecklistItemViewModel> OpenItems { get; }

    public ReadOnlyObservableCollection<ChecklistItemViewModel> CompletedItems { get; }

    /// <summary>The effective title: the CR's for a linked task (B-02.3), so a CR or Grid title edit shows here.</summary>
    public string Title => Model.EffectiveTitle;

    public string Service => Model.Service ?? string.Empty;

    public bool HasService => Service.Length > 0;

    /// <summary>"CR-1042 · Task 00457 · In Progress", or just the status for an unlinked task.</summary>
    public string Reference => Model.Cr.CrNumber is not { } number
        ? Model.Status.Name
        : Model.Cr.TaskNumber is { } taskNumber
            ? _context.Strings.Format("Checklists_ReferenceWithTask", number, taskNumber, Model.Status.Name)
            : _context.Strings.Format("Checklists_Reference", number, Model.Status.Name);

    public string ProgressText => _context.Strings.Format("Checklists_Progress", Model.ChecklistDone, Model.ChecklistTotal);

    public double ProgressPercent => Model.ChecklistTotal == 0 ? 0 : 100.0 * Model.ChecklistDone / Model.ChecklistTotal;

    public string CompletedHeader => _context.Strings.Format("Checklists_CompletedHeader", _completedItems.Count);

    public bool HasCompleted => _completedItems.Count > 0;

    public int OpenCount => Model.ChecklistTotal - Model.ChecklistDone;

    /// <summary>"Open task Card limits API": the link's accessible name.</summary>
    public string OpenTaskName => _context.Strings.Format("Checklists_OpenTaskName", Model.EffectiveTitle);

    /// <summary>The Add an item box's draft; bound two-way and kept when adding fails.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddItemCommand))]
    public partial string NewItemText { get; set; } = string.Empty;

    /// <summary>The inline error under the Add box (blank text, conflict, storage).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewItemError))]
    public partial string? NewItemError { get; private set; }

    public bool HasNewItemError => NewItemError is not null;

    /// <summary>The last item command failure for this group, shown above its items.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToggleError))]
    public partial string? ToggleError { get; private set; }

    public bool HasToggleError => ToggleError is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddItemCommand))]
    public partial bool IsAdding { get; private set; }

    /// <summary>B-09 search: the group matches by its title, service, or CR reference, or through matching items.</summary>
    public bool HeaderMatches(string query) =>
        query.Length == 0
        || Model.EffectiveTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || (Model.Service?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || (Model.Cr.CrNumber?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || (Model.Cr.TaskNumber?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);

    /// <summary>
    /// Shows only matching items while a search is active, unless the group itself matches (then all items show).
    /// The page filter decides whether the group is shown at all.
    /// </summary>
    public void ApplySearch(string query)
    {
        if (_query != query)
        {
            _query = query;
            SyncItems();
        }
    }

    /// <summary>"Pop out to card" (B-16.3): the item becomes a Subtask card in one transaction.</summary>
    public Task PopOutAsync(ChecklistItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _queue.Enqueue(async () =>
        {
            _keepVisible(Model.Id);
            item.IsPending = true;
            var cardId = await _context.Tasks.PopOutAsync(Model, new ChecklistItemId(item.Id)).ConfigureAwait(true);
            item.IsPending = false;
            ToggleError = cardId is null && _context.SaveStatus.FailureMessage is { } failure ? failure : null;
        });
    }

    /// <summary>Checks or unchecks an item from its checkbox (B-09). Never changes the task's status or dates.</summary>
    public Task ToggleAsync(ChecklistItemViewModel item, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _queue.Enqueue(async () =>
        {
            if (item.IsChecked == isChecked)
            {
                return;
            }

            _keepVisible(Model.Id);
            item.IsPending = true;
            ToggleError = await _queue.RunAsync(op => _context.UseCases.SetChecklistItemCompleted.ExecuteAsync(
                new SetChecklistItemCompletedCommand(op, Model.Id, Model.Revision, new ChecklistItemId(item.Id), isChecked), CancellationToken.None)).ConfigureAwait(true);
            item.IsPending = false;
            if (ToggleError is not null)
            {
                // AC-07: the shared state is unchanged, so the checkbox returns to the committed value.
                item.RestoreCommitted();
            }
            else
            {
                _context.Announce(_context.Strings.Format(isChecked ? "Checklists_ItemDoneAnnounced" : "Checklists_ItemNotDoneAnnounced", item.Text));
            }
        });
    }

    public async Task<string?> RenameAsync(ChecklistItemViewModel item, string text)
    {
        ArgumentNullException.ThrowIfNull(item);
        string? error = null;
        await _queue.Enqueue(async () =>
        {
            item.IsPending = true;
            error = await _queue.RunAsync(op => _context.UseCases.RenameChecklistItem.ExecuteAsync(
                new RenameChecklistItemCommand(op, Model.Id, Model.Revision, new ChecklistItemId(item.Id), text), CancellationToken.None)).ConfigureAwait(true);
            item.IsPending = false;
        }).ConfigureAwait(true);
        return error;
    }

    public bool CanMove(ChecklistItemViewModel item, bool up) => ChecklistSections.Neighbour(_openItems, item, up) is not null;

    public Task MoveAsync(ChecklistItemViewModel item, bool up)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _queue.Enqueue(async () =>
        {
            if (ChecklistSections.Neighbour(_openItems, item, up) is not { } anchor)
            {
                return;
            }

            item.IsPending = true;
            ToggleError = await _queue.RunAsync(op => _context.UseCases.MoveChecklistItem.ExecuteAsync(
                new MoveChecklistItemCommand(op, Model.Id, Model.Revision, new ChecklistItemId(item.Id), new ChecklistItemId(anchor.Id), PlaceAfter: !up), CancellationToken.None)).ConfigureAwait(true);
            item.IsPending = false;
        });
    }

    public Task DeleteAsync(ChecklistItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _queue.Enqueue(async () =>
        {
            _keepVisible(Model.Id);
            item.IsPending = true;
            ToggleError = await _queue.RunAsync(op => _context.UseCases.DeleteChecklistItem.ExecuteAsync(
                new DeleteChecklistItemCommand(op, Model.Id, Model.Revision, new ChecklistItemId(item.Id)), CancellationToken.None)).ConfigureAwait(true);
            item.IsPending = false;
            if (ToggleError is null)
            {
                _context.Announce(_context.Strings.Format("Checklists_ItemDeleted", item.Text));
            }
        });
    }

    protected override void OnModelUpdated(TaskReadModel previous)
    {
        SyncItems();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Service));
        OnPropertyChanged(nameof(HasService));
        OnPropertyChanged(nameof(Reference));
        OnPropertyChanged(nameof(OpenTaskName));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(OpenCount));
    }

    /// <summary>"Open task" (B-09, design §4.5): the task editor for this group's task.</summary>
    [RelayCommand]
    private Task OpenTask() => _context.Tasks.OpenAsync(Model.Id);

    private bool CanAddItem() => !IsAdding;

    /// <summary>Enter in the Add box, or its Add button: blank text is refused with an inline error (B-02.7).</summary>
    [RelayCommand(CanExecute = nameof(CanAddItem))]
    private Task AddItemAsync() => _queue.Enqueue(async () =>
    {
        IsAdding = true;
        NewItemError = await _queue.RunAsync(op => _context.UseCases.AddChecklistItem.ExecuteAsync(
            new AddChecklistItemCommand(op, Model.Id, Model.Revision, new ChecklistItemId(Guid.NewGuid()), NewItemText), CancellationToken.None)).ConfigureAwait(true);
        if (NewItemError is null)
        {
            NewItemText = string.Empty;
        }

        IsAdding = false;
    });

    private void SyncItems()
    {
        var showAll = HeaderMatches(_query);

        // Every item is observed, matching or not, so a completion hidden by the search still counts as recent.
        var recency = Model.ChecklistItems.ToDictionary(i => i.Id.Value, i => _completions.Observe(i.Id.Value, i.IsCompleted));
        var items = Model.ChecklistItems
            .Where(i => showAll || i.Text.Contains(_query, StringComparison.CurrentCultureIgnoreCase))
            .Select(i => (i.Id.Value, i.Text, i.IsCompleted, recency[i.Id.Value]))
            .ToList();
        ChecklistSections.Sync(_openItems, _completedItems, items, (id, text, done) => new ChecklistItemViewModel(id, text, done, this, _context.Strings));
        OnPropertyChanged(nameof(CompletedHeader));
        OnPropertyChanged(nameof(HasCompleted));
    }
}

/// <summary>The open/completed split and Move up/down neighbours shared by every checklist card (B-09).</summary>
internal static class ChecklistSections
{
    /// <summary>
    /// Puts each item in the open or completed section, keeping each row's ViewModel instance so a focused checkbox
    /// keeps its container (T-07). Open items follow the stored order; completed items are shown most recently
    /// completed first (higher <c>Recency</c>), ties in stored order (B-09). Stored ranks are never changed.
    /// </summary>
    public static void Sync(
        ObservableCollection<ChecklistItemViewModel> open,
        ObservableCollection<ChecklistItemViewModel> completed,
        IReadOnlyList<(Guid Id, string Text, bool IsCompleted, long Recency)> ordered,
        Func<Guid, string, bool, ChecklistItemViewModel> create)
    {
        var existing = open.Concat(completed).ToDictionary(i => i.Id);

        // An item moving between sections is inserted into the other list as a new key, so the reused ViewModel
        // must take the new state here; otherwise its row in the other section renders the previous IsChecked.
        ChecklistItemViewModel Reuse((Guid Id, string Text, bool IsCompleted, long Recency) model)
        {
            if (!existing.TryGetValue(model.Id, out var item))
            {
                return create(model.Id, model.Text, model.IsCompleted);
            }

            item.Update(model.Text, model.IsCompleted);
            return item;
        }

        KeyedCollectionSync.Sync(open, [.. ordered.Where(i => !i.IsCompleted)], m => m.Id, vm => vm.Id, Reuse, (vm, m) => vm.Update(m.Text, m.IsCompleted));
        KeyedCollectionSync.Sync(completed, [.. CompletedDisplayOrder(ordered)], m => m.Id, vm => vm.Id, Reuse, (vm, m) => vm.Update(m.Text, m.IsCompleted));
        foreach (var item in open.Concat(completed))
        {
            item.RefreshCommands();
        }
    }

    /// <summary>B-09: completed items, most recently completed first; the sort is stable, so ties keep stored order.</summary>
    public static IEnumerable<(Guid Id, string Text, bool IsCompleted, long Recency)> CompletedDisplayOrder(
        IEnumerable<(Guid Id, string Text, bool IsCompleted, long Recency)> ordered) =>
        ordered.Where(i => i.IsCompleted).OrderByDescending(i => i.Recency);

    /// <summary>
    /// The visible neighbour in the open section; Move up places the item before it, Move down after it. Completed
    /// items are shown in completion order, so moving one would not change what is shown (B-09).
    /// </summary>
    public static ChecklistItemViewModel? Neighbour(
        IReadOnlyList<ChecklistItemViewModel> open,
        ChecklistItemViewModel item,
        bool up)
    {
        var index = IndexOf(open, item);
        var target = up ? index - 1 : index + 1;
        return index >= 0 && target >= 0 && target < open.Count ? open[target] : null;
    }

    private static int IndexOf(IReadOnlyList<ChecklistItemViewModel> list, ChecklistItemViewModel item)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Completion recency of task checklist items for the Completed order (B-09). Task items carry no completion
/// timestamp, so recency is the order in which this page saw items change from open to done during the session;
/// items already done when first seen have recency 0 and keep their stored order. Use on the UI thread.
/// </summary>
public sealed class ChecklistCompletionTracker
{
    private readonly Dictionary<Guid, bool> _lastSeen = [];
    private readonly Dictionary<Guid, long> _recency = [];
    private long _sequence;

    /// <returns>The item's recency: higher is more recently completed; 0 when unknown or open.</returns>
    public long Observe(Guid id, bool isCompleted)
    {
        if (!isCompleted)
        {
            _recency.Remove(id);
        }
        else if (_lastSeen.TryGetValue(id, out var wasCompleted) && !wasCompleted)
        {
            _recency[id] = ++_sequence;
        }

        _lastSeen[id] = isCompleted;
        return _recency.GetValueOrDefault(id);
    }
}
