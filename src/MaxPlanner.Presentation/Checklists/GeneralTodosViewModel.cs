using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Checklists;

/// <summary>
/// The always-present General to-dos card (B-09, design §4.5): independent to-dos with no task, CR, or status.
/// It follows the store's committed batches and keeps each row's ViewModel instance across updates (T-07). Use on
/// the UI thread.
/// </summary>
public sealed partial class GeneralTodosViewModel : ObservableObject, IChecklistItemOwner, IDisposable
{
    /// <summary>The group key in AutomationIds (design §8.1: <c>GroupKey</c> = <c>General</c> or the TaskId).</summary>
    public const string GroupKey = "General";

    private readonly WorkspaceContext _context;
    private readonly ChecklistCommandQueue _queue;
    private readonly ObservableCollection<ChecklistItemViewModel> _openItems = [];
    private readonly ObservableCollection<ChecklistItemViewModel> _completedItems = [];
    private readonly IDisposable _commits;
    private string _query = string.Empty;

    public GeneralTodosViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _queue = new ChecklistCommandQueue(context, "Record_GeneralTodo");
        OpenItems = new ReadOnlyObservableCollection<ChecklistItemViewModel>(_openItems);
        CompletedItems = new ReadOnlyObservableCollection<ChecklistItemViewModel>(_completedItems);
        _commits = context.Store.Committed.ObserveOn(context.UiScheduler).Subscribe(_ => Sync());
        Sync();
    }

    public bool CanPopOut => false;

    public ReadOnlyObservableCollection<ChecklistItemViewModel> OpenItems { get; }

    public ReadOnlyObservableCollection<ChecklistItemViewModel> CompletedItems { get; }

    [ObservableProperty]
    public partial int TotalCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressPercent))]
    public partial int DoneCount { get; private set; }

    /// <summary>Open to-dos in the dataset, whatever the search (the page subtitle and nav badge count).</summary>
    public int OpenCount => TotalCount - DoneCount;

    /// <summary>Items matching the current search, for the SelectorBar match count.</summary>
    public int MatchCount => _openItems.Count + _completedItems.Count;

    public string ProgressText => _context.Strings.Format("Checklists_Progress", DoneCount, TotalCount);

    public double ProgressPercent => TotalCount == 0 ? 0 : 100.0 * DoneCount / TotalCount;

    public string CompletedHeader => _context.Strings.Format("Checklists_CompletedHeader", _completedItems.Count);

    public bool HasCompleted => _completedItems.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddItemCommand))]
    public partial string NewItemText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewItemError))]
    public partial string? NewItemError { get; private set; }

    public bool HasNewItemError => NewItemError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    public partial string? ActionError { get; private set; }

    public bool HasActionError => ActionError is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddItemCommand))]
    public partial bool IsAdding { get; private set; }

    /// <summary>B-09 search over the to-do text.</summary>
    public void ApplySearch(string query)
    {
        if (_query != query)
        {
            _query = query;
            Sync();
        }
    }

    public Task ToggleAsync(ChecklistItemViewModel item, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _queue.Enqueue(async () =>
        {
            if (item.IsChecked == isChecked || Find(item) is not { } todo)
            {
                return;
            }

            item.IsPending = true;
            ActionError = await _queue.RunAsync(op => _context.UseCases.SetGeneralTodoCompleted.ExecuteAsync(
                new SetGeneralTodoCompletedCommand(op, todo.Id, todo.Revision, isChecked), CancellationToken.None)).ConfigureAwait(true);
            item.IsPending = false;
            if (ActionError is not null)
            {
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
            if (Find(item) is not { } todo)
            {
                return;
            }

            item.IsPending = true;
            error = await _queue.RunAsync(op => _context.UseCases.RenameGeneralTodo.ExecuteAsync(
                new RenameGeneralTodoCommand(op, todo.Id, todo.Revision, text), CancellationToken.None)).ConfigureAwait(true);
            item.IsPending = false;
        }).ConfigureAwait(true);
        return error;
    }

    public bool CanMove(ChecklistItemViewModel item, bool up) => ChecklistSections.Neighbour(_openItems, _completedItems, item, up) is not null;

    public Task MoveAsync(ChecklistItemViewModel item, bool up)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _queue.Enqueue(async () =>
        {
            if (ChecklistSections.Neighbour(_openItems, _completedItems, item, up) is not { } anchor || Find(item) is not { } todo)
            {
                return;
            }

            item.IsPending = true;
            ActionError = await _queue.RunAsync(op => _context.UseCases.MoveGeneralTodo.ExecuteAsync(
                new MoveGeneralTodoCommand(op, todo.Id, todo.Revision, new GeneralTodoId(anchor.Id), PlaceAfter: !up), CancellationToken.None)).ConfigureAwait(true);
            item.IsPending = false;
        });
    }

    public Task DeleteAsync(ChecklistItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _queue.Enqueue(async () =>
        {
            if (Find(item) is not { } todo)
            {
                return;
            }

            item.IsPending = true;
            ActionError = await _queue.RunAsync(op => _context.UseCases.DeleteGeneralTodo.ExecuteAsync(
                new DeleteGeneralTodoCommand(op, todo.Id, todo.Revision), CancellationToken.None)).ConfigureAwait(true);
            item.IsPending = false;
            if (ActionError is null)
            {
                _context.Announce(_context.Strings.Format("Checklists_ItemDeleted", item.Text));
            }
        });
    }

    /// <summary>General to-dos never become Subtask cards (B-16.3 applies to task items only).</summary>
    public Task PopOutAsync(ChecklistItemViewModel item) => Task.CompletedTask;

    public void Dispose() => _commits.Dispose();

    /// <summary>Enter in the Add box or its Add button. Blank text shows an inline error and creates nothing (B-09).</summary>
    [RelayCommand(CanExecute = nameof(CanAddItem))]
    private Task AddItemAsync() => _queue.Enqueue(async () =>
    {
        IsAdding = true;
        NewItemError = await _queue.RunAsync(op => _context.UseCases.AddGeneralTodo.ExecuteAsync(
            new AddGeneralTodoCommand(op, new GeneralTodoId(Guid.NewGuid()), NewItemText), CancellationToken.None)).ConfigureAwait(true);
        if (NewItemError is null)
        {
            NewItemText = string.Empty;
        }

        IsAdding = false;
    });

    private bool CanAddItem() => !IsAdding;

    private Domain.Checklists.GeneralTodo? Find(ChecklistItemViewModel item) =>
        _context.Store.GeneralTodos.Lookup(new GeneralTodoId(item.Id)) is { HasValue: true } todo ? todo.Value : null;

    private void Sync()
    {
        var all = _context.Store.GeneralTodos.Items
            .OrderBy(t => t.Rank)
            .ThenBy(t => t.Id.Value)
            .ToList();
        TotalCount = all.Count;
        DoneCount = all.Count(t => t.IsCompleted);
        OnPropertyChanged(nameof(OpenCount));
        var items = all
            .Where(t => _query.Length == 0 || t.Text.Value.Contains(_query, StringComparison.CurrentCultureIgnoreCase))
            .Select(t => (t.Id.Value, t.Text.Value, t.IsCompleted))
            .ToList();
        ChecklistSections.Sync(_openItems, _completedItems, items, (id, text, done) => new ChecklistItemViewModel(id, text, done, this, _context.Strings));
        OnPropertyChanged(nameof(CompletedHeader));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(MatchCount));
    }
}
