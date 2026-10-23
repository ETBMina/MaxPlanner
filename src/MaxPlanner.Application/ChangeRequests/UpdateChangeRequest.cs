using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Application.ChangeRequests;

/// <param name="ExpectedRevision">The CR revision the editor loaded; a different current revision is a conflict.</param>
/// <param name="StatusId">The CR status chosen in the editor (B-03.8: independent of the linked tasks' statuses).</param>
/// <param name="NewOwner">A person typed into the Owner picker that did not exist yet; created only by this save.</param>
/// <param name="MakeDefaultOwner">B-11: also make the owner the default CR Owner.</param>
public sealed record UpdateChangeRequestCommand(
    OperationId OperationId,
    ChangeRequestId ChangeRequestId,
    long ExpectedRevision,
    ChangeRequestDraft Draft,
    CrStatusId StatusId,
    NewPerson? NewOwner = null,
    bool MakeDefaultOwner = false);

/// <summary>
/// Saves the CR editor draft (B-03, design §5.3) with a revision check. The CR owns the Title, Task Number,
/// Squad, and Owner every linked task and Subtask card shows (T-13), so one committed CR change is enough
/// for every dependent display to update: the projection store recomposes the linked tasks and their cards
/// from the published CR. No task row is written, and no task status, date, or link changes (B-03.8–10).
/// </summary>
public sealed class UpdateChangeRequest
{
    public const string OperationKind = "cr.update";

    private readonly IMutationCoordinator _coordinator;

    public UpdateChangeRequest(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>
    /// The CR's new revision; a <see cref="ConcurrencyConflictError"/> when the CR changed since the editor
    /// loaded it, or a <see cref="DuplicateCrNumberError"/> naming the CR that already uses the number.
    /// </returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(UpdateChangeRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var content = ChangeRequestRules.Parse(command.Draft, command.NewOwner, validation);
        if (validation.HasErrors || content is null)
        {
            return UseCaseResults.Failed<long>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command, content), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, UpdateChangeRequestCommand command, ChangeRequestContent content)
    {
        if (!TransactionGuards.TryLoadChangeRequest(tx, command.ChangeRequestId, command.ExpectedRevision, out var changeRequest, out var loadError))
        {
            return TxResult.Abort<long>(loadError);
        }

        if (ChangeRequestRules.CheckUniqueNumber(tx, content.Number, changeRequest.Id) is { } duplicate)
        {
            return TxResult.Abort<long>(duplicate);
        }

        content = ChangeRequestRules.ApplyNewOwner(tx, command.NewOwner, content, out var lookupsChanged);
        var workflow = tx.Workflow.Read();
        content = content with { PriorityId = command.Draft.PriorityId ?? workflow.Defaults.DefaultPriorityId };
        var status = workflow.FindCrStatus(command.StatusId);
        var invalid = ChangeRequestRules.Validate(workflow, content, status);
        if (invalid is not null || status is null)
        {
            return TxResult.Abort<long>(invalid ?? ChangeRequestRules.UnknownStatus);
        }

        var updated = changeRequest.Update(content, status, tx.UtcNow);
        tx.ChangeRequests.Update(updated);
        tx.Changes.UpsertChangeRequest(updated);
        ChangeRequestRules.FinishLookups(tx, command.MakeDefaultOwner, updated.OwnerId, lookupsChanged);
        return TxResult.Commit(updated.Revision);
    }
}
