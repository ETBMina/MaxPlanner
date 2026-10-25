using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Tasks;

/// <summary>A task editor's whole draft for an existing task (B-02.8, design §5.1).</summary>
/// <param name="Content">For a linked task (or one being linked) the title is ignored by the UI and must be its own title while unlinked.</param>
/// <param name="StatusId">A change is a status transition: B-04 dates apply and the task moves to the end of the new Board column (B-07).</param>
/// <param name="ChecklistItems">The complete checklist in display order; items left out are deleted.</param>
/// <param name="NewLookups">People and tags the draft created; they are inserted only by this save (B-02.6).</param>
/// <param name="LinkTo">
/// A link confirmed in the editor (B-02.4, design §5.6). It is created permanently in the same transaction,
/// against the CR revision the confirmation previewed (T-13). Only an unlinked task can be linked.
/// </param>
public sealed record UpdateTaskCommand(
    OperationId OperationId,
    TaskId TaskId,
    long ExpectedRevision,
    TaskContentDraft Content,
    TaskStatusId StatusId,
    PriorityId PriorityId,
    DateIntent Start,
    DateIntent End,
    IReadOnlyList<ChecklistItemDraft> ChecklistItems,
    NewLookups? NewLookups = null,
    CrLinkTarget? LinkTo = null);

/// <summary>
/// Saves a task editor draft as one logical operation with one revision: content, priority, status and B-04
/// dates, Board position, checklist, new people and tags, and an optional permanent link.
/// </summary>
public sealed class UpdateTask
{
    public const string OperationKind = "task.update";

    private readonly IMutationCoordinator _coordinator;

    public UpdateTask(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>
    /// The task's new revision, or every field error at once (<see cref="ValidationFailedError"/>), a
    /// <see cref="ConcurrencyConflictError"/> for the task or the previewed CR, or a storage failure.
    /// </returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(UpdateTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var content = TaskContentParser.Parse(command.Content, isLinked: null, validation);
        var checklist = ChecklistDrafts.Parse(command.ChecklistItems, validation);
        LookupAdditions.Validate(command.NewLookups, validation);
        if (validation.HasErrors || content is null || checklist is null)
        {
            return UseCaseResults.Failed<long>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command, content, checklist), cancellationToken);
    }

    private static TxResult<long> Execute(
        IWriteTransaction tx,
        UpdateTaskCommand command,
        TaskContent content,
        IReadOnlyList<(ChecklistItemId Id, ChecklistText Text, bool IsCompleted)> checklist)
    {
        if (!TransactionGuards.TryLoadTask(tx, command.TaskId, command.ExpectedRevision, out var task, out var loadError))
        {
            return TxResult.Abort<long>(loadError);
        }

        ChangeRequest? changeRequest = null;
        if (command.LinkTo is { } target)
        {
            if (!TransactionGuards.TryLoadChangeRequest(tx, target.Id, target.ExpectedRevision, out changeRequest, out var crError))
            {
                return TxResult.Abort<long>(crError);
            }

            // The link table is the authority (T-05, ADR 0006): a task links once.
            if (tx.Links.FindByTask(task.Id) is not null)
            {
                return TransactionGuards.Reject<long>(DomainErrorCodes.TaskAlreadyLinked, "ChangeRequest");
            }
        }

        content = LookupAdditions.Apply(tx, command.NewLookups, content);
        var workflow = tx.Workflow.Read();
        content = content with
        {
            CustomValues = CustomFieldPolicy.MergeForUpdate(FieldTarget.Task, workflow.Fields, task.CustomValues, content.CustomValues),
        };

        var validation = new ValidationCollector();
        var current = workflow.FindTaskStatus(task.StatusId);
        var targetStatus = workflow.FindTaskStatus(command.StatusId);
        if (current is null || targetStatus is null)
        {
            validation.Add(new DomainError(DomainErrorCodes.UnknownReference, "Status"));
        }

        if (workflow.FindPriority(command.PriorityId) is null)
        {
            validation.Add(new DomainError(DomainErrorCodes.UnknownReference, "Priority"));
        }

        TransactionGuards.CheckPeopleAndTags(workflow, content, validation);
        TransactionGuards.CheckCustomValues(workflow, FieldTarget.Task, content.CustomValues, validation);
        if (validation.HasErrors || current is null || targetStatus is null)
        {
            return TxResult.Abort<long>(validation.ToError());
        }

        if (!ChecklistDrafts.TryBuild(tx, task.ChecklistItems, checklist, out var items, out var itemError))
        {
            return TxResult.Abort<long>(itemError!);
        }

        // B-07: a status change from the editor places the task at the top of the full destination column,
        // the same rule as Grid and Board menu moves; an unchanged status keeps its rank.
        var rank = targetStatus.Id == current.Id ? task.Rank : BoardColumnRanks.TopRank(tx, targetStatus, new BoardCardKey(BoardCardKind.Task, task.Id.Value));
        var edited = task.Edit(
            new TaskEdit(content, command.PriorityId, command.Start, command.End, items),
            current,
            targetStatus,
            rank,
            changeRequest?.Id,
            tx.LocalToday,
            tx.UtcNow);
        if (edited.Error is { } invalid)
        {
            return TransactionGuards.Reject<long>(invalid);
        }

        tx.Tasks.Update(edited.Value);
        tx.Changes.UpsertTask(edited.Value);
        if (changeRequest is not null)
        {
            var link = new CrTaskLink(task.Id, changeRequest.Id, tx.UtcNow);
            tx.Links.Insert(link);
            tx.Changes.AddLink(link);
        }

        return TxResult.Commit(edited.Value.Revision);
    }
}
