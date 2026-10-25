using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Tasks;

/// <summary>Where a moved card lands in its destination column.</summary>
public abstract record BoardPlacement
{
    private protected BoardPlacement()
    {
    }

    /// <summary>The end of the full column. Used for a cross-column drop while the Board is filtered (B-07).</summary>
    public static BoardPlacement AppendToEnd { get; } = new AppendToEndPlacement();

    /// <summary>
    /// The top of the full column. The default for status changes made outside a positional Board drop
    /// (editors, Grid, Board menu and keyboard moves) and for new, copied and restored cards (B-07).
    /// </summary>
    public static BoardPlacement Top { get; } = new TopPlacement();

    /// <param name="beforeRank">Rank of the card that will precede the moved card, or null for the top.</param>
    /// <param name="afterRank">Rank of the card that will follow it, or null for the bottom.</param>
    public static BoardPlacement Between(long? beforeRank, long? afterRank) => new BetweenPlacement(beforeRank, afterRank);
}

public sealed record AppendToEndPlacement : BoardPlacement;

public sealed record TopPlacement : BoardPlacement;

public sealed record BetweenPlacement(long? BeforeRank, long? AfterRank) : BoardPlacement;

/// <param name="Start">Explicit StartDate edit sent with the move (e.g. from a dialog); unchanged by default.</param>
public sealed record MoveTaskCommand(
    OperationId OperationId,
    TaskId TaskId,
    long ExpectedRevision,
    TaskStatusId TargetStatusId,
    BoardPlacement Placement,
    DateIntent Start = default,
    DateIntent End = default);

/// <summary>Moves a task within or across Board columns: status, B-04 dates, and order in one transaction (B-07).</summary>
public sealed class MoveTask
{
    public const string OperationKind = "task.move";

    private readonly IMutationCoordinator _coordinator;

    public MoveTask(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The task's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(MoveTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Placement);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, MoveTaskCommand command)
    {
        if (!TransactionGuards.TryLoadTask(tx, command.TaskId, command.ExpectedRevision, out var task, out var loadError))
        {
            return TxResult.Abort<long>(loadError);
        }

        var workflow = tx.Workflow.Read();
        var current = workflow.FindTaskStatus(task.StatusId);
        var target = workflow.FindTaskStatus(command.TargetStatusId);
        if (current is null || target is null)
        {
            return TransactionGuards.Reject<long>(DomainErrorCodes.UnknownReference, "Status");
        }

        var rank = BoardColumnRanks.RankFor(tx, target, new BoardCardKey(BoardCardKind.Task, task.Id.Value), command.Placement);

        var moved = task.MoveTo(current, target, rank, command.Start, command.End, tx.LocalToday, tx.UtcNow);
        if (moved.Error is { } invalid)
        {
            return TransactionGuards.Reject<long>(invalid);
        }

        tx.Tasks.Update(moved.Value);
        tx.Changes.UpsertTask(moved.Value);
        return TxResult.Commit(moved.Value.Revision);
    }
}

/// <summary>Rank assignment for a card entering a Board column; shared by task and Subtask card moves (B-07, B-16.5).</summary>
internal static class BoardColumnRanks
{
    /// <summary>The rank for <paramref name="moved"/> at <paramref name="placement"/> in <paramref name="column"/>.</summary>
    public static long RankFor(IWriteTransaction tx, TaskStatusDefinition column, BoardCardKey moved, BoardPlacement placement) => placement switch
    {
        BetweenPlacement between => RankPolicy.Between(between.BeforeRank, between.AfterRank)
            ?? RenumberColumn(tx, column, moved, between.BeforeRank),
        TopPlacement => RankPolicy.Before(tx.Tasks.MinBoardRank(column.Id)) ?? RenumberColumn(tx, column, moved, beforeRank: null),
        _ => RankPolicy.After(tx.Tasks.MaxBoardRank(column.Id)),
    };

    /// <summary>
    /// The rank for a card that enters <paramref name="column"/> without a positional drop: new, copied,
    /// restored, or changed status elsewhere. It goes to the top of the full column (B-07).
    /// </summary>
    public static long TopRank(IWriteTransaction tx, TaskStatusDefinition column, BoardCardKey entering) =>
        RankFor(tx, column, entering, BoardPlacement.Top);

    /// <summary>
    /// B-07: when two neighbours have no free rank between them, the full destination column (cards hidden
    /// by filters included) is respaced in this same transaction. Only cards whose rank changes are
    /// written; the moved card's new rank is returned for its own move.
    /// </summary>
    public static long RenumberColumn(IWriteTransaction tx, TaskStatusDefinition column, BoardCardKey moved, long? beforeRank)
    {
        var cards = tx.Tasks.ListBoardColumn(column.Id).Where(c => c.Key != moved).ToList();
        var index = beforeRank is { } before ? cards.FindLastIndex(c => c.Rank <= before) + 1 : 0;
        cards.Insert(index, (moved, long.MinValue));

        var movedRank = 0L;
        foreach (var (key, rank) in RankPolicy.Renumber(cards))
        {
            if (key == moved)
            {
                movedRank = rank;
            }
            else
            {
                Reposition(tx, column, key, rank);
            }
        }

        return movedRank;
    }

    // Respacing is a same-status move, so no date automation applies (B-04: not a transition).
    private static void Reposition(IWriteTransaction tx, TaskStatusDefinition column, BoardCardKey key, long rank)
    {
        if (key.Kind == BoardCardKind.Task)
        {
            var task = tx.Tasks.Find(new TaskId(key.Id)) ?? throw new InvalidOperationException($"Board column lists missing task {key.Id}.");
            var repositioned = task.MoveTo(column, column, rank, DateIntent.Unchanged, DateIntent.Unchanged, tx.LocalToday, tx.UtcNow).Value;
            tx.Tasks.Update(repositioned);
            tx.Changes.UpsertTask(repositioned);
        }
        else
        {
            var card = tx.SubtaskCards.Find(new SubtaskCardId(key.Id)) ?? throw new InvalidOperationException($"Board column lists missing Subtask card {key.Id}.");
            var repositioned = card.MoveTo(column, column, rank, DateIntent.Unchanged, DateIntent.Unchanged, tx.LocalToday, tx.UtcNow).Value;
            tx.SubtaskCards.Update(repositioned);
            tx.Changes.UpsertSubtaskCard(repositioned);
        }
    }
}
