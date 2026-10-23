using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Filters;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Grid;

/// <summary>Asks the view to move keyboard focus to a cell, optionally opening its editor.</summary>
public sealed record GridFocusRequest(GridRowViewModel Row, string Column, bool BeginEdit);

/// <summary>
/// The Grid page (B-07 Grid, design §4.3): tasks and Subtask cards as rows with every column of B-07 plus active
/// custom fields, typed cell editors, sortable and resizable columns, a column chooser, a persisted layout, and the
/// persisted Show archived toggle. Each edit is one Application command (T-07); the editor stays open until the
/// result is known and keeps its draft on failure (AC-07). Use on the UI thread.
/// </summary>
public sealed partial class GridPageViewModel : ObservableObject, IPageViewModel
{
    private static readonly TimeSpan CommittedDisplayTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkspaceContext _context;
    private readonly ProjectionView<WorkItemRecord, Guid, GridRowViewModel> _view;
    private readonly IDisposable _workflowSubscription;
    private readonly ObservableCollection<StatusOption> _statusOptions = [];
    private readonly ObservableCollection<PriorityOption> _priorityOptions = [];
    private readonly ObservableCollection<GridColumnViewModel> _columns = [];
    private WorkflowSnapshot? _workflow;
    private Guid? _pendingFocus;
    private string? _sortColumn;
    private bool _sortDescending;
    private readonly bool _isConstructed;

    public GridPageViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var store = context.Store;
        var preferences = context.Preferences.Current;
        ShowArchived = preferences.ShowArchivedTasks ?? false;
        Filters = context.TaskFilters;
        _view = new ProjectionView<WorkItemRecord, Guid, GridRowViewModel>(
            WorkItemRecord.Connect(store),
            store.Scheduler,
            context.UiScheduler,
            record => new GridRowViewModel(record, context.Strings, () => _workflow),
            GridRowComparer.Default,
            Membership(ShowArchived));
        _view.Initialized += (_, _) => UpdateState();
        ((INotifyCollectionChanged)_view.Items).CollectionChanged += OnRowsChanged;
        StatusOptions = new ReadOnlyObservableCollection<StatusOption>(_statusOptions);
        PriorityOptions = new ReadOnlyObservableCollection<PriorityOption>(_priorityOptions);
        Columns = new ReadOnlyObservableCollection<GridColumnViewModel>(_columns);
        ApplySavedLayout(preferences.GridLayout, workflow: null);
        _workflowSubscription = store.Workflow.ObserveOn(context.UiScheduler).Subscribe(OnWorkflow);
        _context.PropertyChanged += OnContextChanged;
        Filters.Changed += OnFiltersChanged;
        _isConstructed = true;
        ApplySort();
    }

    public event EventHandler<GridFocusRequest>? FocusRequested;

    /// <summary>Raised when the visible column set or its order changed; the view rebuilds its header and cells.</summary>
    public event EventHandler? LayoutChanged;

    public ReadOnlyObservableCollection<GridRowViewModel> Rows => _view.Items;

    public ReadOnlyObservableCollection<StatusOption> StatusOptions { get; }

    public ReadOnlyObservableCollection<PriorityOption> PriorityOptions { get; }

    /// <summary>Every column in configured order, hidden ones included (the column chooser lists them all).</summary>
    public ReadOnlyObservableCollection<GridColumnViewModel> Columns { get; }

    /// <summary>The visible columns in display order.</summary>
    public IReadOnlyList<GridColumnViewModel> VisibleColumns => [.. _columns.Where(c => c.IsVisible)];

    [ObservableProperty]
    public partial ViewState State { get; private set; } = ViewState.Loading;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>The shared Board/Grid task scope (B-10); a Dashboard drill-down replaces it (design §2.9).</summary>
    public TaskFilterScopeViewModel Filters { get; }

    /// <summary>The task search, shared with the Board (B-10).</summary>
    public string SearchText
    {
        get => Filters.SearchText;
        set => Filters.SearchText = value;
    }

    /// <summary>Design §2.5 Filtered-empty title: no match, or only archived tasks match.</summary>
    [ObservableProperty]
    public partial string FilteredTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FilteredMessage { get; private set; } = string.Empty;

    /// <summary>"Clear all filters", or "Show archived" when only archived tasks match (B-10, design §4.3).</summary>
    [ObservableProperty]
    public partial string FilteredActionText { get; private set; } = string.Empty;

    /// <summary>B-05: archived tasks (and their Subtask cards) are shown read-only when on; persisted, off by default.</summary>
    [ObservableProperty]
    public partial bool ShowArchived { get; set; }

    public bool IsEditingBlocked => _context.IsEditingBlocked;

    /// <summary>The visible column keys in display order (keyboard navigation and UIA use them).</summary>
    public IReadOnlyList<string> VisibleKeys => [.. _columns.Where(c => c.IsVisible).Select(c => c.Key)];

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

        var typed = column is GridColumns.Title or GridColumns.Subtitle or GridColumns.Service || GridColumns.IsCustom(column) ? initialText : null;
        row.BeginEdit(column, typed, _statusOptions, _priorityOptions);
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
    /// Enter, Tab, Save, or leaving the cell: commits the draft as one command. Returns true when the editor
    /// closed because the value was saved (or unchanged); false keeps the editor open with its draft and the
    /// error, which Tab uses to stay on the cell (B-07).
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

        IReadOnlyDictionary<FieldId, CustomValue>? customValues = null;
        if (row.DraftCustom is { } custom && row.BaseModel?.Task is { } owner)
        {
            if (!custom.TryGetValue(out var value))
            {
                row.FailDraft(custom.Error ?? _context.Strings.GetString("Error_Unexpected"));
                return false;
            }

            var values = owner.CustomValues.ToDictionary();
            if (value is null)
            {
                values.Remove(custom.Field.Id);
            }
            else
            {
                values[custom.Field.Id] = value;
            }

            customValues = values.AsReadOnly();
        }

        var operation = row.NextOperation();
        row.BeginSave();
        _context.SaveStatus.Begin();
        var (error, revision) = await ExecuteAsync(row, column, row.BaseModel!, operation, customValues);
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

    // ----- Columns (B-07: resize, reorder, show/hide, typed sort, persisted layout) -----

    /// <summary>Header click or Enter: Ascending → Descending → default order (design §4.3).</summary>
    public void CycleSort(string key)
    {
        if (_columns.FirstOrDefault(c => c.Key == key) is not { } column)
        {
            return;
        }

        (_sortColumn, _sortDescending) = column.Sort switch
        {
            GridSortDirection.None => (key, false),
            GridSortDirection.Ascending => (key, true),
            _ => ((string?)null, false),
        };
        ApplySort();
        SaveLayout();
    }

    /// <summary>Pointer drag or Alt+Left/Right (±16 epx) on a header; widths are clamped to 48–800 (design §4.3).</summary>
    public void SetWidth(string key, double width)
    {
        if (_columns.FirstOrDefault(c => c.Key == key) is { } column)
        {
            column.Width = GridColumns.Clamp(width);
            SaveLayout();
        }
    }

    /// <summary>Header drag or menu Move left/right, Ctrl+Shift+Left/Right: moves among the visible columns.</summary>
    public bool MoveColumn(string key, int offset)
    {
        var visible = VisibleColumns.ToList();
        var index = visible.FindIndex(c => c.Key == key);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= visible.Count)
        {
            return false;
        }

        var from = _columns.IndexOf(visible[index]);
        var to = _columns.IndexOf(visible[target]);
        _columns.Move(from, to);
        RaiseLayout();
        return true;
    }

    /// <summary>Column chooser (design §4.3). At least one column stays visible; visibility never changes results (B-10).</summary>
    public void SetVisible(string key, bool isVisible)
    {
        if (_columns.FirstOrDefault(c => c.Key == key) is not { } column || column.IsVisible == isVisible
            || (!isVisible && _columns.Count(c => c.IsVisible) <= 1))
        {
            return;
        }

        column.IsVisible = isVisible;
        if (!isVisible && _sortColumn == key)
        {
            _sortColumn = null;
            ApplySort();
        }

        RaiseLayout();
    }

    /// <summary>Column chooser Reset / overflow "Reset column layout": default order, widths, visibility, and sort.</summary>
    [RelayCommand]
    private void ResetLayout()
    {
        _sortColumn = null;
        _sortDescending = false;
        ApplySavedLayout(null, _workflow);
        ApplySort();
        RaiseLayout();
    }

    // ----- Row commands (design §4.3 row menu) -----

    /// <summary>
    /// Row ⋯ › Open (design §4.3): the task editor, or the Subtask card editor for a card row (design §5.2);
    /// also the S-breakpoint row-details route (T-08.1).
    /// </summary>
    public Task OpenRowAsync(GridRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Model.Card is { } card ? _context.Tasks.OpenSubtaskCardAsync(card.Id) : _context.Tasks.OpenAsync(row.Model.Task!.Id);
    }

    /// <summary>Row ⋯ › Open CR: the CR whose values the row inherits (B-02.4: inherited values are edited there).</summary>
    public Task OpenCrAsync(GridRowViewModel row) =>
        row?.Model.Cr.ChangeRequestId is { } id ? _context.ChangeRequests.OpenAsync(id) : Task.CompletedTask;

    /// <summary>Row ⋯ › Copy… (tasks only, B-06).</summary>
    public Task CopyRowAsync(GridRowViewModel row) =>
        row?.Model.Task is { } task ? _context.Tasks.CopyAsync(task.Id) : Task.CompletedTask;

    /// <summary>Row ⋯ › Link to CR… (unlinked tasks, archived ones included, B-03.6, B-05).</summary>
    public Task LinkRowAsync(GridRowViewModel row) =>
        row?.Model.Task is { Cr.ChangeRequestId: null } task ? _context.Tasks.LinkAsync(task.Id) : Task.CompletedTask;

    /// <summary>Row ⋯ › Add Subtask card (tasks only, B-16.3).</summary>
    public Task AddSubtaskCardAsync(GridRowViewModel row) =>
        row?.Model.Task is { IsArchived: false } task ? _context.Tasks.AddSubtaskCardAsync(task) : Task.CompletedTask;

    /// <summary>Row ⋯ › Archive (tasks only, B-05).</summary>
    public Task<bool> ArchiveRowAsync(GridRowViewModel row) =>
        row?.Model.Task is { IsArchived: false } task ? _context.Tasks.ArchiveAsync(task) : Task.FromResult(false);

    /// <summary>Row ⋯ › Restore (archived tasks, B-05): back at the end of its Board column.</summary>
    public Task<bool> RestoreRowAsync(GridRowViewModel row) =>
        row?.Model.Task is { IsArchived: true } task ? _context.Tasks.RestoreAsync(task) : Task.FromResult(false);

    /// <summary>Row ⋯ › Delete… with the confirmation of design §5.7.</summary>
    public Task<bool> DeleteRowAsync(GridRowViewModel row) => row?.Model switch
    {
        { Task: { } task } => _context.Tasks.DeleteAsync(task),
        { Card: { } card } => _context.Tasks.DeleteSubtaskCardAsync(card),
        _ => Task.FromResult(false),
    };

    public void Dispose()
    {
        Filters.Changed -= OnFiltersChanged;
        _context.PropertyChanged -= OnContextChanged;
        ((INotifyCollectionChanged)_view.Items).CollectionChanged -= OnRowsChanged;
        _workflowSubscription.Dispose();
        _view.Dispose();
    }

    /// <summary>Replaces the row order; tests use it to exercise §7.7.</summary>
    internal void SetOrder(IComparer<GridRowViewModel> comparer) => _view.SetComparer(comparer);

    partial void OnShowArchivedChanged(bool value)
    {
        if (!_isConstructed)
        {
            return;
        }

        _view.SetFilter(Membership(value));
        _context.Preferences.Update(p => p with { ShowArchivedTasks = value });
        UpdateState();
    }

    /// <summary>
    /// The accent New task (design §4.3): creates a task in the default status through CreateTask, then focuses
    /// its Title cell with the editor open. B-02.2 requires a title, so the new task starts with the localized
    /// "New task" title; the full editor is one row-menu Open away.
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

    /// <summary>Archive visibility composes with the shared filters (B-10); column visibility never affects it.</summary>
    private Func<WorkItemRecord, bool> Membership(bool showArchived)
    {
        var filter = Filters.Predicate();
        return showArchived ? filter : record => record.IsActive && filter(record);
    }

    private void OnFiltersChanged(object? sender, EventArgs e)
    {
        _view.SetFilter(Membership(ShowArchived));
        OnPropertyChanged(nameof(SearchText));
        UpdateState();
    }

    /// <summary>The Filtered-empty action: Show archived when only archived tasks match, otherwise Clear all filters.</summary>
    [RelayCommand]
    private void ResolveFilteredEmpty()
    {
        if (!ShowArchived && OnlyArchivedMatch())
        {
            ShowArchived = true;
        }
        else
        {
            Filters.ClearAll();
        }
    }

    private bool OnlyArchivedMatch()
    {
        var filter = Filters.Predicate();
        return _context.Store.Tasks.Items.Any(t => t.IsArchived && filter(WorkItemRecord.From(t)))
            || _context.Store.SubtaskCards.Items.Any(c => c.IsParentArchived && filter(WorkItemRecord.From(c)));
    }

    private async Task<(AppError? Error, long Revision)> ExecuteAsync(
        GridRowViewModel row,
        string column,
        WorkItemRecord baseModel,
        OperationId operation,
        IReadOnlyDictionary<FieldId, CustomValue>? customValues)
    {
        var useCases = _context.UseCases;
        AppResult<CommandOutcome<long>> result = (baseModel.Task, baseModel.Card, column) switch
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

            // Every other task cell saves the whole task with one changed field, at the revision the edit began
            // from (UpdateTask: same status, so no transition and no automatic dates, B-04).
            ({ } task, _, _) => await useCases.UpdateTask.ExecuteAsync(TaskEdit(row, task, column, operation, customValues), CancellationToken.None),
            (_, { } card, GridColumns.Notes or GridColumns.StartDate or GridColumns.EndDate) => await useCases.UpdateSubtaskCard.ExecuteAsync(
                new UpdateSubtaskCardCommand(
                    operation,
                    card.Id,
                    card.Revision,
                    card.Subtitle,
                    column == GridColumns.Notes ? row.DraftText : card.Notes,
                    card.Status.Id,
                    column == GridColumns.StartDate ? DateIntentOf(row, card.StartDate) : DateIntent.Unchanged,
                    column == GridColumns.EndDate ? DateIntentOf(row, card.EndDate) : DateIntent.Unchanged),
                CancellationToken.None),
            _ => throw new InvalidOperationException($"Column {column} is not editable for this row."),
        };
        return result.IsSuccess ? (null, result.Value.Value) : (result.Error, 0);
    }

    private static UpdateTaskCommand TaskEdit(GridRowViewModel row, TaskReadModel task, string column, OperationId operation, IReadOnlyDictionary<FieldId, CustomValue>? customValues)
    {
        IReadOnlyList<PersonId> designers = [.. task.Designers.Select(p => p.Id)];
        IReadOnlyList<PersonId> developers = [.. task.Developers.Select(p => p.Id)];
        IReadOnlyList<TagId> tags = [.. task.Tags.Select(t => t.Id)];
        var newLookups = NewLookups.None;
        if (row.DraftTokens is { } tokens && row.DraftNewNames is { } names)
        {
            var ids = tokens.Ids;
            switch (column)
            {
                case GridColumns.Designers:
                    designers = [.. ids.Select(id => new PersonId(id))];
                    newLookups = new NewLookups([.. names.Used(ids).Select(n => new NewPerson(new PersonId(n.Id), n.Name))], []);
                    break;
                case GridColumns.Developers:
                    developers = [.. ids.Select(id => new PersonId(id))];
                    newLookups = new NewLookups([.. names.Used(ids).Select(n => new NewPerson(new PersonId(n.Id), n.Name))], []);
                    break;
                case GridColumns.Tags:
                    tags = [.. ids.Select(id => new TagId(id))];
                    newLookups = new NewLookups([], [.. names.Used(ids).Select(n => new NewTag(new TagId(n.Id), n.Name))]);
                    break;
                default:
                    break;
            }
        }

        var content = new TaskContentDraft(
            task.IsTitleInherited ? null : task.EffectiveTitle,
            task.Service,
            designers,
            developers,
            tags,
            column == GridColumns.Notes ? row.DraftText : task.Notes,
            customValues ?? task.CustomValues);
        return new UpdateTaskCommand(
            operation,
            task.Id,
            task.Revision,
            content,
            task.Status.Id,
            column == GridColumns.Priority && row.DraftPriority is { } priority ? priority.Id : task.Priority.Id,
            column == GridColumns.StartDate ? DateIntentOf(row, task.StartDate) : DateIntent.Unchanged,
            column == GridColumns.EndDate ? DateIntentOf(row, task.EndDate) : DateIntent.Unchanged,
            [.. task.ChecklistItems.Select(i => new ChecklistItemDraft(i.Id, i.Text, i.IsCompleted))],
            newLookups);
    }

    private static DateIntent DateIntentOf(GridRowViewModel row, DateOnly? committed) =>
        DraftDates.Intent(DraftDates.FromPicker(row.DraftDate), committed, row.DraftDateCleared);

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

    private void OnWorkflow(WorkflowSnapshot workflow)
    {
        var isFirst = _workflow is null;
        _workflow = workflow;
        KeyedCollectionSync.Sync(
            _statusOptions,
            [.. workflow.TaskStatuses.OrderBy(s => s.Order).Select(s => new StatusOption(s.Id, s.Name.Display))],
            option => option.Id,
            option => option.Id,
            option => option,
            (_, _) => { });
        _priorityOptions.Clear();
        foreach (var priority in workflow.Priorities.OrderBy(p => p.Order))
        {
            _priorityOptions.Add(new PriorityOption(priority.Id, priority.Name.Display));
        }

        // The first workflow brings the custom fields, so the saved layout is applied again with their saved positions.
        if (isFirst)
        {
            ApplySavedLayout(_context.Preferences.Current.GridLayout, workflow);
            RaiseLayout();
        }
        else if (SyncCustomColumns(workflow))
        {
            RaiseLayout();
        }

        foreach (var row in Rows)
        {
            row.RaiseDisplayChanged();
        }
    }

    /// <summary>
    /// Adds a column for each new active task field and drops columns of deactivated fields (B-07: removing a
    /// custom field must not break the saved layout). Returns true when the column set changed.
    /// </summary>
    private bool SyncCustomColumns(WorkflowSnapshot workflow)
    {
        var fields = workflow.FieldsFor(FieldTarget.Task).Where(f => f.IsActive).OrderBy(f => f.DisplayOrder).ThenBy(f => f.Id.Value).ToList();
        var keys = fields.Select(f => GridColumns.Custom(f.Id)).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        for (var i = _columns.Count - 1; i >= 0; i--)
        {
            if (_columns[i].IsCustom && !keys.Contains(_columns[i].Key))
            {
                if (_sortColumn == _columns[i].Key)
                {
                    _sortColumn = null;
                    ApplySort();
                }

                _columns.RemoveAt(i);
                changed = true;
            }
        }

        var saved = _context.Preferences.Current.GridLayout?.Columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var key = GridColumns.Custom(field.Id);
            if (_columns.FirstOrDefault(c => c.Key == key) is { } existing)
            {
                if (existing.Header != field.Label.Display)
                {
                    existing.UpdateField(field, field.Label.Display);
                    UpdateHeaderName(existing);
                    changed = true;
                }

                continue;
            }

            var column = new GridColumnViewModel(key, field.Label.Display, field);
            if (saved?.GetValueOrDefault(key) is { } preference)
            {
                column.Width = GridColumns.Clamp(preference.Width);
                column.IsVisible = preference.IsVisible;
            }

            UpdateHeaderName(column);
            _columns.Add(column);
            changed = true;
        }

        if (_sortColumn is { } sort && GridColumns.IsCustom(sort))
        {
            ApplySort();
        }

        return changed;
    }

    /// <summary>
    /// Builds the column list from a saved layout (T-09): saved keys first in their saved order, then any column the
    /// layout does not know in default order. Unknown saved keys are dropped and widths are clamped to 48–800.
    /// </summary>
    private void ApplySavedLayout(GridLayoutPreference? layout, WorkflowSnapshot? workflow)
    {
        var strings = _context.Strings;
        var builtIn = GridColumns.Default.ToDictionary(k => k, k => new GridColumnViewModel(k, strings.GetString("Grid_Column_" + k), null), StringComparer.Ordinal);
        var custom = workflow?.FieldsFor(FieldTarget.Task).Where(f => f.IsActive).OrderBy(f => f.DisplayOrder).ThenBy(f => f.Id.Value)
            .Select(f => new GridColumnViewModel(GridColumns.Custom(f.Id), f.Label.Display, f)).ToList() ?? [];
        var all = builtIn.Values.Concat(custom).ToDictionary(c => c.Key, StringComparer.Ordinal);
        var ordered = new List<GridColumnViewModel>();
        foreach (var saved in layout?.Columns ?? [])
        {
            if (all.Remove(saved.Key, out var column))
            {
                column.Width = GridColumns.Clamp(saved.Width);
                column.IsVisible = saved.IsVisible;
                ordered.Add(column);
            }
        }

        ordered.AddRange(GridColumns.Default.Where(all.ContainsKey).Select(k => all[k]));
        ordered.AddRange(custom.Where(c => all.ContainsKey(c.Key)));
        if (!ordered.Any(c => c.IsVisible))
        {
            ordered[0].IsVisible = true;
        }

        _columns.Clear();
        foreach (var column in ordered)
        {
            UpdateHeaderName(column);
            _columns.Add(column);
        }

        if (layout is { SortColumnKey: { } sortKey } && _columns.Any(c => c.Key == sortKey))
        {
            _sortColumn = sortKey;
            _sortDescending = layout.SortDescending;
            ApplySort();
        }
    }

    private void ApplySort()
    {
        foreach (var column in _columns)
        {
            column.Sort = column.Key != _sortColumn ? GridSortDirection.None : _sortDescending ? GridSortDirection.Descending : GridSortDirection.Ascending;
            UpdateHeaderName(column);
        }

        var field = _columns.FirstOrDefault(c => c.Key == _sortColumn)?.Field;
        if (!_isConstructed)
        {
            return;
        }

        _view.SetComparer(_sortColumn is null ? GridRowComparer.Default : new GridRowComparer(_sortColumn, _sortDescending, field));
        UpdateState();
    }

    private void UpdateHeaderName(GridColumnViewModel column)
    {
        var state = _context.Strings.GetString(column.Sort switch
        {
            GridSortDirection.Ascending => "Grid_SortStateAscending",
            GridSortDirection.Descending => "Grid_SortStateDescending",
            _ => "Grid_SortStateNone",
        });
        column.AccessibleName = _context.Strings.Format("Grid_HeaderName", column.Header, state);
    }

    private void RaiseLayout()
    {
        SaveLayout();
        OnPropertyChanged(nameof(VisibleColumns));
        OnPropertyChanged(nameof(VisibleKeys));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>T-09: the layout is a UI preference, saved independently of the data (B-07).</summary>
    private void SaveLayout()
    {
        var layout = new GridLayoutPreference(
            [.. _columns.Select(c => new GridColumnPreference(c.Key, c.Width, c.IsVisible))],
            _sortColumn,
            _sortDescending);
        _context.Preferences.Update(p => p with { GridLayout = layout });
    }

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
        if (!_isConstructed)
        {
            return;
        }

        var count = Rows.Count;
        var strings = _context.Strings;
        var onlyArchived = count == 0 && !ShowArchived && OnlyArchivedMatch();
        State = !_view.IsInitialized ? ViewState.Loading
            : count > 0 ? ViewState.Populated
            : Filters.IsFiltered || onlyArchived ? ViewState.FilteredEmpty
            : ViewState.Empty;
        if (State == ViewState.FilteredEmpty)
        {
            // B-10, design §4.3: "Only archived tasks match — turn on Show archived" is a different way out than clearing.
            FilteredTitle = strings.GetString(onlyArchived ? (Filters.IsFiltered ? "Grid_OnlyArchivedMatchTitle" : "Grid_OnlyArchivedTitle") : "Filters_NoMatchesTitle");
            FilteredMessage = onlyArchived ? strings.GetString("Grid_OnlyArchivedMessage") : string.Join(" · ", Filters.Chips.Select(c => c.Label));
            FilteredActionText = strings.GetString(onlyArchived ? "Grid_ShowArchivedAction" : "Filters_ClearAllAction");
        }

        var sort = _columns.FirstOrDefault(c => c.Key == _sortColumn) is { } sorted
            ? strings.Format(_sortDescending ? "Grid_SummarySortDescending" : "Grid_SummarySortAscending", sorted.Header)
            : strings.GetString("Grid_SummarySortDefault");
        var archived = strings.GetString(ShowArchived ? "Grid_SummaryArchivedShown" : "Grid_SummaryArchivedHidden");
        Summary = Filters.IsFiltered
            ? strings.Format("Grid_SummaryFiltered", count, ShowArchived ? _context.Store.Tasks.Count + _context.Store.SubtaskCards.Count : _context.Store.Tasks.Items.Count(t => !t.IsArchived) + _context.Store.SubtaskCards.Items.Count(c => !c.IsParentArchived), sort, archived)
            : strings.Format("Grid_Summary", count, sort, archived);
    }
}
