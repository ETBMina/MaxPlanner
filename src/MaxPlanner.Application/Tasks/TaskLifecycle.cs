using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;

namespace MaxPlanner.Application.Tasks;

public sealed record ArchiveTaskCommand(OperationId OperationId, TaskId TaskId, long ExpectedRevision);

/// <summary>
/// Archives a task (B-05): a reversible visibility state, not completion. Content, link, status, checklist
/// items, and Subtask cards are kept; the task and its cards leave the active views.
/// </summary>
public sealed class ArchiveTask
{
    public const string OperationKind = "task.archive";

    private readonly IMutationCoordinator _coordinator;

    public ArchiveTask(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The task's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(ArchiveTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (!TransactionGuards.TryLoadTask(tx, command.TaskId, command.ExpectedRevision, out var task, out var error))
            {
                return TxResult.Abort<long>(error);
            }

            var archived = task.Archive(tx.UtcNow);
            if (archived.Error is { } invalid)
            {
                return TransactionGuards.Reject<long>(invalid);
            }

            tx.Tasks.Update(archived.Value);
            tx.Changes.UpsertTask(archived.Value);
            return TxResult.Commit(archived.Value.Revision);
        }, cancellationToken);
    }
}

public sealed record RestoreTaskCommand(OperationId OperationId, TaskId TaskId, long ExpectedRevision);

/// <summary>
/// Restores an archived task (B-05) at the end of its current Board column, deterministically; its Subtask
/// cards and checklist group reappear because they follow the parent's archive state.
/// </summary>
public sealed class RestoreTask
{
    public const string OperationKind = "task.restore";

    private readonly IMutationCoordinator _coordinator;

    public RestoreTask(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The task's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(RestoreTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (!TransactionGuards.TryLoadTask(tx, command.TaskId, command.ExpectedRevision, out var task, out var error))
            {
                return TxResult.Abort<long>(error);
            }

            // The column's maximum covers active cards only, so the archived task itself never counts.
            var restored = task.Restore(RankPolicy.After(tx.Tasks.MaxBoardRank(task.StatusId)), tx.UtcNow);
            if (restored.Error is { } invalid)
            {
                return TransactionGuards.Reject<long>(invalid);
            }

            tx.Tasks.Update(restored.Value);
            tx.Changes.UpsertTask(restored.Value);
            return TxResult.Commit(restored.Value.Revision);
        }, cancellationToken);
    }
}

public sealed record DeleteTaskCommand(OperationId OperationId, TaskId TaskId, long ExpectedRevision);

/// <summary>What a task deletion removed with the task, for the result message.</summary>
public sealed record TaskDeletionSummary(int ChecklistItemCount, int SubtaskCardCount);

/// <summary>
/// Permanently deletes a task with its checklist items, Subtask cards, and CR link in one transaction
/// (B-05, T-06.3). Its CR stays. Every deleted ID is tombstoned so a stale editor or import cannot bring
/// it back. The expected revision makes a confirmation shown for older content a conflict, so the
/// counts the user confirmed are the counts deleted.
/// </summary>
public sealed class DeleteTask
{
    public const string OperationKind = "task.delete";

    private readonly IMutationCoordinator _coordinator;

    public DeleteTask(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<TaskDeletionSummary>>> ExecuteAsync(DeleteTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (!TransactionGuards.TryLoadTask(tx, command.TaskId, command.ExpectedRevision, out var task, out var error))
            {
                return TxResult.Abort<TaskDeletionSummary>(error);
            }

            var cards = tx.SubtaskCards.ListByParent(task.Id);
            foreach (var card in cards)
            {
                tx.SubtaskCards.Delete(card.Id);
                TransactionGuards.Tombstone(tx, EntityKind.SubtaskCard, card.Id.Value);
                tx.Changes.DeleteSubtaskCard(card.Id);
            }

            if (tx.Links.FindByTask(task.Id) is { } link)
            {
                tx.Links.DeleteForTask(task.Id);
                tx.Changes.RemoveLink(link);
            }

            foreach (var item in task.ChecklistItems)
            {
                TransactionGuards.Tombstone(tx, EntityKind.ChecklistItem, item.Id.Value);
            }

            tx.Tasks.Delete(task.Id);
            TransactionGuards.Tombstone(tx, EntityKind.Task, task.Id.Value);
            tx.Changes.DeleteTask(task.Id);
            return TxResult.Commit(new TaskDeletionSummary(task.ChecklistItems.Count, cards.Count));
        }, cancellationToken);
    }
}
