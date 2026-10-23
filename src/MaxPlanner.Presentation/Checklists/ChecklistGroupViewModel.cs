using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Checklists;

/// <summary>One checklist item row. <see cref="IsChecked"/> is always the committed state (T-07).</summary>
public sealed partial class ChecklistItemViewModel : ObservableObject
{
    public ChecklistItemViewModel(ChecklistItemReadModel model) => Update(model);

    public ChecklistItemId Id { get; private set; }

    [ObservableProperty]
    public partial string Text { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsChecked { get; private set; }

    /// <summary>True while a toggle of this item is being saved.</summary>
    [ObservableProperty]
    public partial bool IsPending { get; internal set; }

    internal void Update(ChecklistItemReadModel model)
    {
        Id = model.Id;
        Text = model.Text;
        IsChecked = model.IsCompleted;
    }

    /// <summary>
    /// A failed toggle: the checkbox already shows the user's click, so the committed value is announced
    /// again to put the control back (x:Bind re-reads it on the notification).
    /// </summary>
    internal void RestoreCommitted() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>
/// A task's checklist group on the Checklists page (B-09, design §4.5): header, progress, incomplete items,
/// a Completed section, and the Add an item box. Commands go through the checklist use cases. Use on the
/// UI thread.
/// </summary>
/// <remarks>
/// Checklist items are edited through their task's revision (T-05), so commands of one group run one at a
/// time, each after the previous result has reached the store; two quick toggles then both succeed instead
/// of the second one conflicting with the first.
/// </remarks>
public sealed partial class ChecklistGroupViewModel : ProjectionItemViewModel<TaskReadModel, TaskId>
{
    private static readonly TimeSpan CommittedDisplayTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkspaceContext _context;
    private readonly ObservableCollection<ChecklistItemViewModel> _openItems = [];
    private readonly ObservableCollection<ChecklistItemViewModel> _completedItems = [];
    private Task _tail = Task.CompletedTask;
    private TaskCompletionSource? _revisionWaiter;
    private long _awaitedRevision;

    public ChecklistGroupViewModel(TaskReadModel model, WorkspaceContext context)
        : base(model)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        OpenItems = new ReadOnlyObservableCollection<ChecklistItemViewModel>(_openItems);
        CompletedItems = new ReadOnlyObservableCollection<ChecklistItemViewModel>(_completedItems);
        SyncItems();
    }

    public override TaskId Key => Model.Id;

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

    public string CompletedHeader => _context.Strings.Format("Checklists_CompletedHeader", Model.ChecklistDone);

    public bool HasCompleted => Model.ChecklistDone > 0;

    public bool HasOpenItems => Model.ChecklistDone < Model.ChecklistTotal;

    /// <summary>The Add an item box's draft; bound two-way and kept when adding fails.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddItemCommand))]
    public partial string NewItemText { get; set; } = string.Empty;

    /// <summary>The inline error under the Add box (blank text, conflict, storage).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewItemError))]
    public partial string? NewItemError { get; private set; }

    public bool HasNewItemError => NewItemError is not null;

    /// <summary>The last toggle failure for this group, shown above its items.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToggleError))]
    public partial string? ToggleError { get; private set; }

    public bool HasToggleError => ToggleError is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddItemCommand))]
    public partial bool IsAdding { get; private set; }

    /// <summary>Checks or unchecks an item from its checkbox click (B-09). Never changes the task's status or dates.</summary>
    public Task ToggleAsync(ChecklistItemViewModel item, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Enqueue(() => ToggleNowAsync(item, isChecked));
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
        OnPropertyChanged(nameof(CompletedHeader));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(HasOpenItems));
        if (_revisionWaiter is not null && Model.Revision >= _awaitedRevision)
        {
            var waiter = _revisionWaiter;
            _revisionWaiter = null;
            waiter.TrySetResult();
        }
    }

    private bool CanAddItem() => !IsAdding;

    /// <summary>Enter in the Add box, or its Add button: blank text is refused by the use case with an inline error (B-02.7).</summary>
    /// <summary>"Open task" (B-09, design §4.5): the task editor for this group's task.</summary>
    [RelayCommand]
    private Task OpenTask() => _context.Tasks.OpenAsync(Model.Id);

    /// <summary>"Open task Card limits API": the link's accessible name.</summary>
    public string OpenTaskName => _context.Strings.Format("Checklists_OpenTaskName", Model.EffectiveTitle);

    [RelayCommand(CanExecute = nameof(CanAddItem))]
    private Task AddItemAsync() => Enqueue(AddNowAsync);

    private async Task AddNowAsync()
    {
        if (_context.IsEditingBlocked)
        {
            NewItemError = _context.Strings.GetString("Error_EditingBlocked");
            return;
        }

        var operation = OperationId.New();
        IsAdding = true;
        _context.SaveStatus.Begin();
        var result = await _context.UseCases.AddChecklistItem.ExecuteAsync(
            new AddChecklistItemCommand(operation, Model.Id, Model.Revision, new ChecklistItemId(Guid.NewGuid()), NewItemText), CancellationToken.None);
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, "Record_Task", _context.Strings);
            _context.SaveStatus.Report(failure);
            NewItemError = failure.Message;
            IsAdding = false;
            return;
        }

        _context.SaveStatus.Complete();
        NewItemError = null;
        NewItemText = string.Empty;
        await WaitForRevisionAsync(result.Value.Value);
        IsAdding = false;
    }

    private async Task ToggleNowAsync(ChecklistItemViewModel item, bool isChecked)
    {
        if (item.IsChecked == isChecked)
        {
            return;
        }

        if (_context.IsEditingBlocked)
        {
            ToggleError = _context.Strings.GetString("Error_EditingBlocked");
            item.RestoreCommitted();
            return;
        }

        var operation = OperationId.New();
        item.IsPending = true;
        _context.SaveStatus.Begin();
        var result = await _context.UseCases.SetChecklistItemCompleted.ExecuteAsync(
            new SetChecklistItemCompletedCommand(operation, Model.Id, Model.Revision, item.Id, isChecked), CancellationToken.None);
        item.IsPending = false;
        if (!result.IsSuccess)
        {
            // AC-07: the shared state is unchanged, so the checkbox returns to the committed value.
            var failure = CommandFailure.From(result.Error!, operation, "Record_Task", _context.Strings);
            _context.SaveStatus.Report(failure);
            ToggleError = failure.Message;
            item.RestoreCommitted();
            return;
        }

        _context.SaveStatus.Complete();
        ToggleError = null;
        await WaitForRevisionAsync(result.Value.Value);
    }

    /// <summary>Runs <paramref name="command"/> after the group's previous command and its store update.</summary>
    private Task Enqueue(Func<Task> command)
    {
        var previous = _tail;
        var next = RunAfterAsync(previous, command);
        _tail = next;
        return next;
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> command)
    {
        await previous;
        await command();
    }

    private Task WaitForRevisionAsync(long revision)
    {
        if (Model.Revision >= revision)
        {
            return Task.CompletedTask;
        }

        _awaitedRevision = revision;
        _revisionWaiter = new TaskCompletionSource();

        // Bounded: a resync or the task leaving the page also ends the wait.
        return Task.WhenAny(_revisionWaiter.Task, Task.Delay(CommittedDisplayTimeout));
    }

    private void SyncItems()
    {
        Sync(_openItems, [.. Model.ChecklistItems.Where(i => !i.IsCompleted)]);
        Sync(_completedItems, [.. Model.ChecklistItems.Where(i => i.IsCompleted)]);
    }

    private static void Sync(ObservableCollection<ChecklistItemViewModel> target, IReadOnlyList<ChecklistItemReadModel> models) =>
        KeyedCollectionSync.Sync(target, models, m => m.Id, vm => vm.Id, m => new ChecklistItemViewModel(m), (vm, m) => vm.Update(m));
}
