using MaxPlanner.Application.Common;

namespace MaxPlanner.Application.Abstractions;

/// <summary>
/// Runs every business mutation as one ordered, durable transaction with an idempotent operation
/// ledger and publishes the committed change set (T-06, T-07, ADR 0009, ADR 0010). One instance per
/// open dataset.
/// </summary>
public interface IMutationCoordinator
{
    /// <summary>
    /// Executes <paramref name="work"/> inside one write transaction and, if it commits, records the
    /// operation in the ledger, assigns the next commit sequence, and publishes the change set.
    /// </summary>
    /// <typeparam name="T">
    /// The use case's result. It is recorded in the operation ledger with the commit, so it must be a small
    /// immutable value (an ID, a revision, a count summary), never a live entity graph.
    /// </typeparam>
    /// <param name="operationId">
    /// The stable ID of this user intent. If the ledger already records it as committed, the recorded
    /// outcome is returned with <see cref="CommandOutcome{T}.WasAlreadyApplied"/> set, and
    /// <paramref name="work"/> is neither executed nor published again (T-05 retry after an uncertain outcome).
    /// </param>
    /// <param name="operationKind">A stable name such as <c>task.create</c>, stored in the ledger and the change set.</param>
    /// <param name="work">
    /// The transaction body. It is synchronous by type: it runs from <c>BEGIN IMMEDIATE</c> to commit or
    /// rollback on the writer worker and must not await, block on, or dispatch other work (T-06.2). It
    /// checks revisions and references, writes through the stores, and records each write in
    /// <see cref="IWriteTransaction.Changes"/>. Returning <see cref="TxResult.Abort{T}"/> or throwing
    /// rolls back everything it wrote; nothing is recorded or published and no sequence is consumed.
    /// </param>
    /// <param name="cancellationToken">
    /// Honored only until the transaction begins; cancellation then returns
    /// <see cref="OperationCancelledError"/> with nothing changed. Once the transaction has begun the
    /// operation runs to its outcome, and a committed operation reports success (T-06.6).
    /// </param>
    /// <returns>
    /// The committed value and sequence, or a typed failure: the aborting error from <paramref name="work"/>,
    /// <see cref="StorageFailureError"/> when the transaction failed and rolled back, or
    /// <see cref="CommitOutcomeUnknownError"/> when the storage cannot say whether the commit happened.
    /// </returns>
    /// <remarks>
    /// Publication happens after commit, in sequence order. A failure while publishing does not undo or
    /// retry the durable commit: the call still returns the committed outcome, and subscribers detect
    /// the missing sequence and resynchronize from a snapshot (T-07). Exceptions thrown by
    /// <paramref name="work"/> that are not storage failures are programming errors and propagate after
    /// rollback.
    /// </remarks>
    Task<AppResult<CommandOutcome<T>>> ExecuteAsync<T>(
        OperationId operationId,
        string operationKind,
        Func<IWriteTransaction, TxResult<T>> work,
        CancellationToken cancellationToken);
}

/// <param name="Value">The use case result recorded with the commit.</param>
/// <param name="CommitSequence">The gap-free sequence of the commit that applied the operation.</param>
/// <param name="WasAlreadyApplied">True when this call replayed an earlier committed outcome from the ledger.</param>
public sealed record CommandOutcome<T>(T Value, long CommitSequence, bool WasAlreadyApplied);

/// <summary>How a transaction body ends: commit with a value, or roll back with a typed error.</summary>
public readonly struct TxResult<T>
{
    private readonly T? _value;

    internal TxResult(T value)
    {
        _value = value;
        Error = null;
    }

    internal TxResult(AppError error)
    {
        _value = default;
        Error = error;
    }

    /// <summary>Null when the transaction should commit.</summary>
    public AppError? Error { get; }

    public bool IsCommit => Error is null;

    /// <exception cref="InvalidOperationException">The result is an abort.</exception>
    public T Value => IsCommit ? _value! : throw new InvalidOperationException($"Transaction aborted with {Error!.Code}.");
}

public static class TxResult
{
    public static TxResult<T> Commit<T>(T value) => new(value);

    public static TxResult<T> Abort<T>(AppError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(error);
    }
}
