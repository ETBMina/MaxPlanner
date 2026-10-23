using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.SubtaskCards;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>
/// The task and Subtask card commands every page offers (B-02.1, B-05, B-06, design §4.2 card menu, §4.3 row
/// menu): open, new, copy, archive, restore, delete, and Subtask card add/delete. One route per rule (P8):
/// each runs one use case, reports the actual result in the save status, and announces it. Use on the UI thread.
/// </summary>
public sealed class TaskCommands
{
    private static readonly TimeSpan PopOutDisplayTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkspaceContext _context;

    internal TaskCommands(WorkspaceContext context) => _context = context;

    /// <summary>Opens the task editor for an existing task (Board Enter/Open, Grid row Open, Checklists Open task).</summary>
    public Task OpenAsync(TaskId taskId) => RunEditorAsync(new TaskEditorViewModel(_context, taskId, initialStatus: null));

    /// <summary>Opens the task editor for a new task; Board creation preselects the column's status (B-02.1).</summary>
    public Task NewAsync(TaskStatusId? status = null) => RunEditorAsync(new TaskEditorViewModel(_context, taskId: null, status));

    /// <summary>
    /// "Create task" from a CR (B-03.5): a new task whose permanent link to the saved CR is pending, so the
    /// task and its link are created together by Save, with the inherited values of that CR (T-13).
    /// </summary>
    public Task NewLinkedAsync(ChangeRequestId changeRequestId) => RunEditorAsync(new TaskEditorViewModel(_context, taskId: null, initialStatus: null, changeRequestId));

    /// <summary>Opens the Subtask card editor (design §5.2): Board/Grid Open, the task editor's card list.</summary>
    public Task OpenSubtaskCardAsync(SubtaskCardId cardId) => RunSubtaskEditorAsync(SubtaskCardEditorViewModel.ForCard(_context, cardId));

    /// <summary>"Add Subtask card" (B-16.3): the Subtask card editor for a new card under <paramref name="parentId"/>.</summary>
    public Task NewSubtaskCardAsync(TaskId parentId, TaskStatusId? status = null) =>
        RunSubtaskEditorAsync(SubtaskCardEditorViewModel.ForNewCard(_context, parentId, status));

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

    /// <summary>Board/Grid card menu "Add Subtask card" (B-16.3): opens the Subtask card editor for a new card.</summary>
    public Task AddSubtaskCardAsync(TaskReadModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return NewSubtaskCardAsync(task.Id);
    }

    /// <summary>
    /// "Pop out to card" (B-16.3, design §4.5): one transaction removes the checklist item and creates a Subtask
    /// card from it. The save status then reads "Moved to a Subtask card in Backlog — Open card".
    /// </summary>
    /// <returns>The new card, or null when the command failed (reported in the save status).</returns>
    public async Task<SubtaskCardId?> PopOutAsync(TaskReadModel task, ChecklistItemId itemId)
    {
        ArgumentNullException.ThrowIfNull(task);
        var cardId = new SubtaskCardId(Guid.NewGuid());
        var outcome = await RunAsync(
            "Record_Task",
            op => _context.UseCases.PopOutChecklistItem.ExecuteAsync(new PopOutChecklistItemCommand(op, task.Id, task.Revision, itemId, cardId), CancellationToken.None),
            complete: _ => { }).ConfigureAwait(true);
        if (outcome is null)
        {
            return null;
        }

        // The status the use case chose (default, or the first completed one for a checked item) is read from
        // the committed card, so the message never guesses it.
        await _context.WhenDisplayedAsync(outcome.CommitSequence, PopOutDisplayTimeout).ConfigureAwait(true);
        var card = _context.Store.SubtaskCards.Lookup(cardId);
        var statusName = card.HasValue ? card.Value.Status.Name : string.Empty;
        var message = _context.Strings.Format("PopOut_Moved", statusName);
        _context.SaveStatus.Complete(message, _context.Strings.GetString("PopOut_OpenCard"), () => OpenSubtaskCardAsync(cardId));
        _context.Announce(_context.Strings.Format("PopOut_Announced", statusName));
        return cardId;
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
    /// <param name="complete">Replaces the plain "Saved" result when the caller reports its own result text.</param>
    internal async Task<CommandOutcome<T>?> RunAsync<T>(
        string recordKey,
        Func<OperationId, Task<AppResult<CommandOutcome<T>>>> command,
        Action<CommandOutcome<T>>? complete = null)
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

        if (complete is null)
        {
            _context.SaveStatus.Complete();
        }
        else
        {
            // The caller completes the indicator with its own result once it is displayed.
            complete(result.Value);
        }

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
            case EditorFollowUp.OpenCr when editor.LinkedCr is { } cr:
                await _context.ChangeRequests.OpenAsync(cr.Id).ConfigureAwait(true);
                break;
            case EditorFollowUp.OpenSubtaskCard when editor.FollowUpCardId is { } cardId:
                await OpenSubtaskCardAsync(cardId).ConfigureAwait(true);
                break;
            case EditorFollowUp.AddSubtaskCard when editor.ExistingTaskId is { } parentId:
                await NewSubtaskCardAsync(parentId).ConfigureAwait(true);
                break;
            default:
                break;
        }
    }

    private async Task RunSubtaskEditorAsync(SubtaskCardEditorViewModel editor)
    {
        using (editor)
        {
            await _context.ShowDialogAsync(editor).ConfigureAwait(true);
        }

        switch (editor.FollowUp)
        {
            case SubtaskEditorFollowUp.Delete when editor.Latest is { } card:
                await DeleteSubtaskCardAsync(card).ConfigureAwait(true);
                break;
            case SubtaskEditorFollowUp.OpenParent when editor.Parent is not null:
                await OpenAsync(editor.ParentTaskId).ConfigureAwait(true);
                break;
            default:
                break;
        }
    }
}
