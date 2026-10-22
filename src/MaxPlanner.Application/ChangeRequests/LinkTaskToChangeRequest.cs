using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Application.ChangeRequests;

/// <param name="ExpectedCrRevision">The CR revision whose title/owner/squad the confirmation previewed (T-13).</param>
public sealed record LinkTaskToChangeRequestCommand(
    OperationId OperationId,
    TaskId TaskId,
    long ExpectedTaskRevision,
    ChangeRequestId ChangeRequestId,
    long ExpectedCrRevision);

/// <summary>
/// Permanently links an unlinked task (active or archived) to a CR (B-03.6, ADR 0006). There is
/// deliberately no unlink or reassign use case.
/// </summary>
public sealed class LinkTaskToChangeRequest
{
    public const string OperationKind = "task.link";

    private readonly IMutationCoordinator _coordinator;

    public LinkTaskToChangeRequest(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The task's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(LinkTaskToChangeRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, LinkTaskToChangeRequestCommand command)
    {
        if (!TransactionGuards.TryLoadTask(tx, command.TaskId, command.ExpectedTaskRevision, out var task, out var taskError))
        {
            return TxResult.Abort<long>(taskError);
        }

        if (!TransactionGuards.TryLoadChangeRequest(tx, command.ChangeRequestId, command.ExpectedCrRevision, out var changeRequest, out var crError))
        {
            return TxResult.Abort<long>(crError);
        }

        // The link table is the authority (T-05); checking it as well as the loaded task keeps the rule
        // from depending on how the store maps the link onto the task.
        if (tx.Links.FindByTask(task.Id) is not null)
        {
            return TransactionGuards.Reject<long>(DomainErrorCodes.TaskAlreadyLinked, "ChangeRequest");
        }

        var linked = task.LinkTo(changeRequest.Id, tx.UtcNow);
        if (linked.Error is { } invalid)
        {
            return TransactionGuards.Reject<long>(invalid);
        }

        var link = new CrTaskLink(task.Id, changeRequest.Id, tx.UtcNow);
        tx.Tasks.Update(linked.Value);
        tx.Links.Insert(link);
        tx.Changes.UpsertTask(linked.Value);
        tx.Changes.AddLink(link);
        return TxResult.Commit(linked.Value.Revision);
    }
}
