using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;

namespace MaxPlanner.Application.Abstractions;

/// <summary>
/// The view of the dataset inside one write transaction. Valid only during the
/// <see cref="IMutationCoordinator.ExecuteAsync{T}"/> delegate and only on its thread.
/// </summary>
/// <remarks>
/// Stores are feature-specific, synchronous, and return materialized records (T-06.4). Writes are
/// visible to later reads in the same transaction and are discarded on rollback. Stores enforce
/// storage constraints (primary/unique keys, foreign keys) and throw when a use case violates them;
/// use cases check business rules first so such a throw indicates a defect.
/// <para>An <c>Update</c> must be given a record that was loaded (<c>Find</c>) or written earlier in the
/// same transaction, with a higher revision than that state. Stores guard the write with the revision
/// they last saw in this transaction and throw otherwise, so a stale entity (for example one taken
/// from a read model) can never overwrite newer data.</para>
/// </remarks>
public interface IWriteTransaction
{
    /// <summary>UTC now, captured once when the transaction began (audit timestamps).</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>The user's local calendar day, captured once with <see cref="UtcNow"/> (B-04 date automation).</summary>
    DateOnly LocalToday { get; }

    ChangeSetBuilder Changes { get; }

    ITaskStore Tasks { get; }

    ISubtaskCardStore SubtaskCards { get; }

    IChangeRequestStore ChangeRequests { get; }

    ILinkStore Links { get; }

    IReviewStore Reviews { get; }

    IGeneralTodoStore GeneralTodos { get; }

    IWorkflowReader Workflow { get; }

    ITombstoneStore Tombstones { get; }
}

public enum BoardCardKind
{
    Task,
    SubtaskCard,
}

/// <summary>A card in a Board column: a task or a Subtask card, which share columns and ranks (B-16.5).</summary>
public readonly record struct BoardCardKey(BoardCardKind Kind, Guid Id);

public interface ITaskStore
{
    /// <summary>The task with its checklist items and its CR link (from the link table), or null.</summary>
    TaskItem? Find(TaskId id);

    /// <summary>Inserts the task and its checklist items. The CR link is inserted separately through <see cref="ILinkStore"/>.</summary>
    void Insert(TaskItem task);

    /// <summary>Replaces the task row and its checklist items. The link is never changed here (ADR 0006).</summary>
    void Update(TaskItem task);

    /// <summary>Deletes the task and its checklist items. Its link and Subtask cards must already be deleted.</summary>
    void Delete(TaskId id);

    /// <summary>
    /// The highest rank in a Board column, or null when it is empty. Covers non-archived tasks and the
    /// Subtask cards of non-archived parents, because both kinds share the column (B-07, B-16.5).
    /// </summary>
    long? MaxBoardRank(TaskStatusId status);

    /// <summary>
    /// Every card of a Board column in order (rank, then ID), with the same membership as
    /// <see cref="MaxBoardRank"/>. Used to renumber the full column, filtered-out cards included (B-07).
    /// </summary>
    IReadOnlyList<(BoardCardKey Key, long Rank)> ListBoardColumn(TaskStatusId status);
}

public interface ISubtaskCardStore
{
    SubtaskCard? Find(SubtaskCardId id);

    /// <summary>Requires the parent task to exist.</summary>
    void Insert(SubtaskCard card);

    void Update(SubtaskCard card);

    void Delete(SubtaskCardId id);

    IReadOnlyList<SubtaskCard> ListByParent(TaskId parentTaskId);
}

public interface IChangeRequestStore
{
    ChangeRequest? Find(ChangeRequestId id);

    /// <summary>Lookup by <see cref="Domain.Values.CrNumber.Normalized"/>, the unique key (B-03.2).</summary>
    ChangeRequest? FindByNormalizedNumber(string normalizedNumber);

    void Insert(ChangeRequest changeRequest);

    void Update(ChangeRequest changeRequest);

    /// <summary>Deletes the CR. Its links (and, by cascade, their tasks) must already be deleted.</summary>
    void Delete(ChangeRequestId id);
}

/// <summary>The <c>cr_task_links</c> table: insert-only, one row per linked task (T-05, ADR 0006).</summary>
public interface ILinkStore
{
    CrTaskLink? FindByTask(TaskId taskId);

    /// <summary>Requires both endpoints to exist and the task to have no link (primary key on the task).</summary>
    void Insert(CrTaskLink link);

    /// <summary>The linked tasks of a CR, archived tasks included.</summary>
    IReadOnlyList<TaskId> ListTaskIds(ChangeRequestId changeRequestId);

    /// <summary>Removes the task's link. Only task deletion and CR cascade deletion call this.</summary>
    void DeleteForTask(TaskId taskId);
}

public interface IReviewStore
{
    ReviewItem? Find(ReviewItemId id);

    void Insert(ReviewItem item);

    void Update(ReviewItem item);

    void Delete(ReviewItemId id);

    long? MaxRank();
}

public interface IGeneralTodoStore
{
    GeneralTodo? Find(GeneralTodoId id);

    void Insert(GeneralTodo todo);

    void Update(GeneralTodo todo);

    void Delete(GeneralTodoId id);

    long? MaxRank();
}

public interface IWorkflowReader
{
    /// <summary>The lookups, field definitions, and defaults as of this transaction.</summary>
    WorkflowSnapshot Read();
}

/// <summary>
/// IDs of deleted records (T-05 <c>tombstones</c>), so stale editors and imports cannot silently
/// re-create them (B-05) and "not found" can say the record was deleted.
/// </summary>
public interface ITombstoneStore
{
    void Add(EntityKind kind, Guid id, DateTimeOffset deletedAtUtc);

    bool Contains(EntityKind kind, Guid id);
}
