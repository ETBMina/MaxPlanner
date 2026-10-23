using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Settings;

/// <summary>
/// Where a lookup value is used, as the removal confirmation shows it (B-04 "show counts before applying",
/// design §5.9). Archived tasks and Subtask cards of archived parents are included.
/// </summary>
/// <param name="Tasks">Tasks that reference the value (status, priority, people, tags, or a custom value).</param>
/// <param name="SubtaskCards">Subtask cards in the status (task statuses only).</param>
/// <param name="ChangeRequests">CRs that reference the value (status, priority, owner, squad, or a custom value).</param>
/// <param name="CrOwners">CRs the person owns; a CR always needs an Owner, so these can only be replaced (ADR 0020).</param>
/// <param name="ReviewItems">Review items with the person as Developer (B-17.4).</param>
/// <param name="Defaults">Workspace defaults that name the value; each needs a replacement.</param>
public sealed record LookupUsageCounts(int Tasks, int SubtaskCards, int ChangeRequests, int CrOwners, int ReviewItems, WorkspaceDefaultRoles Defaults)
{
    public static LookupUsageCounts None { get; } = new(0, 0, 0, 0, 0, WorkspaceDefaultRoles.None);

    public int RecordCount => Tasks + SubtaskCards + ChangeRequests + ReviewItems;

    public bool IsUnused => RecordCount == 0 && Defaults == WorkspaceDefaultRoles.None;

    /// <summary>The policy view of these counts (<see cref="LookupRemovalPolicy"/>).</summary>
    public LookupUsage ToPolicyUsage() => new(RecordCount, CrOwners, Defaults != WorkspaceDefaultRoles.None);
}

/// <summary>Counts the references to one lookup value in a set of records (one implementation for preview and commit).</summary>
public static class LookupUsageCounter
{
    public static LookupUsageCounts Count(
        LookupKind kind,
        Guid id,
        IEnumerable<TaskItem> tasks,
        IEnumerable<SubtaskCard> cards,
        IEnumerable<ChangeRequest> changeRequests,
        IEnumerable<ReviewItem> reviews,
        WorkflowDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(changeRequests);
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(defaults);

        // A probe remap: an entity "references" the value exactly when remapping would change it.
        var probe = new LookupRemap(kind, id, Guid.Empty);
        var probeTime = DateTimeOffset.UnixEpoch;
        var taskCount = tasks.Count(t => t.Remap(probe, null, probeTime) is not null);
        var cardCount = cards.Count(c => c.Remap(probe, null, probeTime) is not null);
        var crList = changeRequests.Where(c => c.Remap(probe, probeTime) is not null).ToList();
        var owners = kind == LookupKind.Person ? crList.Count(c => c.OwnerId.Value == id) : 0;
        var reviewCount = reviews.Count(r => r.Remap(probe, probeTime) is not null);
        return new LookupUsageCounts(taskCount, cardCount, crList.Count, owners, reviewCount, WorkflowLookups.DefaultRoles(defaults, kind, id));
    }
}

/// <summary>Reads the current usage of a lookup value for the removal confirmation (design §5.9).</summary>
public sealed class PreviewLookupRemoval
{
    private readonly IReadSnapshotSource _snapshots;

    public PreviewLookupRemoval(IReadSnapshotSource snapshots) => _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));

    /// <returns>The counts as of the latest committed data, or a storage failure.</returns>
    public async Task<AppResult<LookupUsageCounts>> ExecuteAsync(LookupKind kind, Guid id, CancellationToken cancellationToken)
    {
        var loaded = await _snapshots.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return AppResult.Failure<LookupUsageCounts>(loaded.Error!);
        }

        var s = loaded.Value;
        return AppResult.Success(LookupUsageCounter.Count(kind, id, s.Tasks, s.SubtaskCards, s.ChangeRequests, s.ReviewItems, s.Workflow.Defaults));
    }
}

/// <param name="ReplacementId">The value that takes over every reference, or null to clear references where allowed.</param>
/// <param name="ConfirmedUsage">The counts the user confirmed; different counts at commit refuse the removal (design §5.9).</param>
public sealed record RemoveLookupCommand(OperationId OperationId, LookupKind Kind, Guid Id, Guid? ReplacementId, LookupUsageCounts ConfirmedUsage);

/// <summary>
/// Removes a status, priority, squad, person, or tag (B-04, B-11, AC-08, AC-31). Every reference in tasks
/// (archived included), Subtask cards, CRs, review items, custom values, and workspace defaults is replaced or,
/// where allowed, cleared, and the value is deleted, all in one transaction (T-06.3).
/// </summary>
/// <remarks>
/// A replaced status is not a transition: dates are kept (B-04). Active cards from the removed Board column
/// are appended to the end of the replacement column in their previous order, so nothing lands silently at
/// the top. CR Owner usage and the default CR Owner can only be replaced; Designers, Developers, review
/// Developers, tags, and squads may be cleared (design §11 Q4). The confirmed counts are compared with the
/// counts inside the transaction, so a confirmation shown for other data never applies.
/// </remarks>
public sealed class RemoveLookup
{
    public const string OperationKind = "workflow.lookup-remove";

    private readonly IMutationCoordinator _coordinator;

    public RemoveLookup(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The number of records that were remapped.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(RemoveLookupCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.ConfirmedUsage);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, RemoveLookupCommand command)
    {
        var workflow = tx.Workflow.Read();
        var ids = WorkflowLookups.Entries(workflow, command.Kind).Select(e => e.Id).ToList();
        var tasks = tx.Tasks.ListAll();
        var cards = tx.SubtaskCards.ListAll();
        var changeRequests = tx.ChangeRequests.ListAll();
        var reviews = tx.Reviews.ListAll();
        var usage = LookupUsageCounter.Count(command.Kind, command.Id, tasks, cards, changeRequests, reviews, workflow.Defaults);
        if (ids.Contains(command.Id) && usage != command.ConfirmedUsage)
        {
            return TransactionGuards.Reject<long>(DomainErrorCodes.LookupUsageChanged, "Removed");
        }

        if (LookupRemovalPolicy.Validate(command.Kind, ids, command.Id, command.ReplacementId, usage.ToPolicyUsage()) is { } invalid)
        {
            return TransactionGuards.Reject<long>(invalid);
        }

        var remap = new LookupRemap(command.Kind, command.Id, command.ReplacementId);
        var defaults = WorkflowLookups.RemapDefaults(workflow.Defaults, remap);
        if (command.Kind == LookupKind.CrStatus
            && WorkflowConfigurationPolicy.ValidateCrBacklogStatus(workflow.FindCrStatus(defaults.CrBacklogStatusId)) is { } backlog)
        {
            return TransactionGuards.Reject<long>(backlog with { Field = "Replacement" });
        }

        var ranks = command.Kind == LookupKind.TaskStatus ? AppendRanks(tx, new TaskStatusId(command.Id), new TaskStatusId(remap.RequiredReplacement)) : [];
        var count = 0L;
        foreach (var task in tasks)
        {
            if (task.Remap(remap, RankOf(ranks, BoardCardKind.Task, task.Id.Value), tx.UtcNow) is { } remapped)
            {
                tx.Tasks.Update(remapped);
                tx.Changes.UpsertTask(remapped);
                count++;
            }
        }

        foreach (var card in cards)
        {
            if (card.Remap(remap, RankOf(ranks, BoardCardKind.SubtaskCard, card.Id.Value), tx.UtcNow) is { } remapped)
            {
                tx.SubtaskCards.Update(remapped);
                tx.Changes.UpsertSubtaskCard(remapped);
                count++;
            }
        }

        foreach (var changeRequest in changeRequests)
        {
            if (changeRequest.Remap(remap, tx.UtcNow) is { } remapped)
            {
                tx.ChangeRequests.Update(remapped);
                tx.Changes.UpsertChangeRequest(remapped);
                count++;
            }
        }

        foreach (var review in reviews)
        {
            if (review.Remap(remap, tx.UtcNow) is { } remapped)
            {
                tx.Reviews.Update(remapped);
                tx.Changes.UpsertReviewItem(remapped);
                count++;
            }
        }

        tx.Lookups.SaveWorkflow(WorkflowLookups.Remove(workflow, command.Kind, command.Id) with { Defaults = defaults });
        tx.Changes.ReplaceWorkflow(tx.Workflow.Read());
        return TxResult.Commit(count);
    }

    /// <summary>
    /// The active cards of the removed column (tasks and Subtask cards share columns, B-16.5), each with a rank
    /// after the end of the replacement column, in their previous order. Archived tasks and the cards of
    /// archived parents keep their ranks: restoring a task places it at the end of its column anyway (B-05).
    /// </summary>
    private static Dictionary<BoardCardKey, long> AppendRanks(IWriteTransaction tx, TaskStatusId removed, TaskStatusId replacement)
    {
        var ranks = new Dictionary<BoardCardKey, long>();
        var last = tx.Tasks.MaxBoardRank(replacement);
        foreach (var (key, _) in tx.Tasks.ListBoardColumn(removed))
        {
            var rank = RankPolicy.After(last);
            ranks[key] = rank;
            last = rank;
        }

        return ranks;
    }

    private static long? RankOf(Dictionary<BoardCardKey, long> ranks, BoardCardKind kind, Guid id) =>
        ranks.TryGetValue(new BoardCardKey(kind, id), out var rank) ? rank : null;
}
