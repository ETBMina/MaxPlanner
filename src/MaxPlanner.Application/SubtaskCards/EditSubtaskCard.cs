using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.SubtaskCards;

/// <param name="Subtitle">Required (B-16.2).</param>
/// <param name="Notes">Blank clears the notes.</param>
public sealed record UpdateSubtaskCardContentCommand(OperationId OperationId, SubtaskCardId CardId, long ExpectedRevision, string? Subtitle, string? Notes);

/// <summary>
/// Saves a Subtask card's own text fields (B-16.2). Status and dates have their own command; the parent
/// task is never changed.
/// </summary>
public sealed class UpdateSubtaskCardContent
{
    public const string OperationKind = "subtask-card.update-content";

    private readonly IMutationCoordinator _coordinator;

    public UpdateSubtaskCardContent(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The card's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(UpdateSubtaskCardContentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var subtitle = validation.Check(SubtitleText.Create(command.Subtitle));
        var notes = validation.Check(LongText.CreateOptional(command.Notes, "Notes"));
        if (validation.HasErrors)
        {
            return UseCaseResults.Failed<long>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command, subtitle.Value, notes.Value), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, UpdateSubtaskCardContentCommand command, SubtitleText subtitle, LongText? notes)
    {
        if (!TransactionGuards.TryLoadEditableSubtaskCard(tx, command.CardId, command.ExpectedRevision, out var card, out var error))
        {
            return TxResult.Abort<long>(error);
        }

        var updated = card.Edit(subtitle, notes, tx.UtcNow);
        tx.SubtaskCards.Update(updated);
        tx.Changes.UpsertSubtaskCard(updated);
        return TxResult.Commit(updated.Revision);
    }
}

public sealed record MoveSubtaskCardCommand(
    OperationId OperationId,
    SubtaskCardId CardId,
    long ExpectedRevision,
    TaskStatusId TargetStatusId,
    BoardPlacement Placement,
    DateIntent Start = default,
    DateIntent End = default);

/// <summary>
/// Changes a Subtask card's status and Board position in one transaction, with B-04 date automation on
/// the card's own dates only (B-16.4). The parent task's status and dates never change.
/// </summary>
public sealed class MoveSubtaskCard
{
    public const string OperationKind = "subtask-card.move";

    private readonly IMutationCoordinator _coordinator;

    public MoveSubtaskCard(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The card's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(MoveSubtaskCardCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Placement);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, MoveSubtaskCardCommand command)
    {
        if (!TransactionGuards.TryLoadEditableSubtaskCard(tx, command.CardId, command.ExpectedRevision, out var card, out var error))
        {
            return TxResult.Abort<long>(error);
        }

        var workflow = tx.Workflow.Read();
        var current = workflow.FindTaskStatus(card.StatusId);
        var target = workflow.FindTaskStatus(command.TargetStatusId);
        if (current is null || target is null)
        {
            return TransactionGuards.Reject<long>(DomainErrorCodes.UnknownReference, "Status");
        }

        var rank = BoardColumnRanks.RankFor(tx, target, new BoardCardKey(BoardCardKind.SubtaskCard, card.Id.Value), command.Placement);
        var moved = card.MoveTo(current, target, rank, command.Start, command.End, tx.LocalToday, tx.UtcNow);
        if (moved.Error is { } invalid)
        {
            return TransactionGuards.Reject<long>(invalid);
        }

        tx.SubtaskCards.Update(moved.Value);
        tx.Changes.UpsertSubtaskCard(moved.Value);
        return TxResult.Commit(moved.Value.Revision);
    }
}
