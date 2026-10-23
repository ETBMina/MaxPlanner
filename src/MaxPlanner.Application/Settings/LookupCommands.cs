using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Settings;

/// <param name="Id">Preallocated, so a retry after an uncertain outcome cannot add the value twice (T-05).</param>
public sealed record AddLookupCommand(OperationId OperationId, LookupKind Kind, Guid Id, string Name);

/// <summary>
/// Adds a status, priority, squad, person, or tag in Settings (B-04, B-11). Ordered lookups append at the end;
/// new statuses start without semantic flags, so adding one never changes date automation or completion.
/// </summary>
public sealed class AddLookup
{
    public const string OperationKind = "workflow.lookup-add";

    private readonly IMutationCoordinator _coordinator;

    public AddLookup(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>0 on success; a <see cref="ValidationFailedError"/> for a blank, too long, or duplicate name.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(AddLookupCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var name = LookupName.Create(command.Name);
        if (name.Error is { } invalid)
        {
            return UseCaseResults.Failed<long>(new ValidationFailedError(invalid));
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            var workflow = tx.Workflow.Read();
            var entries = WorkflowLookups.Entries(workflow, command.Kind);
            if (entries.Any(e => e.Id == command.Id))
            {
                return TransactionGuards.Reject<long>(DomainErrorCodes.DuplicateKey, "Id");
            }

            if (WorkflowConfigurationPolicy.ValidateUniqueName(entries.Select(e => e.Name), name.Value) is { } duplicate)
            {
                return TransactionGuards.Reject<long>(duplicate);
            }

            return SettingsTransactions.Save(tx, WorkflowLookups.Add(workflow, command.Kind, command.Id, name.Value));
        }, cancellationToken);
    }
}

/// <param name="ExpectedStamp"><see cref="WorkflowStamps.Item"/> of the value as the user saw it.</param>
public sealed record RenameLookupCommand(OperationId OperationId, LookupKind Kind, Guid Id, string ExpectedStamp, string Name);

/// <summary>
/// Renames a lookup value (B-04): only the label changes, never the identity, so every display (filtered-out
/// and archived records included) shows the new name after one commit. A rename is not a status transition.
/// </summary>
public sealed class RenameLookup
{
    public const string OperationKind = "workflow.lookup-rename";

    private readonly IMutationCoordinator _coordinator;

    public RenameLookup(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(RenameLookupCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var name = LookupName.Create(command.Name);
        if (name.Error is { } invalid)
        {
            return UseCaseResults.Failed<long>(new ValidationFailedError(invalid));
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            var workflow = tx.Workflow.Read();
            if (WorkflowStamps.Item(workflow, command.Kind, command.Id) != command.ExpectedStamp)
            {
                return TxResult.Abort<long>(WorkflowChangedError.Instance);
            }

            var others = WorkflowLookups.Entries(workflow, command.Kind).Where(e => e.Id != command.Id).Select(e => e.Name);
            if (WorkflowConfigurationPolicy.ValidateUniqueName(others, name.Value) is { } duplicate)
            {
                return TransactionGuards.Reject<long>(duplicate);
            }

            return SettingsTransactions.Save(tx, WorkflowLookups.Rename(workflow, command.Kind, command.Id, name.Value));
        }, cancellationToken);
    }
}

/// <param name="Offset">-1 moves the value up (earlier in order), +1 down.</param>
/// <param name="ExpectedOrderStamp"><see cref="WorkflowStamps.Order"/> of the list the user saw.</param>
public sealed record MoveLookupCommand(OperationId OperationId, LookupKind Kind, Guid Id, string ExpectedOrderStamp, int Offset);

/// <summary>
/// Moves a status, priority, or squad up or down (B-04). Board columns, Calendar status lists, and Dashboard
/// charts follow the configured order; no record changes.
/// </summary>
public sealed class MoveLookup
{
    public const string OperationKind = "workflow.lookup-move";

    private readonly IMutationCoordinator _coordinator;

    public MoveLookup(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(MoveLookupCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!WorkflowLookups.IsOrdered(command.Kind) || command.Offset is not (-1 or 1))
        {
            return UseCaseResults.Failed<long>(new ValidationFailedError(new DomainError(DomainErrorCodes.ItemNotFound, "Order")));
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            var workflow = tx.Workflow.Read();
            if (WorkflowStamps.Order(workflow, command.Kind) != command.ExpectedOrderStamp)
            {
                return TxResult.Abort<long>(WorkflowChangedError.Instance);
            }

            var ids = WorkflowLookups.Entries(workflow, command.Kind).Select(e => e.Id).ToList();
            var index = ids.IndexOf(command.Id);
            var target = index + command.Offset;
            if (index < 0 || target < 0 || target >= ids.Count)
            {
                return TransactionGuards.Reject<long>(DomainErrorCodes.ItemNotFound, "Order");
            }

            (ids[index], ids[target]) = (ids[target], ids[index]);
            return SettingsTransactions.Save(tx, WorkflowLookups.Reorder(workflow, command.Kind, ids));
        }, cancellationToken);
    }
}

/// <param name="ExpectedStamp"><see cref="WorkflowStamps.Item"/> of the status as the user saw it.</param>
public sealed record SetTaskStatusFlagsCommand(OperationId OperationId, TaskStatusId Id, string ExpectedStamp, bool IsCompleted, bool SetsStartDate, bool IsBacklog);

/// <summary>
/// Changes the semantic flags of a task status (B-04). Completion and date automation read these flags, never
/// labels. A flag change is configuration, not a transition: no stored date is filled or cleared retroactively.
/// </summary>
public sealed class SetTaskStatusFlags
{
    public const string OperationKind = "workflow.task-status-flags";

    private readonly IMutationCoordinator _coordinator;

    public SetTaskStatusFlags(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(SetTaskStatusFlagsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            var workflow = tx.Workflow.Read();
            if (WorkflowStamps.Item(workflow, LookupKind.TaskStatus, command.Id.Value) != command.ExpectedStamp)
            {
                return TxResult.Abort<long>(WorkflowChangedError.Instance);
            }

            var updated = workflow with
            {
                TaskStatuses = [.. workflow.TaskStatuses.Select(s => s.Id == command.Id
                    ? s with { IsCompleted = command.IsCompleted, SetsStartDate = command.SetsStartDate, IsBacklog = command.IsBacklog }
                    : s)],
            };
            return SettingsTransactions.Save(tx, updated);
        }, cancellationToken);
    }
}

/// <param name="ExpectedStamp"><see cref="WorkflowStamps.Item"/> of the status as the user saw it.</param>
public sealed record SetCrStatusFlagsCommand(OperationId OperationId, CrStatusId Id, string ExpectedStamp, bool IsCompleted, bool IsBacklog);

/// <summary>
/// Changes the semantic flags of a CR status (B-04): Completed drives the overdue predicate (B-15) and Backlog
/// the Calendar backlog (B-08.3). The configured "Move to backlog" status must keep its Backlog flag.
/// </summary>
public sealed class SetCrStatusFlags
{
    public const string OperationKind = "workflow.cr-status-flags";

    private readonly IMutationCoordinator _coordinator;

    public SetCrStatusFlags(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(SetCrStatusFlagsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            var workflow = tx.Workflow.Read();
            if (WorkflowStamps.Item(workflow, LookupKind.CrStatus, command.Id.Value) != command.ExpectedStamp)
            {
                return TxResult.Abort<long>(WorkflowChangedError.Instance);
            }

            var updated = workflow with
            {
                CrStatuses = [.. workflow.CrStatuses.Select(s => s.Id == command.Id ? s with { IsCompleted = command.IsCompleted, IsBacklog = command.IsBacklog } : s)],
            };
            if (WorkflowConfigurationPolicy.ValidateCrBacklogStatus(updated.FindCrStatus(updated.Defaults.CrBacklogStatusId)) is { } invalid)
            {
                return TransactionGuards.Reject<long>(invalid with { Field = "IsBacklog" });
            }

            return SettingsTransactions.Save(tx, updated);
        }, cancellationToken);
    }
}

/// <param name="ExpectedStamp"><see cref="WorkflowStamps.Defaults"/> as the user saw them.</param>
/// <param name="DefaultCrOwnerId">B-04/B-11: null means no default; new CRs then start without an Owner.</param>
public sealed record UpdateWorkflowDefaultsCommand(
    OperationId OperationId,
    string ExpectedStamp,
    TaskStatusId DefaultTaskStatusId,
    CrStatusId DefaultCrStatusId,
    PriorityId DefaultPriorityId,
    PersonId? DefaultCrOwnerId,
    CrStatusId CalendarSchedulingStatusId,
    CrStatusId CrBacklogStatusId);

/// <summary>
/// Saves the workspace defaults (B-04, B-11): new-task status and priority, new-CR status, the default CR
/// Owner, the Calendar scheduling status, and the "Move to backlog" status. Every reference must resolve.
/// </summary>
public sealed class UpdateWorkflowDefaults
{
    public const string OperationKind = "workflow.defaults";

    private readonly IMutationCoordinator _coordinator;

    public UpdateWorkflowDefaults(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(UpdateWorkflowDefaultsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            var workflow = tx.Workflow.Read();
            if (WorkflowStamps.Defaults(workflow) != command.ExpectedStamp)
            {
                return TxResult.Abort<long>(WorkflowChangedError.Instance);
            }

            var validation = new ValidationCollector();
            void Resolve(bool exists, string field) => validation.Add(exists ? null : new DomainError(DomainErrorCodes.UnknownReference, field));
            Resolve(workflow.FindTaskStatus(command.DefaultTaskStatusId) is not null, "DefaultTaskStatus");
            Resolve(workflow.FindCrStatus(command.DefaultCrStatusId) is not null, "DefaultCrStatus");
            Resolve(workflow.FindPriority(command.DefaultPriorityId) is not null, "DefaultPriority");
            Resolve(command.DefaultCrOwnerId is not { } owner || workflow.FindPerson(owner) is not null, "DefaultCrOwner");
            Resolve(workflow.FindCrStatus(command.CalendarSchedulingStatusId) is not null, "CalendarSchedulingStatus");
            validation.Add(WorkflowConfigurationPolicy.ValidateCrBacklogStatus(workflow.FindCrStatus(command.CrBacklogStatusId)));
            if (validation.HasErrors)
            {
                return TxResult.Abort<long>(validation.ToError());
            }

            var defaults = new WorkflowDefaults(
                command.DefaultTaskStatusId,
                command.DefaultCrStatusId,
                command.DefaultPriorityId,
                command.DefaultCrOwnerId,
                command.CalendarSchedulingStatusId,
                command.CrBacklogStatusId);
            return SettingsTransactions.Save(tx, workflow with { Defaults = defaults });
        }, cancellationToken);
    }
}

internal static class SettingsTransactions
{
    /// <summary>Writes the new configuration and publishes it, so every view relabels and regroups after one commit.</summary>
    public static TxResult<long> Save(IWriteTransaction tx, WorkflowSnapshot workflow)
    {
        tx.Lookups.SaveWorkflow(workflow);
        tx.Changes.ReplaceWorkflow(tx.Workflow.Read());
        return TxResult.Commit(0L);
    }
}
