using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;

namespace MaxPlanner.Application.ChangeRequests;

/// <param name="ConfirmedTaskIds">The linked tasks (archived included) listed in the confirmation the user accepted.</param>
public sealed record DeleteChangeRequestCommand(
    OperationId OperationId,
    ChangeRequestId ChangeRequestId,
    long ExpectedRevision,
    IReadOnlyCollection<TaskId> ConfirmedTaskIds);

/// <summary>What a cascade deletion removed besides the CR itself, for the result message.</summary>
public sealed record CascadeDeletionSummary(int TaskCount, int ChecklistItemCount, int SubtaskCardCount);

/// <summary>
/// Deletes a CR with its links, every linked task (archived included), their checklist items, and
/// their Subtask cards in one transaction, writing a tombstone for every deleted ID (B-05, ADR 0007).
/// </summary>
public sealed class DeleteChangeRequest
{
    public const string OperationKind = "cr.delete-cascade";

    private readonly IMutationCoordinator _coordinator;

    public DeleteChangeRequest(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>
    /// The summary, or a <see cref="ValidationFailedError"/> with <c>cascade-scope-changed</c> when the
    /// linked tasks differ from the confirmed set, in which case nothing is deleted and the UI confirms again.
    /// </returns>
    public Task<AppResult<CommandOutcome<CascadeDeletionSummary>>> ExecuteAsync(DeleteChangeRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.ConfirmedTaskIds);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<CascadeDeletionSummary> Execute(IWriteTransaction tx, DeleteChangeRequestCommand command)
    {
        if (!TransactionGuards.TryLoadChangeRequest(tx, command.ChangeRequestId, command.ExpectedRevision, out var changeRequest, out var loadError))
        {
            return TxResult.Abort<CascadeDeletionSummary>(loadError);
        }

        var linkedTaskIds = tx.Links.ListTaskIds(changeRequest.Id);
        if (CrDeletionPolicy.EnsureConfirmedScope(linkedTaskIds, command.ConfirmedTaskIds) is { } scopeChanged)
        {
            return TransactionGuards.Reject<CascadeDeletionSummary>(scopeChanged);
        }

        int checklistItems = 0, subtaskCards = 0;
        foreach (var taskId in linkedTaskIds)
        {
            // The link's foreign key guarantees both rows exist; a missing one is storage corruption.
            var task = tx.Tasks.Find(taskId) ?? throw new InvalidOperationException($"Linked task {taskId} is missing.");
            var link = tx.Links.FindByTask(taskId) ?? throw new InvalidOperationException($"Link of task {taskId} is missing.");

            foreach (var card in tx.SubtaskCards.ListByParent(taskId))
            {
                tx.SubtaskCards.Delete(card.Id);
                TransactionGuards.Tombstone(tx, EntityKind.SubtaskCard, card.Id.Value);
                tx.Changes.DeleteSubtaskCard(card.Id);
                subtaskCards++;
            }

            tx.Links.DeleteForTask(taskId);
            tx.Changes.RemoveLink(link);

            foreach (var item in task.ChecklistItems)
            {
                TransactionGuards.Tombstone(tx, EntityKind.ChecklistItem, item.Id.Value);
            }

            checklistItems += task.ChecklistItems.Count;
            tx.Tasks.Delete(taskId);
            TransactionGuards.Tombstone(tx, EntityKind.Task, taskId.Value);
            tx.Changes.DeleteTask(taskId);
        }

        tx.ChangeRequests.Delete(changeRequest.Id);
        TransactionGuards.Tombstone(tx, EntityKind.ChangeRequest, changeRequest.Id.Value);
        tx.Changes.DeleteChangeRequest(changeRequest.Id);
        return TxResult.Commit(new CascadeDeletionSummary(linkedTaskIds.Count, checklistItems, subtaskCards));
    }
}
