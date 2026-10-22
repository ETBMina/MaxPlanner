using MaxPlanner.Application.Common;

namespace MaxPlanner.Application.Abstractions;

/// <summary>
/// Answers whether an operation committed, for recovery after <see cref="CommitOutcomeUnknownError"/> (T-07).
/// </summary>
public interface IOperationStatusQuery
{
    /// <summary>
    /// Reads the operation ledger. The query is ordered after every mutation queued before it, so an
    /// uncertain operation has finished by the time it is answered.
    /// </summary>
    /// <returns>The status, or a <see cref="StorageFailureError"/> / <see cref="OperationCancelledError"/>.</returns>
    Task<AppResult<OperationStatus>> GetOperationStatusAsync(OperationId operationId, CancellationToken cancellationToken);
}

/// <param name="IsCommitted">True when the ledger records the operation as committed.</param>
/// <param name="OperationKind">The recorded kind; null when not committed.</param>
/// <param name="CommitSequence">The commit that applied it; null when not committed.</param>
public sealed record OperationStatus(OperationId OperationId, bool IsCommitted, string? OperationKind, long? CommitSequence);
