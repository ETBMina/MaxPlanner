using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.SubtaskCards;

/// <param name="StatusId">Null uses the default task status; Board creation passes the column's status.</param>
public sealed record CreateSubtaskCardCommand(
    OperationId OperationId,
    TaskId ParentTaskId,
    SubtaskCardId NewCardId,
    string? Subtitle,
    string? Notes = null,
    TaskStatusId? StatusId = null,
    DateIntent Start = default,
    DateIntent End = default);

/// <summary>Adds a Subtask card under an active task (B-16.3). The parent's status and dates never change.</summary>
public sealed class CreateSubtaskCard
{
    public const string OperationKind = "subtask-card.create";

    private readonly IMutationCoordinator _coordinator;

    public CreateSubtaskCard(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<SubtaskCardId>>> ExecuteAsync(CreateSubtaskCardCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var subtitle = validation.Check(SubtitleText.Create(command.Subtitle));
        var notes = validation.Check(LongText.CreateOptional(command.Notes, "Notes"));
        if (validation.HasErrors)
        {
            return UseCaseResults.Failed<SubtaskCardId>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command, subtitle.Value, notes.Value), cancellationToken);
    }

    private static TxResult<SubtaskCardId> Execute(IWriteTransaction tx, CreateSubtaskCardCommand command, SubtitleText subtitle, LongText? notes)
    {
        if (!TransactionGuards.TryLoadTask(tx, command.ParentTaskId, expectedRevision: null, out var parent, out var loadError))
        {
            return TxResult.Abort<SubtaskCardId>(loadError);
        }

        // B-05: an archived task (and its cards) is read-only until restored.
        if (parent.IsArchived)
        {
            return TransactionGuards.Reject<SubtaskCardId>(DomainErrorCodes.ArchivedReadOnly, "Parent");
        }

        if (SubtaskCardRules.CheckNewCard(tx, parent.Id, command.NewCardId) is { } error)
        {
            return TxResult.Abort<SubtaskCardId>(error);
        }

        var workflow = tx.Workflow.Read();
        if (workflow.FindTaskStatus(command.StatusId ?? workflow.Defaults.DefaultTaskStatusId) is not { } status)
        {
            return TransactionGuards.Reject<SubtaskCardId>(DomainErrorCodes.UnknownReference, "Status");
        }

        return SubtaskCardRules.Insert(tx, command.NewCardId, parent.Id, subtitle, notes, status, command.Start, command.End);
    }
}

public sealed record PopOutChecklistItemCommand(
    OperationId OperationId,
    TaskId TaskId,
    long ExpectedRevision,
    ChecklistItemId ItemId,
    SubtaskCardId NewCardId);

/// <summary>
/// B-16.3 "Pop out to card": removes a checklist item and creates a Subtask card from it in one
/// transaction. The card's subtitle comes from the item text; a checked item becomes a completed card.
/// </summary>
public sealed class PopOutChecklistItem
{
    public const string OperationKind = "checklist-item.pop-out";

    private readonly IMutationCoordinator _coordinator;

    public PopOutChecklistItem(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<SubtaskCardId>>> ExecuteAsync(PopOutChecklistItemCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<SubtaskCardId> Execute(IWriteTransaction tx, PopOutChecklistItemCommand command)
    {
        if (!TransactionGuards.TryLoadTask(tx, command.TaskId, command.ExpectedRevision, out var task, out var loadError))
        {
            return TxResult.Abort<SubtaskCardId>(loadError);
        }

        if (task.ChecklistItems.All(i => i.Id != command.ItemId))
        {
            return TxResult.Abort<SubtaskCardId>(TransactionGuards.NotFound(tx, EntityKind.ChecklistItem, command.ItemId.Value));
        }

        if (SubtaskCardRules.CheckNewCard(tx, task.Id, command.NewCardId) is { } error)
        {
            return TxResult.Abort<SubtaskCardId>(error);
        }

        var removal = task.RemoveChecklistItem(command.ItemId, tx.UtcNow);
        if (removal.Error is { } readOnly)
        {
            return TransactionGuards.Reject<SubtaskCardId>(readOnly);
        }

        var (updatedTask, item) = removal.Value;

        // B-16.3: checklist text may be longer (or have more lines) than a subtitle allows. The card then
        // gets a shortened subtitle and keeps the full original text in its notes, so nothing is lost.
        var subtitle = SubtitleText.FromChecklistText(item.Text);
        var notes = subtitle.Value == item.Text.Value ? null : LongText.CreateOptional(item.Text.Value, "Notes").Value;

        // B-16.3: a checked item becomes a card in the first completed status (if one is configured).
        var workflow = tx.Workflow.Read();
        var status = (item.IsCompleted ? workflow.FirstCompletedTaskStatus() : null) ?? workflow.FindTaskStatus(workflow.Defaults.DefaultTaskStatusId);
        if (status is null)
        {
            return TransactionGuards.Reject<SubtaskCardId>(DomainErrorCodes.UnknownReference, "Status");
        }

        tx.Tasks.Update(updatedTask);
        tx.Changes.UpsertTask(updatedTask);
        TransactionGuards.Tombstone(tx, EntityKind.ChecklistItem, item.Id.Value);
        return SubtaskCardRules.Insert(tx, command.NewCardId, task.Id, subtitle, notes, status, DateIntent.Unchanged, DateIntent.Unchanged);
    }
}

public sealed record DeleteSubtaskCardCommand(OperationId OperationId, SubtaskCardId CardId, long ExpectedRevision);

/// <summary>
/// Deletes one Subtask card after the user confirmed it (B-16.7). The parent task is unchanged. The cards
/// of an archived parent are read-only until the parent is restored (B-16.6).
/// </summary>
public sealed class DeleteSubtaskCard
{
    public const string OperationKind = "subtask-card.delete";

    private readonly IMutationCoordinator _coordinator;

    public DeleteSubtaskCard(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<SubtaskCardId>>> ExecuteAsync(DeleteSubtaskCardCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            if (!TransactionGuards.TryLoadEditableSubtaskCard(tx, command.CardId, command.ExpectedRevision, out var card, out var error))
            {
                return TxResult.Abort<SubtaskCardId>(error);
            }

            tx.SubtaskCards.Delete(card.Id);
            TransactionGuards.Tombstone(tx, EntityKind.SubtaskCard, card.Id.Value);
            tx.Changes.DeleteSubtaskCard(card.Id);
            return TxResult.Commit(card.Id);
        }, cancellationToken);
    }
}

internal static class SubtaskCardRules
{
    /// <summary>The new card's ID is unused and the parent is below the B-02.10 card limit.</summary>
    public static AppError? CheckNewCard(IWriteTransaction tx, TaskId parentId, SubtaskCardId newCardId)
    {
        if (TransactionGuards.EnsureNewId(tx, EntityKind.SubtaskCard, newCardId.Value, tx.SubtaskCards.Find(newCardId) is not null) is { } idError)
        {
            return idError;
        }

        return tx.SubtaskCards.ListByParent(parentId).Count >= DomainLimits.SubtaskCardsPerTask
            ? new ValidationFailedError(new DomainError(DomainErrorCodes.TooManyItems, "SubtaskCards", DomainLimits.SubtaskCardsPerTask))
            : null;
    }

    /// <summary>Creates the card at the end of its Board column with B-04 creation dates for the card only.</summary>
    public static TxResult<SubtaskCardId> Insert(
        IWriteTransaction tx,
        SubtaskCardId id,
        TaskId parentId,
        SubtitleText subtitle,
        LongText? notes,
        TaskStatusDefinition status,
        DateIntent start,
        DateIntent end)
    {
        var created = SubtaskCard.Create(id, parentId, subtitle, notes, status, start, end,
            RankPolicy.After(tx.Tasks.MaxBoardRank(status.Id)), tx.LocalToday, tx.UtcNow);
        if (created.Error is { } invalid)
        {
            return TransactionGuards.Reject<SubtaskCardId>(invalid);
        }

        tx.SubtaskCards.Insert(created.Value);
        tx.Changes.UpsertSubtaskCard(created.Value);
        return TxResult.Commit(created.Value.Id);
    }
}
