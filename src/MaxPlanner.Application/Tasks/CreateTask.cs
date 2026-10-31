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

/// <summary>The CR a new task is created under, with the revision its inherited values were previewed from (T-13).</summary>
public sealed record CrLinkTarget(ChangeRequestId Id, long ExpectedRevision);

/// <param name="NewTaskId">Preallocated, so a retry with the same operation cannot create a second task (T-05).</param>
/// <param name="StatusId">Null uses the default task status; Board creation passes the column's status (B-02.1).</param>
/// <param name="PriorityId">Null uses the default priority.</param>
/// <param name="LinkTo">Set when creating from a CR: the task and its permanent link are created together (B-03.5).</param>
/// <param name="ChecklistItems">Items added in the task editor before the first save, in display order.</param>
/// <param name="NewLookups">People and tags the editor draft created; inserted only by this save (B-02.6).</param>
public sealed record CreateTaskCommand(
    OperationId OperationId,
    TaskId NewTaskId,
    TaskContentDraft Content,
    TaskStatusId? StatusId = null,
    PriorityId? PriorityId = null,
    DateIntent Start = default,
    DateIntent End = default,
    CrLinkTarget? LinkTo = null,
    IReadOnlyList<ChecklistItemDraft>? ChecklistItems = null,
    NewLookups? NewLookups = null);

/// <summary>Creates a task at the end of its Board column, optionally linked to a CR in the same transaction.</summary>
public sealed class CreateTask
{
    public const string OperationKind = "task.create";

    private readonly IMutationCoordinator _coordinator;

    public CreateTask(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<TaskId>>> ExecuteAsync(CreateTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var content = TaskContentParser.Parse(command.Content, isLinked: command.LinkTo is not null, validation);
        var checklist = ChecklistDrafts.Parse(command.ChecklistItems, validation);
        LookupAdditions.Validate(command.NewLookups, command.Content.DesignerId, command.Content.DeveloperId, validation);
        if (validation.HasErrors || content is null || checklist is null)
        {
            return UseCaseResults.Failed<TaskId>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command, content, checklist), cancellationToken);
    }

    private static TxResult<TaskId> Execute(
        IWriteTransaction tx,
        CreateTaskCommand command,
        TaskContent content,
        IReadOnlyList<(ChecklistItemId Id, ChecklistText Text, bool IsCompleted)> checklist)
    {
        if (TransactionGuards.EnsureNewId(tx, EntityKind.Task, command.NewTaskId.Value, tx.Tasks.Find(command.NewTaskId) is not null) is { } idError)
        {
            return TxResult.Abort<TaskId>(idError);
        }

        ChangeRequest? changeRequest = null;
        if (command.LinkTo is { } target && !TransactionGuards.TryLoadChangeRequest(tx, target.Id, target.ExpectedRevision, out changeRequest, out var crError))
        {
            return TxResult.Abort<TaskId>(crError);
        }

        content = LookupAdditions.Apply(tx, command.NewLookups, content);
        var workflow = tx.Workflow.Read();
        var validation = new ValidationCollector();
        var status = workflow.FindTaskStatus(command.StatusId ?? workflow.Defaults.DefaultTaskStatusId);
        var priorityId = command.PriorityId ?? workflow.Defaults.DefaultPriorityId;
        if (status is null)
        {
            validation.Add(new DomainError(DomainErrorCodes.UnknownReference, "Status"));
        }

        if (workflow.FindPriority(priorityId) is null)
        {
            validation.Add(new DomainError(DomainErrorCodes.UnknownReference, "Priority"));
        }

        TransactionGuards.CheckPeopleAndTags(workflow, content, validation);
        TransactionGuards.CheckCustomValues(workflow, FieldTarget.Task, content.CustomValues, validation);
        if (validation.HasErrors || status is null)
        {
            return TxResult.Abort<TaskId>(validation.ToError());
        }

        if (!ChecklistDrafts.TryBuildNew(tx, checklist, out var items, out var itemError))
        {
            return TxResult.Abort<TaskId>(itemError!);
        }

        var created = TaskItem.Create(command.NewTaskId, content, status, priorityId, command.Start, command.End, changeRequest?.Id,
            BoardColumnRanks.TopRank(tx, status, new BoardCardKey(BoardCardKind.Task, command.NewTaskId.Value)), tx.LocalToday, tx.UtcNow, items);
        if (created.Error is { } invalid)
        {
            return TransactionGuards.Reject<TaskId>(invalid);
        }

        tx.Tasks.Insert(created.Value);
        tx.Changes.UpsertTask(created.Value);
        if (changeRequest is not null)
        {
            var link = new CrTaskLink(created.Value.Id, changeRequest.Id, tx.UtcNow);
            tx.Links.Insert(link);
            tx.Changes.AddLink(link);
        }

        return TxResult.Commit(created.Value.Id);
    }
}
