using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;

namespace MaxPlanner.Application.Changes;

/// <summary>
/// Everything one committed operation changed, published after commit in sequence order (T-07, ADR 0010).
/// Immutable; consumers recompose read models from these records and the current relationships.
/// </summary>
/// <remarks>
/// <see cref="Sequence"/> is written in the same transaction as the changes and is gap-free: a
/// subscriber that sees a gap must resynchronize from a snapshot instead of guessing what it missed.
/// Checklist items travel inside their task, which is upserted whole.
/// </remarks>
public sealed record CommittedChangeSet
{
    public required long Sequence { get; init; }

    public required OperationId OperationId { get; init; }

    public required string OperationKind { get; init; }

    public required DateTimeOffset CommittedAtUtc { get; init; }

    public IReadOnlyList<TaskItem> UpsertedTasks { get; init; } = [];

    public IReadOnlyList<TaskId> DeletedTasks { get; init; } = [];

    public IReadOnlyList<SubtaskCard> UpsertedSubtaskCards { get; init; } = [];

    public IReadOnlyList<SubtaskCardId> DeletedSubtaskCards { get; init; } = [];

    public IReadOnlyList<ChangeRequest> UpsertedChangeRequests { get; init; } = [];

    public IReadOnlyList<ChangeRequestId> DeletedChangeRequests { get; init; } = [];

    public IReadOnlyList<CrTaskLink> AddedLinks { get; init; } = [];

    /// <summary>Links removed by task deletion or CR cascade deletion; never by unlinking (ADR 0006).</summary>
    public IReadOnlyList<CrTaskLink> RemovedLinks { get; init; } = [];

    public IReadOnlyList<ReviewItem> UpsertedReviewItems { get; init; } = [];

    public IReadOnlyList<ReviewItemId> DeletedReviewItems { get; init; } = [];

    public IReadOnlyList<GeneralTodo> UpsertedGeneralTodos { get; init; } = [];

    public IReadOnlyList<GeneralTodoId> DeletedGeneralTodos { get; init; } = [];

    /// <summary>The complete new workflow when lookups, field definitions, or defaults changed; otherwise null.</summary>
    public WorkflowSnapshot? Workflow { get; init; }

    /// <summary>Bulk operations (import, restore) ask subscribers to reload a snapshot once instead of applying records.</summary>
    public bool RequiresFullReload { get; init; }
}
