using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Application.Common;

public enum EntityKind
{
    Task,
    ChecklistItem,
    SubtaskCard,
    ChangeRequest,
    ReviewItem,
    GeneralTodo,
}

public enum StorageFailureKind
{
    DiskFull,
    FileLocked,
    Busy,
    Corrupt,
    Unavailable,
    Io,
}

/// <summary>
/// A typed failure returned across the Application boundary (T-06.7). The hierarchy is closed;
/// <see cref="Code"/> is stable so Presentation can map it to a localized, actionable message.
/// </summary>
/// <remarks>
/// Domain rule violations detected inside a transaction (for example an already linked task or a
/// changed cascade scope) are reported as <see cref="ValidationFailedError"/> with the domain code, the
/// same type used for draft validation before the transaction; the domain code tells them apart.
/// </remarks>
public abstract record AppError
{
    private protected AppError()
    {
    }

    public abstract string Code { get; }
}

/// <summary>One or more rule violations; nothing was written. Draft validation reports every field error at once.</summary>
public sealed record ValidationFailedError(IReadOnlyList<DomainError> Errors) : AppError
{
    public ValidationFailedError(DomainError error)
        : this([error])
    {
    }

    public override string Code => "validation-failed";
}

/// <summary>The record changed since the editor loaded it (T-05). The draft is kept for review/reload.</summary>
public sealed record ConcurrencyConflictError(EntityKind Entity, Guid Id, long ExpectedRevision, long ActualRevision) : AppError
{
    public override string Code => "concurrency-conflict";
}

/// <param name="WasDeleted">True when a tombstone shows the record was deleted, as opposed to never existing (B-05).</param>
public sealed record RecordNotFoundError(EntityKind Entity, Guid Id, bool WasDeleted) : AppError
{
    public override string Code => "record-not-found";
}

/// <summary>B-03.2: identifies the CR that already uses the number so the editor can offer to open it.</summary>
public sealed record DuplicateCrNumberError(string Number, ChangeRequestId ExistingId) : AppError
{
    public override string Code => "duplicate-cr-number";
}

public sealed record StorageFailureError(StorageFailureKind Kind) : AppError
{
    public override string Code => "storage-failure";
}

/// <summary>
/// The commit may or may not have happened (for example the storage connection failed during COMMIT).
/// Callers must not blindly retry with a new operation ID; retrying with the same ID is safe (T-07).
/// </summary>
public sealed record CommitOutcomeUnknownError(OperationId OperationId) : AppError
{
    public override string Code => "commit-outcome-unknown";
}

/// <summary>Cancelled before the transaction began, so nothing changed (T-06.6).</summary>
public sealed record OperationCancelledError : AppError
{
    public static OperationCancelledError Instance { get; } = new();

    public override string Code => "operation-cancelled";
}

/// <summary>T-03.8: the dataset was written by a newer app version; this version must not open it.</summary>
public sealed record UnsupportedSchemaError(int FoundVersion, int SupportedVersion) : AppError
{
    public override string Code => "unsupported-schema";
}
