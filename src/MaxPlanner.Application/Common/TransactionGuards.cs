using System.Diagnostics.CodeAnalysis;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;

namespace MaxPlanner.Application.Common;

/// <summary>The load-and-check steps every transaction body starts with.</summary>
internal static class TransactionGuards
{
    /// <summary>
    /// Loads a task and, when <paramref name="expectedRevision"/> is given, checks it against the revision the
    /// editor saw. The error says whether the task was deleted, never existed, or changed.
    /// </summary>
    public static bool TryLoadTask(
        IWriteTransaction tx,
        TaskId id,
        long? expectedRevision,
        [NotNullWhen(true)] out TaskItem? task,
        [NotNullWhen(false)] out AppError? error)
    {
        task = tx.Tasks.Find(id);
        if (task is null)
        {
            error = NotFound(tx, EntityKind.Task, id.Value);
            return false;
        }

        error = RevisionConflict(EntityKind.Task, id.Value, expectedRevision, task.Revision);
        return error is null;
    }

    /// <summary>
    /// Loads a Subtask card with its parent task and checks the card's revision. A card whose parent is
    /// archived is read-only (B-16.6), which is reported as a rule violation.
    /// </summary>
    public static bool TryLoadEditableSubtaskCard(
        IWriteTransaction tx,
        SubtaskCardId id,
        long? expectedRevision,
        [NotNullWhen(true)] out SubtaskCard? card,
        [NotNullWhen(false)] out AppError? error)
    {
        card = tx.SubtaskCards.Find(id);
        if (card is null)
        {
            error = NotFound(tx, EntityKind.SubtaskCard, id.Value);
            return false;
        }

        error = RevisionConflict(EntityKind.SubtaskCard, id.Value, expectedRevision, card.Revision);
        if (error is null && tx.Tasks.Find(card.ParentTaskId) is { IsArchived: true })
        {
            error = new ValidationFailedError(new DomainError(DomainErrorCodes.ArchivedReadOnly, "Parent"));
        }

        return error is null;
    }

    public static bool TryLoadChangeRequest(
        IWriteTransaction tx,
        ChangeRequestId id,
        long? expectedRevision,
        [NotNullWhen(true)] out ChangeRequest? changeRequest,
        [NotNullWhen(false)] out AppError? error)
    {
        changeRequest = tx.ChangeRequests.Find(id);
        if (changeRequest is null)
        {
            error = NotFound(tx, EntityKind.ChangeRequest, id.Value);
            return false;
        }

        error = RevisionConflict(EntityKind.ChangeRequest, id.Value, expectedRevision, changeRequest.Revision);
        return error is null;
    }

    /// <summary>
    /// Refuses a preallocated ID that already exists or was deleted, so a stale editor or replayed import
    /// cannot re-create a deleted record under its old identity (B-05). Retries of the same operation
    /// never reach this check: the operation ledger answers them first.
    /// </summary>
    public static AppError? EnsureNewId(IWriteTransaction tx, EntityKind kind, Guid id, bool exists) =>
        tx.Tombstones.Contains(kind, id) ? new RecordNotFoundError(kind, id, WasDeleted: true)
        : exists ? new ValidationFailedError(new DomainError(DomainErrorCodes.DuplicateKey, "Id"))
        : null;

    public static RecordNotFoundError NotFound(IWriteTransaction tx, EntityKind kind, Guid id) =>
        new(kind, id, tx.Tombstones.Contains(kind, id));

    /// <summary>Deletes a record's identity for good: later commands and imports see it as deleted.</summary>
    public static void Tombstone(IWriteTransaction tx, EntityKind kind, Guid id) => tx.Tombstones.Add(kind, id, tx.UtcNow);

    /// <summary>Every referenced person and tag must exist in the current workflow.</summary>
    public static void CheckPeopleAndTags(WorkflowSnapshot workflow, TaskContent content, ValidationCollector validation)
    {
        CheckPerson(workflow, content.DesignerId, "Designer", validation);
        CheckPerson(workflow, content.DeveloperId, "Developer", validation);
        if (content.TagIds.Any(id => workflow.FindTag(id) is null))
        {
            validation.Add(new DomainError(DomainErrorCodes.UnknownReference, "Tags"));
        }
    }

    /// <summary>Custom values must satisfy their definitions, and People/Tags values must name existing lookups.</summary>
    public static void CheckCustomValues(WorkflowSnapshot workflow, FieldTarget target, IReadOnlyDictionary<FieldId, CustomValue> values, ValidationCollector validation)
    {
        var definitions = workflow.FieldsFor(target);
        validation.AddRange(CustomFieldPolicy.Errors(target, definitions, values));
        foreach (var definition in definitions.Where(d => d.IsActive))
        {
            var unknown = values.GetValueOrDefault(definition.Id) switch
            {
                PeopleCustomValue people => people.PersonIds.Any(id => workflow.FindPerson(id) is null),
                TagsCustomValue tags => tags.TagIds.Any(id => workflow.FindTag(id) is null),
                _ => false,
            };
            if (unknown)
            {
                validation.Add(new DomainError(DomainErrorCodes.UnknownReference, definition.ErrorField));
            }
        }
    }

    /// <summary>Rolls back with a single rule violation found inside the transaction.</summary>
    public static TxResult<T> Reject<T>(DomainError error) => TxResult.Abort<T>(new ValidationFailedError(error));

    public static TxResult<T> Reject<T>(string code, string? field = null, long? limit = null) => Reject<T>(new DomainError(code, field, limit));

    private static void CheckPerson(WorkflowSnapshot workflow, PersonId? id, string field, ValidationCollector validation)
    {
        if (id is { } person && workflow.FindPerson(person) is null)
        {
            validation.Add(new DomainError(DomainErrorCodes.UnknownReference, field));
        }
    }

    private static ConcurrencyConflictError? RevisionConflict(EntityKind kind, Guid id, long? expected, long actual) =>
        expected is { } e && e != actual ? new ConcurrencyConflictError(kind, id, e, actual) : null;
}
