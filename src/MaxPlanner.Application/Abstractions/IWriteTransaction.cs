using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Workflow;

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

    /// <summary>Adds people and tags created by a saved editor draft (B-02.6).</summary>
    ILookupStore Lookups { get; }

    ITombstoneStore Tombstones { get; }

    /// <summary>External source identities of imported records (B-13.5).</summary>
    IImportLedgerStore ImportLedger { get; }
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

    /// <summary>Every task, archived included, with its checklist items and link (lookup replacement, B-04).</summary>
    IReadOnlyList<TaskItem> ListAll();
}

public interface ISubtaskCardStore
{
    SubtaskCard? Find(SubtaskCardId id);

    /// <summary>Requires the parent task to exist.</summary>
    void Insert(SubtaskCard card);

    void Update(SubtaskCard card);

    void Delete(SubtaskCardId id);

    IReadOnlyList<SubtaskCard> ListByParent(TaskId parentTaskId);

    /// <summary>Every Subtask card, including those of archived parents (lookup replacement, B-04).</summary>
    IReadOnlyList<SubtaskCard> ListAll();
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

    /// <summary>Every CR (lookup replacement, B-04).</summary>
    IReadOnlyList<ChangeRequest> ListAll();
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

    /// <summary>Every review's ID and rank (B-17.2 explicit order), for reordering; callers sort by rank, then ID.</summary>
    IReadOnlyList<(ReviewItemId Id, long Rank)> ListOrder();

    /// <summary>Every review item (lookup replacement, B-17.4).</summary>
    IReadOnlyList<ReviewItem> ListAll();
}

public interface IGeneralTodoStore
{
    GeneralTodo? Find(GeneralTodoId id);

    void Insert(GeneralTodo todo);

    void Update(GeneralTodo todo);

    void Delete(GeneralTodoId id);

    long? MaxRank();

    /// <summary>Every to-do's ID and rank (B-09 explicit order), for reordering; callers sort by rank, then ID.</summary>
    IReadOnlyList<(GeneralTodoId Id, long Rank)> ListOrder();
}

public interface IWorkflowReader
{
    /// <summary>The lookups, field definitions, and defaults as of this transaction.</summary>
    WorkflowSnapshot Read();
}

/// <summary>
/// Writes the configurable workflow: people and tags added while saving a record that uses them (B-02.6), the
/// default CR Owner chosen while saving a CR (B-11), and Settings changes to lookups, field definitions, and
/// defaults (B-04, B-11). Later reads of <see cref="IWorkflowReader.Read"/> in the same transaction include them.
/// </summary>
public interface ILookupStore
{
    /// <summary>Requires an unused ID and an unused normalized name (unique per lookup type, B-04).</summary>
    void InsertPerson(PersonDefinition person);

    /// <summary>Requires an unused ID and an unused normalized name.</summary>
    void InsertTag(TagDefinition tag);

    /// <summary>
    /// Sets the workspace default CR Owner (B-04, B-11 first-CR prompt). Requires an existing person; later
    /// reads of <see cref="IWorkflowReader.Read"/> in the same transaction include it.
    /// </summary>
    void SetDefaultCrOwner(PersonId ownerId);

    /// <summary>
    /// Stores <paramref name="workflow"/> as the complete configuration: lookup values and field definitions
    /// missing from it are deleted, new ones inserted, changed ones updated, and the defaults replaced.
    /// </summary>
    /// <remarks>
    /// Storage constraints still apply: a deleted lookup value must no longer be referenced by any record or
    /// default (the use case remaps references first), names stay unique per lookup type, and field
    /// definitions are never deleted (B-11 deactivates them). A violation throws, rolling back the transaction.
    /// </remarks>
    void SaveWorkflow(WorkflowSnapshot workflow);
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

/// <summary>
/// The <c>import_ledger</c> table (B-13.5): which record an external source record became, keyed by the
/// user-chosen source namespace, the entity kind, and the source record's identity. Rows are insert-only.
/// </summary>
public interface IImportLedgerStore
{
    /// <returns>The record ID imported for this source identity, or null.</returns>
    Guid? Find(string sourceNamespace, string entityKind, string sourceKey);

    /// <summary>Requires an unused (namespace, kind, key); a duplicate throws and rolls the import back.</summary>
    void Insert(string sourceNamespace, string entityKind, string sourceKey, Guid recordId);
}
