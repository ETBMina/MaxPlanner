using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;

namespace MaxPlanner.Application.Abstractions;

/// <summary>
/// Every business record of the dataset plus the commit sequence they reflect, read in one read
/// transaction so the sequence matches the data exactly (T-06.2, ADR 0010 handoff).
/// </summary>
/// <param name="CommitSequence">The last commit included; change sets with a sequence at or below it are already reflected.</param>
public sealed record ReadSnapshot(
    long CommitSequence,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<SubtaskCard> SubtaskCards,
    IReadOnlyList<ChangeRequest> ChangeRequests,
    IReadOnlyList<CrTaskLink> Links,
    IReadOnlyList<ReviewItem> ReviewItems,
    IReadOnlyList<GeneralTodo> GeneralTodos,
    WorkflowSnapshot Workflow);

/// <summary>Loads read snapshots on pooled read-only connections, concurrently with the writer.</summary>
public interface IReadSnapshotSource
{
    /// <returns>The snapshot, or a <see cref="StorageFailureError"/> / <see cref="OperationCancelledError"/>.</returns>
    Task<AppResult<ReadSnapshot>> LoadAsync(CancellationToken cancellationToken);
}
