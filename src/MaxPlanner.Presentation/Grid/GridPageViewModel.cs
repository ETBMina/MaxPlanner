using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Grid;

/// <summary>Asks the view to move keyboard focus to a cell, optionally opening its editor.</summary>
public sealed record GridFocusRequest(GridRowViewModel Row, string Column, bool BeginEdit);

/// <summary>
/// The Grid page (B-07 Grid, design §4.3), Phase 2 subset: tasks and Subtask cards as rows, with inline
/// edits of Title, Subtitle, Service, and Status. Each edit is one Application command (T-07); the editor
/// stays open until the result is known and keeps its draft on failure (AC-07). Use on the UI thread.
/// </summary>
public sealed partial class GridPageViewModel : ObservableObject, IPageViewModel
{
    private static readonly TimeSpan CommittedDisplayTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkspaceContext _context;
    private readonly ProjectionView<WorkItemRecord, Guid, GridRowViewModel> _view;
    private readonly IDisposable _workflowSubscription;
    private readonly ObservableCollection<StatusOption> _statusOptions = [];
    private Guid? _pendingFocus;

    public GridPageViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var store = context.Store;
        _view = new ProjectionView<WorkItemRecord, Guid, GridRowViewModel>(
            WorkItemRecord.Connect(store),
            store.Scheduler,
            context.UiScheduler,
            record => new GridRowViewModel(record, context.Strings),
            DefaultOrder.Instance,
            record => record.IsActive);
        _view.Initialized += (_, _) => UpdateState();
        ((INotifyCollectionChanged)_view.Items).CollectionChanged += OnRowsChanged;
        _workflowSubscription = store.Workflow.ObserveOn(context.UiScheduler).Subscribe(OnWorkflow);
        _context.PropertyChanged += OnContextChanged;
        StatusOptions = new ReadOnlyObservableCollection<StatusOption>(_statusOptions);
    }

    public event EventHandler<GridFocusRequest>? FocusRequested;

    public ReadOnlyObservableCollection<GridRowViewModel> Rows => _view.Items;

    public ReadOnlyObservableCollection<StatusOption> StatusOptions { get; }

    [ObservableProperty]
    public partial ViewState State { get; private set; } = ViewState.Loading;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    public bool IsEditingBlocked => _context.IsEditingBlocked;

    /// <summary>Opens a cell editor. Refused while editing is blocked (T-07) or when the cell is read-only (B-07).</summary>
    /// <param name="initialText">The typed character that started the edit, replacing the value (design §4.3).</param>
    public bool BeginEdit(GridRowViewModel row, string column, string? initialText = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsSaving || !row.CanEdit(column))
        {
            return false;
        }

        if (_context.IsEditingBlocked)
        {
            row.ShowError(_context.Strings.GetString("Error_EditingBlocked"));
            return false;
        }

        row.BeginEdit(column, column == GridColumns.Status ? null : initialText, _statusOptions);
        return true;
    }

    /// <summary>Esc: discards the draft. Ignored while the command runs, because its outcome is not known yet.</summary>
    public void CancelEdit(GridRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsSaving)
        {
            EndEdit(row);
        }
    }

    /// <summary>
    /// Enter, Tab, or leaving the cell: commits the draft as one command. Returns true when the editor
    /// closed because the value was saved (or unchanged); false keeps the editor open with its draft and
    /// the error, which Tab uses to stay on the cell (B-07).
    /// </summary>
    public async Task<bool> CommitAsync(GridRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.EditingColumn is not { } column || row.IsSaving)
        {
            return false;
        }

        if (!row.IsDraftChanged())
        {
            EndEdit(row);
            return true;
        }

        if (_context.IsEditingBlocked)
        {
            row.ShowError(_context.Strings.GetString("Error_EditingBlocked"));
            return false;
        }

        var operation = row.NextOperation();
        row.BeginSave();
        _context.SaveStatus.Begin();
        var (error, revision) = await ExecuteAsync(row, column, row.BaseModel!, operation);
        if (error is not null)
        {
            var failure = CommandFailure.From(error, operation, row.IsTask ? "Record_Task" : "Record_SubtaskCard", _context.Strings);
            _context.SaveStatus.Report(failure);
            row.FailSave(failure);
            return false;
        }

        _context.SaveStatus.Complete();

        // Close the editor only once the committed value is displayed (T-07: show committed state only). The
        // bound covers a row that leaves the list meanwhile; a resync replaces it with the committed value too.
        await Task.WhenAny(row.WaitForRevisionAsync(revision), Task.Delay(CommittedDisplayTimeout));
        EndEdit(row);
        return true;
    }

    /// <summary>Conflict "Use mine": keeps the draft and commits it against the current revision (design §5.10).</summary>
    public Task<bool> UseMineAsync(GridRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.HasConflict)
        {
            return Task.FromResult(false);
        }

        row.Rebase();
        return CommitAsync(row);
    }

    /// <summary>"Saved now: …" for the single-cell conflict popup (design §5.10).</summary>
    public string FormatSavedNow(string savedValue) => _context.Strings.Format("Grid_ConflictSavedNow", savedValue);

    /// <summary>"Your change: …" for the single-cell conflict popup (design §5.10).</summary>
    public string FormatYourDraft(string draft) => _context.Strings.Format("Grid_ConflictYourDraft", draft);

    /// <summary>Conflict "Reload": discards the draft and shows the saved value (design §5.10).</summary>
    public void Reload(GridRowViewModel row) => CancelEdit(row);

    public void Dispose()
    {
        _context.PropertyChanged -= OnContextChanged;
        ((INotifyCollectionChanged)_view.Items).CollectionChanged -= OnRowsChanged;
        _workflowSubscription.Dispose();
        _view.Dispose();
    }

    /// <summary>Replaces the row order; Phase 9 sorting uses it, and tests use it to exercise §7.7.</summary>
    internal void SetOrder(IComparer<GridRowViewModel> comparer) => _view.SetComparer(comparer);

    /// <summary>
    /// The accent New task (design §4.3): creates a task in the default status through CreateTask, then
    /// focuses its Title cell with the editor open. B-02.2 requires a title, so the new task starts with
    /// the localized "New task" title (interim choice until the task editor arrives in Phase 4).
    /// </summary>
    [RelayCommand]
    private async Task NewTaskAsync()
    {
        if (_context.IsEditingBlocked)
        {
            return;
        }

        var id = new TaskId(Guid.NewGuid());
        var operation = OperationId.New();
        var draft = new TaskContentDraft(_context.Strings.GetString("Grid_NewTaskTitle"), null, [], [], [], null, CustomValue.NoValues);
        _context.SaveStatus.Begin();
        var result = await _context.UseCases.CreateTask.ExecuteAsync(new CreateTaskCommand(operation, id, draft), CancellationToken.None);
        if (!result.IsSuccess)
        {
            _context.SaveStatus.Report(CommandFailure.From(result.Error!, operation, "Record_Task", _context.Strings));
            return;
        }

        _context.SaveStatus.Complete();
        _pendingFocus = id.Value;
        RaisePendingFocus();
    }

    private async Task<(AppError? Error, long Revision)> ExecuteAsync(GridRowViewModel row, string column, WorkItemRecord baseModel, OperationId operation)
    {
        var useCases = _context.UseCases;
        var result = (baseModel.Task, baseModel.Card, column) switch
        {
            ({ } task, _, GridColumns.Title or GridColumns.Service) => await useCases.UpdateTaskContent.ExecuteAsync(
                new UpdateTaskContentCommand(operation, task.Id, task.Revision, ContentWith(task, column, row.DraftText)), CancellationToken.None),
            (_, { } card, GridColumns.Subtitle) => await useCases.UpdateSubtaskCardContent.ExecuteAsync(
                new UpdateSubtaskCardContentCommand(operation, card.Id, card.Revision, row.DraftText, card.Notes), CancellationToken.None),

            // B-07: a status change from the Grid appends to the end of the full destination column, the same
            // rule as a filtered Board move, so it never renumbers cards the user cannot see.
            ({ } task, _, GridColumns.Status) => await useCases.MoveTask.ExecuteAsync(
                new MoveTaskCommand(operation, task.Id, task.Revision, row.DraftStatus!.Id, BoardPlacement.AppendToEnd), CancellationToken.None),
            (_, { } card, GridColumns.Status) => await useCases.MoveSubtaskCard.ExecuteAsync(
                new MoveSubtaskCardCommand(operation, card.Id, card.Revision, row.DraftStatus!.Id, BoardPlacement.AppendToEnd), CancellationToken.None),
            _ => throw new InvalidOperationException($"Column {column} is not editable for this row."),
        };
        return result.IsSuccess ? (null, result.Value.Value) : (result.Error, 0);
    }

    /// <summary>
    /// UpdateTaskContent saves the whole editable content, so every field except the edited one is sent
    /// as it was when the edit began; the expected revision makes any later change a conflict instead.
    /// </summary>
    private static TaskContentDraft ContentWith(TaskReadModel task, string column, string draft) => new(
        column == GridColumns.Title ? draft : task.IsTitleInherited ? null : task.EffectiveTitle,
        column == GridColumns.Service ? draft : task.Service,
        [.. task.Designers.Select(p => p.Id)],
        [.. task.Developers.Select(p => p.Id)],
        [.. task.Tags.Select(t => t.Id)],
        task.Notes,
        task.CustomValues);

    private void EndEdit(GridRowViewModel row)
    {
        if (row.EndEdit())
        {
            _view.Resort();
        }
    }

    private void OnWorkflow(WorkflowSnapshot workflow) =>
        KeyedCollectionSync.Sync(
            _statusOptions,
            [.. workflow.TaskStatuses.OrderBy(s => s.Order).Select(s => new StatusOption(s.Id, s.Name.Display))],
            option => option.Id,
            option => option.Id,
            option => option,
            (_, _) => { });

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateState();
        RaisePendingFocus();
    }

    private void RaisePendingFocus()
    {
        if (_pendingFocus is not { } id || Rows.FirstOrDefault(r => r.Key == id) is not { } row)
        {
            return;
        }

        _pendingFocus = null;
        FocusRequested?.Invoke(this, new GridFocusRequest(row, GridColumns.Title, BeginEdit: BeginEdit(row, GridColumns.Title)));
    }

    private void OnContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceContext.IsEditingBlocked))
        {
            OnPropertyChanged(nameof(IsEditingBlocked));
        }
    }

    private void UpdateState()
    {
        var count = Rows.Count;
        State = !_view.IsInitialized ? ViewState.Loading
            : count > 0 ? ViewState.Populated
            : _context.Store.Tasks.Items.Any(t => t.IsArchived) ? ViewState.FilteredEmpty
            : ViewState.Empty;
        Summary = _context.Strings.Format("Grid_Summary", count);
    }

    /// <summary>B-07 default: CreatedAt descending, then ID, on the held sort snapshot (design §7.7).</summary>
    private sealed class DefaultOrder : IComparer<GridRowViewModel>
    {
        public static DefaultOrder Instance { get; } = new();

        public int Compare(GridRowViewModel? x, GridRowViewModel? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null || y is null)
            {
                return x is null ? -1 : 1;
            }

            var byCreated = y.SortModel.CreatedAtUtc.CompareTo(x.SortModel.CreatedAtUtc);
            return byCreated != 0 ? byCreated : x.SortModel.Id.CompareTo(y.SortModel.Id);
        }
    }
}
