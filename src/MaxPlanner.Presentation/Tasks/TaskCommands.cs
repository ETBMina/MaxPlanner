using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>
/// The task and Subtask card commands every page offers (B-02.1, B-05, B-06, design §4.2 card menu, §4.3 row
/// menu): open, new, copy, archive, restore, delete, and Subtask card add/delete. One route per rule (P8):
/// each runs one use case, reports the actual result in the save status, and announces it. Use on the UI thread.
/// </summary>
public sealed class TaskCommands
{
    private readonly WorkspaceContext _context;

    internal TaskCommands(WorkspaceContext context) => _context = context;

    /// <summary>Opens the task editor for an existing task (Board Enter/Open, Grid row Open, Checklists Open task).</summary>
    public Task OpenAsync(TaskId taskId) => RunEditorAsync(new TaskEditorViewModel(_context, taskId, initialStatus: null));

    /// <summary>Opens the task editor for a new task; Board creation preselects the column's status (B-02.1).</summary>
    public Task NewAsync(TaskStatusId? status = null) => RunEditorAsync(new TaskEditorViewModel(_context, taskId: null, status));

    /// <summary>Opens the Copy dialog (B-06, design §5.5).</summary>
    public async Task CopyAsync(TaskId sourceId)
    {
        if (_context.Store.Tasks.Lookup(sourceId) is not { HasValue: true } source)
        {
            return;
        }

        var copy = new CopyTaskViewModel(_context, source.Value);
        await _context.ShowDialogAsync(copy).ConfigureAwait(true);
        if (copy.CreatedTaskId is not null)
        {
            _context.Announce(_context.Strings.Format("Copy_Created", copy.Title));
        }
    }

    /// <summary>Archive is reversible, so it runs without a confirmation (B-05); the result is announced.</summary>
    public async Task<bool> ArchiveAsync(TaskReadModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var outcome = await RunAsync("Record_Task", op => _context.UseCases.ArchiveTask.ExecuteAsync(
            new ArchiveTaskCommand(op, task.Id, task.Revision), CancellationToken.None)).ConfigureAwait(true);
        if (outcome is not null)
        {
            _context.Announce(_context.Strings.Format("Task_Archived", task.EffectiveTitle));
        }

        return outcome is not null;
    }

    public async Task<bool> RestoreAsync(TaskReadModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var outcome = await RunAsync("Record_Task", op => _context.UseCases.RestoreTask.ExecuteAsync(
            new RestoreTaskCommand(op, task.Id, task.Revision), CancellationToken.None)).ConfigureAwait(true);
        if (outcome is not null)
        {
            _context.Announce(_context.Strings.Format("Task_Restored", task.EffectiveTitle));
        }

        return outcome is not null;
    }

    /// <summary>
    /// Design §5.7: names the task and what goes with it, offers Archive as the reversible alternative, and
    /// defaults to Cancel. The confirmed revision is sent, so content changed meanwhile is a conflict.
    /// </summary>
    public async Task<bool> DeleteAsync(TaskReadModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var strings = _context.Strings;
        var message = strings.Format(task.Cr.CrNumber is null ? "DeleteTask_Message" : "DeleteTask_MessageLinked",
            task.EffectiveTitle, task.ChecklistTotal, task.SubtaskCardCount);
        var confirmation = new ConfirmationViewModel(
            "DeleteTask_Dialog",
            strings.GetString("DeleteTask_Title"),
            message,
            strings.GetString("DeleteTask_DeleteButton"),
            task.IsArchived ? null : strings.GetString("DeleteTask_ArchiveInsteadButton"),
            strings.GetString("Common_Cancel"));
        await _context.ShowDialogAsync(confirmation).ConfigureAwait(true);
        switch (confirmation.Result)
        {
            case ConfirmationResult.Primary:
                var outcome = await RunAsync("Record_Task", op => _context.UseCases.DeleteTask.ExecuteAsync(
                    new DeleteTaskCommand(op, task.Id, task.Revision), CancellationToken.None)).ConfigureAwait(true);
                if (outcome is not null)
                {
                    _context.Announce(strings.Format("Task_Deleted", task.EffectiveTitle));
                }

                return outcome is not null;
            case ConfirmationResult.Secondary:
                return await ArchiveAsync(task).ConfigureAwait(true);
            default:
                return false;
        }
    }

    /// <summary>Asks for a subtitle and creates the Subtask card in the default status (B-16.3, Board card menu).</summary>
    public Task AddSubtaskCardAsync(TaskReadModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return _context.ShowDialogAsync(new SubtaskCardPromptViewModel(_context, task));
    }

    public async Task<bool> DeleteSubtaskCardAsync(SubtaskCardReadModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var strings = _context.Strings;
        var confirmation = new ConfirmationViewModel(
            "DeleteSubtaskCard_Dialog",
            strings.GetString("DeleteSubtaskCard_Title"),
            strings.Format("DeleteSubtaskCard_Message", card.Subtitle),
            strings.GetString("DeleteSubtaskCard_DeleteButton"),
            null,
            strings.GetString("Common_Cancel"));
        await _context.ShowDialogAsync(confirmation).ConfigureAwait(true);
        if (confirmation.Result != ConfirmationResult.Primary)
        {
            return false;
        }

        var outcome = await RunAsync("Record_SubtaskCard", op => _context.UseCases.DeleteSubtaskCard.ExecuteAsync(
            new DeleteSubtaskCardCommand(op, card.Id, card.Revision), CancellationToken.None)).ConfigureAwait(true);
        if (outcome is not null)
        {
            _context.Announce(strings.Format("SubtaskCard_Deleted", card.Subtitle));
        }

        return outcome is not null;
    }

    /// <summary>
    /// Runs one command with the shared save indicator: Saving while it runs, then the actual result. A
    /// failure is reported and announced and returns null; nothing is retried (T-07).
    /// </summary>
    internal async Task<CommandOutcome<T>?> RunAsync<T>(string recordKey, Func<OperationId, Task<AppResult<CommandOutcome<T>>>> command)
    {
        if (_context.IsEditingBlocked)
        {
            _context.Announce(_context.Strings.GetString("Error_EditingBlocked"), isFailure: true);
            return null;
        }

        var operation = OperationId.New();
        _context.SaveStatus.Begin();
        var result = await command(operation).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, recordKey, _context.Strings);
            _context.SaveStatus.Report(failure);
            _context.Announce(failure.Message, isFailure: true);
            return null;
        }

        _context.SaveStatus.Complete();
        return result.Value;
    }

    private async Task RunEditorAsync(TaskEditorViewModel editor)
    {
        using (editor)
        {
            await _context.ShowDialogAsync(editor).ConfigureAwait(true);
        }

        // Dialogs never stack (design §5.0): a follow-up chosen in the editor's More menu opens only now.
        switch (editor.FollowUp)
        {
            case EditorFollowUp.Copy when editor.ExistingTaskId is { } copyId:
                await CopyAsync(copyId).ConfigureAwait(true);
                break;
            case EditorFollowUp.Delete when editor.ExistingTaskId is { } deleteId && _context.Store.Tasks.Lookup(deleteId) is { HasValue: true } task:
                await DeleteAsync(task.Value).ConfigureAwait(true);
                break;
            default:
                break;
        }
    }
}

/// <summary>
/// "Add Subtask card" from the Board card menu (B-16.3): one required subtitle, created in the default status.
/// </summary>
public sealed partial class SubtaskCardPromptViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IDialogViewModel
{
    private readonly WorkspaceContext _context;
    private readonly TaskReadModel _parent;

    public SubtaskCardPromptViewModel(WorkspaceContext context, TaskReadModel parent)
    {
        _context = context;
        _parent = parent;
    }

    public event EventHandler? CloseRequested;

    public string Caption => _context.Strings.Format("Board_SubtaskOf", _parent.EffectiveTitle);

    public string DialogTitle => _context.Strings.GetString("SubtaskPrompt_Title");

    public string SubtitleHeader => _context.Strings.GetString("SubtaskPrompt_SubtitleHeader");

    public string AddButtonText => _context.Strings.GetString("SubtaskPrompt_AddButton");

    public string CancelText => _context.Strings.GetString("Common_Cancel");

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string? Error { get; private set; }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool IsSaving { get; private set; }

    public SubtaskCardId? CreatedId { get; private set; }

    /// <summary>Creates the card; on success the dialog closes, otherwise the error stays beside the field.</summary>
    public async Task<bool> CreateAsync()
    {
        if (IsSaving)
        {
            return false;
        }

        IsSaving = true;
        Error = null;
        var id = new SubtaskCardId(Guid.NewGuid());
        var operation = OperationId.New();
        _context.SaveStatus.Begin();
        var result = await _context.UseCases.CreateSubtaskCard.ExecuteAsync(
            new CreateSubtaskCardCommand(operation, _parent.Id, id, Subtitle), CancellationToken.None).ConfigureAwait(true);
        IsSaving = false;
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, "Record_SubtaskCard", _context.Strings);
            _context.SaveStatus.Report(failure);
            Error = failure.Message;
            _context.Announce(failure.Message, isFailure: true);
            return false;
        }

        _context.SaveStatus.Complete();
        CreatedId = id;
        _context.Announce(_context.Strings.Format("SubtaskCard_Created", Subtitle.Trim()));
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
