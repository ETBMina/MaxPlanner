using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Tasks;

/// <summary>The Copy dialog's draft (B-06, design §5.5).</summary>
/// <param name="NewTaskId">Preallocated, so a retry cannot create a second copy (T-05).</param>
/// <param name="Title">The copy's own title, prefilled from the source's effective title; ignored when <paramref name="LinkTo"/> is set.</param>
/// <param name="Service">Starts blank; required when a CR is chosen (D10).</param>
/// <param name="Options">
/// What to copy. <see cref="CopyOptions.CopyStatus"/> is "Keep current status": the source status when it still
/// exists, otherwise the configured default.
/// </param>
/// <param name="StatusId">An explicit status chosen in the dialog; null applies the B-06 default/keep rule.</param>
/// <param name="PriorityId">An explicit priority; null uses the source priority if it still exists, otherwise the default.</param>
/// <param name="LinkTo">An optional CR confirmed in the dialog (any CR, including one with other tasks); the link is permanent.</param>
public sealed record CopyTaskCommand(
    OperationId OperationId,
    TaskId SourceTaskId,
    TaskId NewTaskId,
    string? Title,
    string? Service,
    CopyOptions Options,
    TaskStatusId? StatusId = null,
    PriorityId? PriorityId = null,
    CrLinkTarget? LinkTo = null);

/// <summary>
/// Creates a genuinely new task from a source task (B-06, AC-09): new task and checklist-item IDs, revision 1,
/// active, at the end of its Board column, unlinked unless the user explicitly chose a CR. Subtask cards
/// are never copied. The source may be archived and linked; it is not changed.
/// </summary>
public sealed class CopyTask
{
    public const string OperationKind = "task.copy";

    private readonly IMutationCoordinator _coordinator;

    public CopyTask(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<TaskId>>> ExecuteAsync(CopyTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Options);
        var validation = new ValidationCollector();
        var isLinked = command.LinkTo is not null;
        var title = isLinked ? DomainResult.Success<TitleText?>(null) : validation.Check(TitleText.Create(command.Title).Map<TitleText?>(t => t));
        var service = validation.Check(ServiceText.CreateOptional(command.Service));
        if (isLinked && service.IsSuccess && service.Value is null)
        {
            validation.Add(new DomainError(DomainErrorCodes.ServiceRequiredWhenLinked, "Service"));
        }

        if (validation.HasErrors)
        {
            return UseCaseResults.Failed<TaskId>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command, title.Value, service.Value), cancellationToken);
    }

    private static TxResult<TaskId> Execute(IWriteTransaction tx, CopyTaskCommand command, TitleText? title, ServiceText? service)
    {
        if (!TransactionGuards.TryLoadTask(tx, command.SourceTaskId, expectedRevision: null, out var source, out var sourceError))
        {
            return TxResult.Abort<TaskId>(sourceError);
        }

        if (TransactionGuards.EnsureNewId(tx, EntityKind.Task, command.NewTaskId.Value, tx.Tasks.Find(command.NewTaskId) is not null) is { } idError)
        {
            return TxResult.Abort<TaskId>(idError);
        }

        ChangeRequest? changeRequest = null;
        if (command.LinkTo is { } target && !TransactionGuards.TryLoadChangeRequest(tx, target.Id, target.ExpectedRevision, out changeRequest, out var crError))
        {
            return TxResult.Abort<TaskId>(crError);
        }

        // The policy needs the source's effective title only to prefill; the dialog already sent the user's
        // title, so any valid title is enough here.
        var sourceCr = source.ChangeRequestId is { } sourceCrId ? tx.ChangeRequests.Find(sourceCrId) : null;
        var effectiveTitle = InheritancePolicy.EffectiveTitle(source.OwnTitle, sourceCr?.Title);
        var workflow = tx.Workflow.Read();
        var draft = CopyTaskPolicy.Draft(
            source,
            effectiveTitle,
            command.Options,
            workflow.Defaults.DefaultTaskStatusId,
            [.. workflow.TaskStatuses.Select(s => s.Id)],
            [.. workflow.Priorities.Select(p => p.Id)],
            workflow.Defaults.DefaultPriorityId,
            Guid.CreateVersion7);

        var validation = new ValidationCollector();
        var status = workflow.FindTaskStatus(command.StatusId ?? draft.StatusId);
        var priorityId = command.PriorityId ?? draft.PriorityId;
        if (status is null)
        {
            validation.Add(new DomainError(DomainErrorCodes.UnknownReference, "Status"));
        }

        if (workflow.FindPriority(priorityId) is null)
        {
            validation.Add(new DomainError(DomainErrorCodes.UnknownReference, "Priority"));
        }

        var content = draft.Content with { OwnTitle = title, Service = service };
        TransactionGuards.CheckPeopleAndTags(workflow, content, validation);
        TransactionGuards.CheckCustomValues(workflow, FieldTarget.Task, content.CustomValues, validation);
        if (validation.HasErrors || status is null)
        {
            return TxResult.Abort<TaskId>(validation.ToError());
        }

        var created = TaskItem.Create(command.NewTaskId, content, status, priorityId, draft.Start, draft.End, changeRequest?.Id,
            BoardColumnRanks.TopRank(tx, status, new BoardCardKey(BoardCardKind.Task, command.NewTaskId.Value)), tx.LocalToday, tx.UtcNow, draft.ChecklistItems);
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
