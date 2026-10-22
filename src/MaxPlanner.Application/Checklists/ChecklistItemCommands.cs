using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
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
