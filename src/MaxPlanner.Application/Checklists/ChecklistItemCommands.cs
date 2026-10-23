using System.Diagnostics.CodeAnalysis;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Checklists;

public sealed record AddChecklistItemCommand(OperationId OperationId, TaskId TaskId, long ExpectedRevision, ChecklistItemId NewItemId, string? Text);

/// <summary>Appends a checklist item to a task (B-02.7). Items are edited through the task's revision (T-05).</summary>
public sealed class AddChecklistItem
{
    public const string OperationKind = "checklist-item.add";

    private readonly IMutationCoordinator _coordinator;

    public AddChecklistItem(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The task's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(AddChecklistItemCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = ChecklistText.Create(command.Text);
        if (text.Error is { } invalid)
        {
            return UseCaseResults.Failed<long>(new ValidationFailedError(invalid));
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command, text.Value), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, AddChecklistItemCommand command, ChecklistText text)
    {
        if (!TransactionGuards.TryLoadTask(tx, command.TaskId, command.ExpectedRevision, out var task, out var loadError))
        {
            return TxResult.Abort<long>(loadError);
        }

        var exists = task.ChecklistItems.Any(i => i.Id == command.NewItemId);
        if (TransactionGuards.EnsureNewId(tx, EntityKind.ChecklistItem, command.NewItemId.Value, exists) is { } idError)
        {
            return TxResult.Abort<long>(idError);
        }

        var updated = task.AddChecklistItem(command.NewItemId, text, tx.UtcNow);
        if (updated.Error is { } invalid)
        {
            return TransactionGuards.Reject<long>(invalid);
        }

        tx.Tasks.Update(updated.Value);
        tx.Changes.UpsertTask(updated.Value);
        return TxResult.Commit(updated.Value.Revision);
    }
}

public sealed record SetChecklistItemCompletedCommand(OperationId OperationId, TaskId TaskId, long ExpectedRevision, ChecklistItemId ItemId, bool IsCompleted);

/// <summary>Checks or unchecks a checklist item. Never changes the task's status or dates (B-04, B-09).</summary>
public sealed class SetChecklistItemCompleted
{
    public const string OperationKind = "checklist-item.set-completed";

    private readonly IMutationCoordinator _coordinator;

    public SetChecklistItemCompleted(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The task's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(SetChecklistItemCompletedCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, SetChecklistItemCompletedCommand command)
    {
        if (!TransactionGuards.TryLoadTask(tx, command.TaskId, command.ExpectedRevision, out var task, out var loadError))
        {
            return TxResult.Abort<long>(loadError);
        }

        if (task.ChecklistItems.All(i => i.Id != command.ItemId))
        {
            return TxResult.Abort<long>(TransactionGuards.NotFound(tx, EntityKind.ChecklistItem, command.ItemId.Value));
        }

        var updated = task.SetChecklistItemCompleted(command.ItemId, command.IsCompleted, tx.UtcNow);
        if (updated.Error is { } invalid)
        {
            return TransactionGuards.Reject<long>(invalid);
        }

        tx.Tasks.Update(updated.Value);
        tx.Changes.UpsertTask(updated.Value);
        return TxResult.Commit(updated.Value.Revision);
    }
}

public sealed record RenameChecklistItemCommand(OperationId OperationId, TaskId TaskId, long ExpectedRevision, ChecklistItemId ItemId, string? Text);

/// <summary>Renames a checklist item inline from Checklists (B-09). Blank text is refused; the item keeps its ID.</summary>
public sealed class RenameChecklistItem
{
    public const string OperationKind = "checklist-item.rename";

    private readonly IMutationCoordinator _coordinator;

    public RenameChecklistItem(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The task's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(RenameChecklistItemCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = ChecklistText.Create(command.Text);
        if (text.Error is { } invalid)
        {
            return UseCaseResults.Failed<long>(new ValidationFailedError(invalid));
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (!ChecklistTransactions.TryLoadItem(tx, command.TaskId, command.ExpectedRevision, command.ItemId, out var task, out var error))
            {
                return TxResult.Abort<long>(error);
            }

            return ChecklistTransactions.Commit(tx, task.EditChecklistItem(command.ItemId, text.Value, tx.UtcNow));
        }, cancellationToken);
    }
}

/// <param name="AnchorItemId">The item the moved item is placed next to (its visible neighbour for Move up/down).</param>
/// <param name="PlaceAfter">True to place after the anchor, false to place before it.</param>
public sealed record MoveChecklistItemCommand(OperationId OperationId, TaskId TaskId, long ExpectedRevision, ChecklistItemId ItemId, ChecklistItemId AnchorItemId, bool PlaceAfter);

/// <summary>Reorders a checklist item within its task (B-09: item order is explicit). Never changes completion.</summary>
public sealed class MoveChecklistItem
{
    public const string OperationKind = "checklist-item.move";

    private readonly IMutationCoordinator _coordinator;

    public MoveChecklistItem(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The task's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(MoveChecklistItemCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, MoveChecklistItemCommand command)
    {
        if (!ChecklistTransactions.TryLoadItem(tx, command.TaskId, command.ExpectedRevision, command.ItemId, out var task, out var error))
        {
            return TxResult.Abort<long>(error);
        }

        var ordered = ExplicitOrder.Sort(task.ChecklistItems.Select(i => (i.Id.Value, i.Rank)));
        if (ExplicitOrder.Place(ordered, command.ItemId.Value, command.AnchorItemId.Value, command.PlaceAfter) is not { } changes)
        {
            return TxResult.Abort<long>(TransactionGuards.NotFound(tx, EntityKind.ChecklistItem, command.AnchorItemId.Value));
        }

        // A renumber changes several items; each step bumps the task revision, and only the final task is written.
        var moved = DomainResult.Success(task);
        foreach (var (id, rank) in changes)
        {
            moved = moved.Error is null ? moved.Value.MoveChecklistItem(new ChecklistItemId(id), rank, tx.UtcNow) : moved;
        }

        return ChecklistTransactions.Commit(tx, moved);
    }
}

public sealed record DeleteChecklistItemCommand(OperationId OperationId, TaskId TaskId, long ExpectedRevision, ChecklistItemId ItemId);

/// <summary>
/// Deletes a checklist item after the Checklists flyout confirmation (B-05, B-09). The item's ID is
/// tombstoned so a stale editor or import cannot re-create it.
/// </summary>
public sealed class DeleteChecklistItem
{
    public const string OperationKind = "checklist-item.delete";

    private readonly IMutationCoordinator _coordinator;

    public DeleteChecklistItem(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The task's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(DeleteChecklistItemCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (!ChecklistTransactions.TryLoadItem(tx, command.TaskId, command.ExpectedRevision, command.ItemId, out var task, out var error))
            {
                return TxResult.Abort<long>(error);
            }

            var removed = task.RemoveChecklistItem(command.ItemId, tx.UtcNow);
            if (removed.Error is { } invalid)
            {
                return TransactionGuards.Reject<long>(invalid);
            }

            TransactionGuards.Tombstone(tx, EntityKind.ChecklistItem, command.ItemId.Value);
            return ChecklistTransactions.Commit(tx, DomainResult.Success(removed.Value.Task));
        }, cancellationToken);
    }
}

internal static class ChecklistTransactions
{
    /// <summary>Loads the task with a revision check and confirms that it still has the item.</summary>
    public static bool TryLoadItem(
        IWriteTransaction tx,
        TaskId taskId,
        long expectedRevision,
        ChecklistItemId itemId,
        [NotNullWhen(true)] out TaskItem? task,
        [NotNullWhen(false)] out AppError? error)
    {
        if (!TransactionGuards.TryLoadTask(tx, taskId, expectedRevision, out task, out error))
        {
            return false;
        }

        if (task.ChecklistItems.All(i => i.Id != itemId))
        {
            error = TransactionGuards.NotFound(tx, EntityKind.ChecklistItem, itemId.Value);
            task = null;
            return false;
        }

        return true;
    }

    public static TxResult<long> Commit(IWriteTransaction tx, DomainResult<TaskItem> updated)
    {
        if (updated.Error is { } invalid)
        {
            return TransactionGuards.Reject<long>(invalid);
        }

        tx.Tasks.Update(updated.Value);
        tx.Changes.UpsertTask(updated.Value);
        return TxResult.Commit(updated.Value.Revision);
    }
}
