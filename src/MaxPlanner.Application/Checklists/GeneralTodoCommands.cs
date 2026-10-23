using System.Diagnostics.CodeAnalysis;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Checklists;

public sealed record AddGeneralTodoCommand(OperationId OperationId, GeneralTodoId NewId, string? Text);

/// <summary>
/// Adds a General to-do at the end of the list (B-09). General to-dos are independent records: no task, CR,
/// or status. Blank text is refused, so the Add box shows an inline error and nothing is created.
/// </summary>
public sealed class AddGeneralTodo
{
    public const string OperationKind = "general-todo.add";

    private readonly IMutationCoordinator _coordinator;

    public AddGeneralTodo(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The new to-do's revision (1).</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(AddGeneralTodoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = ChecklistText.Create(command.Text);
        if (text.Error is { } invalid)
        {
            return UseCaseResults.Failed<long>(new ValidationFailedError(invalid));
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (TransactionGuards.EnsureNewId(tx, EntityKind.GeneralTodo, command.NewId.Value, tx.GeneralTodos.Find(command.NewId) is not null) is { } idError)
            {
                return TxResult.Abort<long>(idError);
            }

            var todo = GeneralTodo.Create(command.NewId, text.Value, RankPolicy.After(tx.GeneralTodos.MaxRank()), tx.UtcNow);
            tx.GeneralTodos.Insert(todo);
            tx.Changes.UpsertGeneralTodo(todo);
            return TxResult.Commit(todo.Revision);
        }, cancellationToken);
    }
}

public sealed record SetGeneralTodoCompletedCommand(OperationId OperationId, GeneralTodoId TodoId, long ExpectedRevision, bool IsCompleted);

/// <summary>Checks or unchecks a General to-do (B-09: completed items move to the Completed section and can be unchecked).</summary>
public sealed class SetGeneralTodoCompleted
{
    public const string OperationKind = "general-todo.set-completed";

    private readonly IMutationCoordinator _coordinator;

    public SetGeneralTodoCompleted(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The to-do's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(SetGeneralTodoCompletedCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
            GeneralTodoTransactions.TryLoad(tx, command.TodoId, command.ExpectedRevision, out var todo, out var error)
                ? GeneralTodoTransactions.Commit(tx, todo.SetCompleted(command.IsCompleted, tx.UtcNow))
                : TxResult.Abort<long>(error), cancellationToken);
    }
}

public sealed record RenameGeneralTodoCommand(OperationId OperationId, GeneralTodoId TodoId, long ExpectedRevision, string? Text);

/// <summary>Renames a General to-do inline (B-09). Blank text is refused.</summary>
public sealed class RenameGeneralTodo
{
    public const string OperationKind = "general-todo.rename";

    private readonly IMutationCoordinator _coordinator;

    public RenameGeneralTodo(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The to-do's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(RenameGeneralTodoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = ChecklistText.Create(command.Text);
        if (text.Error is { } invalid)
        {
            return UseCaseResults.Failed<long>(new ValidationFailedError(invalid));
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
            GeneralTodoTransactions.TryLoad(tx, command.TodoId, command.ExpectedRevision, out var todo, out var error)
                ? GeneralTodoTransactions.Commit(tx, todo.Rename(text.Value, tx.UtcNow))
                : TxResult.Abort<long>(error), cancellationToken);
    }
}

/// <param name="AnchorId">The to-do the moved one is placed next to (its visible neighbour for Move up/down).</param>
public sealed record MoveGeneralTodoCommand(OperationId OperationId, GeneralTodoId TodoId, long ExpectedRevision, GeneralTodoId AnchorId, bool PlaceAfter);

/// <summary>Reorders a General to-do in the explicit list order (B-09).</summary>
public sealed class MoveGeneralTodo
{
    public const string OperationKind = "general-todo.move";

    private readonly IMutationCoordinator _coordinator;

    public MoveGeneralTodo(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The moved to-do's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(MoveGeneralTodoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, MoveGeneralTodoCommand command)
    {
        if (!GeneralTodoTransactions.TryLoad(tx, command.TodoId, command.ExpectedRevision, out var moving, out var error))
        {
            return TxResult.Abort<long>(error);
        }

        var ordered = ExplicitOrder.Sort(tx.GeneralTodos.ListOrder().Select(t => (t.Id.Value, t.Rank)));
        if (ExplicitOrder.Place(ordered, command.TodoId.Value, command.AnchorId.Value, command.PlaceAfter) is not { } changes)
        {
            return TxResult.Abort<long>(TransactionGuards.NotFound(tx, EntityKind.GeneralTodo, command.AnchorId.Value));
        }

        var revision = moving.Revision;
        foreach (var (id, rank) in changes)
        {
            // A renumber touches neighbours too; each is loaded in this transaction before it is written.
            var todo = id == moving.Id.Value ? moving : tx.GeneralTodos.Find(new GeneralTodoId(id))!;
            var updated = todo.MoveTo(rank, tx.UtcNow);
            tx.GeneralTodos.Update(updated);
            tx.Changes.UpsertGeneralTodo(updated);
            revision = id == moving.Id.Value ? updated.Revision : revision;
        }

        return TxResult.Commit(revision);
    }
}

public sealed record DeleteGeneralTodoCommand(OperationId OperationId, GeneralTodoId TodoId, long ExpectedRevision);

/// <summary>Deletes a General to-do after the flyout confirmation (B-05, design §4.5). Its ID is tombstoned.</summary>
public sealed class DeleteGeneralTodo
{
    public const string OperationKind = "general-todo.delete";

    private readonly IMutationCoordinator _coordinator;

    public DeleteGeneralTodo(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The deleted to-do's last revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(DeleteGeneralTodoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (!GeneralTodoTransactions.TryLoad(tx, command.TodoId, command.ExpectedRevision, out var todo, out var error))
            {
                return TxResult.Abort<long>(error);
            }

            tx.GeneralTodos.Delete(todo.Id);
            TransactionGuards.Tombstone(tx, EntityKind.GeneralTodo, todo.Id.Value);
            tx.Changes.DeleteGeneralTodo(todo.Id);
            return TxResult.Commit(todo.Revision);
        }, cancellationToken);
    }
}

internal static class GeneralTodoTransactions
{
    public static bool TryLoad(
        IWriteTransaction tx,
        GeneralTodoId id,
        long expectedRevision,
        [NotNullWhen(true)] out GeneralTodo? todo,
        [NotNullWhen(false)] out AppError? error)
    {
        todo = tx.GeneralTodos.Find(id);
        if (todo is null)
        {
            error = TransactionGuards.NotFound(tx, EntityKind.GeneralTodo, id.Value);
            return false;
        }

        error = todo.Revision == expectedRevision ? null : new ConcurrencyConflictError(EntityKind.GeneralTodo, id.Value, expectedRevision, todo.Revision);
        return error is null;
    }

    public static TxResult<long> Commit(IWriteTransaction tx, GeneralTodo updated)
    {
        tx.GeneralTodos.Update(updated);
        tx.Changes.UpsertGeneralTodo(updated);
        return TxResult.Commit(updated.Revision);
    }
}
