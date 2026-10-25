using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.SubtaskCards;

/// <param name="Subtitle">Required (B-16.2).</param>
/// <param name="Notes">Blank clears the notes.</param>
/// <param name="Start">What the editor did to StartDate; <see cref="DateIntent.Unchanged"/> lets B-04 automation fill it.</param>
public sealed record UpdateSubtaskCardCommand(
    OperationId OperationId,
    SubtaskCardId CardId,
    long ExpectedRevision,
    string? Subtitle,
    string? Notes,
    TaskStatusId StatusId,
    DateIntent Start = default,
    DateIntent End = default);

/// <summary>
/// Saves the Subtask card editor draft (design §5.2) as one change: subtitle, notes, status, and dates.
/// B-04 date automation applies to the card's own dates only; the parent task is never read for writing,
/// so its status and dates cannot change (B-16.4, AC-32). A card of an archived parent is read-only (B-16.6).
/// </summary>
public sealed class UpdateSubtaskCard
{
    public const string OperationKind = "subtask-card.update";

    private readonly IMutationCoordinator _coordinator;

    public UpdateSubtaskCard(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The card's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(UpdateSubtaskCardCommand command, CancellationToken cancellationToken)
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

    private static TxResult<long> Execute(IWriteTransaction tx, UpdateSubtaskCardCommand command, SubtitleText subtitle, LongText? notes)
    {
        if (!TransactionGuards.TryLoadEditableSubtaskCard(tx, command.CardId, command.ExpectedRevision, out var card, out var error))
        {
            return TxResult.Abort<long>(error);
        }

        var workflow = tx.Workflow.Read();
        var current = workflow.FindTaskStatus(card.StatusId);
        var target = workflow.FindTaskStatus(command.StatusId);
        if (current is null || target is null)
        {
            return TransactionGuards.Reject<long>(DomainErrorCodes.UnknownReference, "Status");
        }

        // B-07: a status change from the editor places the card at the top of the full destination column,
        // the same rule as the task editor; an unchanged status keeps its rank.
        var rank = target.Id == current.Id ? card.Rank : BoardColumnRanks.TopRank(tx, target, new BoardCardKey(BoardCardKind.SubtaskCard, card.Id.Value));
        var updated = card.Update(subtitle, notes, current, target, rank, command.Start, command.End, tx.LocalToday, tx.UtcNow);
        if (updated.Error is { } invalid)
        {
            return TransactionGuards.Reject<long>(invalid);
        }

        tx.SubtaskCards.Update(updated.Value);
        tx.Changes.UpsertSubtaskCard(updated.Value);
        return TxResult.Commit(updated.Value.Revision);
    }
}
