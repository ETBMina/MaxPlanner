using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Application.ChangeRequests;

/// <param name="ExpectedRevision">
/// The CR revision the Calendar showed. When the CR already had dates, the user confirmed replacing exactly
/// those dates (design §4.4), so a CR changed meanwhile is a conflict instead of a silent replacement (B-08.5).
/// </param>
public sealed record ScheduleChangeRequestCommand(OperationId OperationId, ChangeRequestId ChangeRequestId, long ExpectedRevision, DateOnly Day);

/// <summary>
/// Schedules a CR on one day from the Calendar backlog (B-08.5): PlannedStart = PlannedEnd = the day and the
/// configured scheduling status, in one command. Linked tasks and their dates never change (B-03.9).
/// </summary>
public sealed class ScheduleChangeRequest
{
    public const string OperationKind = "cr.schedule";

    private readonly IMutationCoordinator _coordinator;

    public ScheduleChangeRequest(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The CR's new revision, or a conflict when it changed since the Calendar showed it.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(ScheduleChangeRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, ScheduleChangeRequestCommand command)
    {
        if (!TransactionGuards.TryLoadChangeRequest(tx, command.ChangeRequestId, command.ExpectedRevision, out var changeRequest, out var loadError))
        {
            return TxResult.Abort<long>(loadError);
        }

        var workflow = tx.Workflow.Read();
        if (workflow.FindCrStatus(workflow.Defaults.CalendarSchedulingStatusId) is not { } status)
        {
            return TxResult.Abort<long>(ChangeRequestRules.UnknownStatus);
        }

        return ChangeRequestScheduling.Commit(tx, changeRequest.ScheduleOn(command.Day, status, tx.UtcNow));
    }
}

/// <param name="NewStart">The new planned start, or null to keep it.</param>
/// <param name="NewEnd">The new planned end, or null to keep it.</param>
public sealed record ResizeChangeRequestCommand(OperationId OperationId, ChangeRequestId ChangeRequestId, long ExpectedRevision, DateOnly? NewStart, DateOnly? NewEnd);

/// <summary>
/// Changes a scheduled CR's planned range from the Calendar (B-08.8): a bar edge drag or a keyboard step changes
/// only that endpoint; the Edit dates flyout may change both at once. Inverted ranges are rejected, the status
/// is unchanged, and a failure leaves the committed dates, which the Calendar keeps showing.
/// </summary>
public sealed class ResizeChangeRequest
{
    public const string OperationKind = "cr.resize";

    private readonly IMutationCoordinator _coordinator;

    public ResizeChangeRequest(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The CR's new revision; a validation error for an unscheduled CR or an inverted range.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(ResizeChangeRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.NewStart is null && command.NewEnd is null)
        {
            return UseCaseResults.Failed<long>(new ValidationFailedError(new DomainError(DomainErrorCodes.Required, "PlannedStart")));
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, ResizeChangeRequestCommand command)
    {
        if (!TransactionGuards.TryLoadChangeRequest(tx, command.ChangeRequestId, command.ExpectedRevision, out var changeRequest, out var loadError))
        {
            return TxResult.Abort<long>(loadError);
        }

        var resized = (command.NewStart, command.NewEnd) switch
        {
            ({ } start, { } end) => changeRequest.Reschedule(start, end, tx.UtcNow),
            ({ } start, null) => changeRequest.SetPlannedEndpoint(PlannedEndpoint.Start, start, tx.UtcNow),
            _ => changeRequest.SetPlannedEndpoint(PlannedEndpoint.End, command.NewEnd!.Value, tx.UtcNow),
        };

        return resized.Error is { } invalid ? TransactionGuards.Reject<long>(invalid) : ChangeRequestScheduling.Commit(tx, resized.Value);
    }
}

public sealed record MoveChangeRequestToBacklogCommand(OperationId OperationId, ChangeRequestId ChangeRequestId, long ExpectedRevision);

/// <summary>
/// "Move to backlog" (B-08.11): explicitly clears both planned dates and sets the configured backlog status.
/// Ordinary status edits never erase dates; only this command does.
/// </summary>
public sealed class MoveChangeRequestToBacklog
{
    public const string OperationKind = "cr.move-to-backlog";

    private readonly IMutationCoordinator _coordinator;

    public MoveChangeRequestToBacklog(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <returns>The CR's new revision.</returns>
    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(MoveChangeRequestToBacklogCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command), cancellationToken);
    }

    private static TxResult<long> Execute(IWriteTransaction tx, MoveChangeRequestToBacklogCommand command)
    {
        if (!TransactionGuards.TryLoadChangeRequest(tx, command.ChangeRequestId, command.ExpectedRevision, out var changeRequest, out var loadError))
        {
            return TxResult.Abort<long>(loadError);
        }

        var workflow = tx.Workflow.Read();
        return workflow.FindCrStatus(workflow.Defaults.CrBacklogStatusId) is { } status
            ? ChangeRequestScheduling.Commit(tx, changeRequest.MoveToBacklog(status, tx.UtcNow))
            : TxResult.Abort<long>(ChangeRequestRules.UnknownStatus);
    }
}

internal static class ChangeRequestScheduling
{
    /// <summary>
    /// Writes only the CR. Planned dates are not inherited by tasks (B-03.9), so no task or Subtask card row
    /// changes; the store recomposes the CR's read model (overdue flag) from the published change.
    /// </summary>
    public static TxResult<long> Commit(IWriteTransaction tx, ChangeRequest changed)
    {
        tx.ChangeRequests.Update(changed);
        tx.Changes.UpsertChangeRequest(changed);
        return TxResult.Commit(changed.Revision);
    }
}
