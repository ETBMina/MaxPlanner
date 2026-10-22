using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Domain.ChangeRequests;

/// <summary>
/// The only persisted authority for "task belongs to CR" (T-05). Insert-only: it is never updated,
/// and is removed only when its task is deleted or its CR is cascade-deleted (ADR 0006, ADR 0007).
/// </summary>
public sealed record CrTaskLink(TaskId TaskId, ChangeRequestId ChangeRequestId, DateTimeOffset LinkedAtUtc);
