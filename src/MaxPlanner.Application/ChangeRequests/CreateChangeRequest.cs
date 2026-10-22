using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.ChangeRequests;

/// <summary>Raw CR editor values (B-03.1). Blank optional text means "no value".</summary>
/// <param name="OwnerId">Required. The editor prefills the default CR Owner; the use case never fills it silently.</param>
/// <param name="PriorityId">Null uses the default priority.</param>
public sealed record ChangeRequestDraft(
    string? Number,
    string? Title,
    PersonId? OwnerId,
    SquadId? SquadId,
    PriorityId? PriorityId,
    DateOnly? PlannedStart,
    DateOnly? PlannedEnd,
    string? EmailSubject,
    string? SolutionDesignUrl,
    string? Description,
    string? TaskNumber,
    IReadOnlyDictionary<FieldId, CustomValue> CustomValues);

/// <param name="StatusId">Null uses the default CR status.</param>
public sealed record CreateChangeRequestCommand(OperationId OperationId, ChangeRequestId NewId, ChangeRequestDraft Draft, CrStatusId? StatusId = null);

/// <summary>Creates a CR with a unique, user-entered number (B-03.1–2).</summary>
public sealed class CreateChangeRequest
{
    public const string OperationKind = "cr.create";

    private readonly IMutationCoordinator _coordinator;

    public CreateChangeRequest(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<ChangeRequestId>>> ExecuteAsync(CreateChangeRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var content = Parse(command.Draft, validation);
        if (validation.HasErrors || content is null)
        {
            return UseCaseResults.Failed<ChangeRequestId>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command, content), cancellationToken);
    }

    /// <returns>The content with <see cref="ChangeRequestContent.PriorityId"/> still to be defaulted, or null when any field is invalid.</returns>
    private static ChangeRequestContent? Parse(ChangeRequestDraft draft, ValidationCollector validation)
    {
        var number = validation.Check(CrNumber.Create(draft.Number));
        var title = validation.Check(TitleText.Create(draft.Title));
        var planned = validation.Check(PlannedRange.CreateOptional(draft.PlannedStart, draft.PlannedEnd));
        var emailSubject = validation.Check(EmailSubject.CreateOptional(draft.EmailSubject));
        var url = validation.Check(HttpUrl.CreateOptional(draft.SolutionDesignUrl));
        var description = validation.Check(LongText.CreateOptional(draft.Description, "Description"));
        var taskNumber = validation.Check(TaskNumber.CreateOptional(draft.TaskNumber));
        if (draft.OwnerId is null)
        {
            // ADR 0020: every CR has an Owner.
            validation.Add(new DomainError(DomainErrorCodes.Required, "Owner"));
        }

        return validation.HasErrors || draft.OwnerId is not { } owner
            ? null
            : new ChangeRequestContent(number.Value, title.Value, owner, draft.SquadId, draft.PriorityId ?? default, planned.Value,
                emailSubject.Value, url.Value, description.Value, taskNumber.Value, draft.CustomValues);
    }

    private static TxResult<ChangeRequestId> Execute(IWriteTransaction tx, CreateChangeRequestCommand command, ChangeRequestContent content)
    {
        if (TransactionGuards.EnsureNewId(tx, EntityKind.ChangeRequest, command.NewId.Value, tx.ChangeRequests.Find(command.NewId) is not null) is { } idError)
        {
            return TxResult.Abort<ChangeRequestId>(idError);
        }

        if (tx.ChangeRequests.FindByNormalizedNumber(content.Number.Normalized) is { } existing)
        {
            return TxResult.Abort<ChangeRequestId>(new DuplicateCrNumberError(existing.Number.Display, existing.Id));
        }

        var workflow = tx.Workflow.Read();
        content = content with { PriorityId = command.Draft.PriorityId ?? workflow.Defaults.DefaultPriorityId };
        var status = workflow.FindCrStatus(command.StatusId ?? workflow.Defaults.DefaultCrStatusId);
        var validation = new ValidationCollector();
        AddUnknown(validation, status is null, "Status");
        AddUnknown(validation, workflow.FindPriority(content.PriorityId) is null, "Priority");
        AddUnknown(validation, workflow.FindPerson(content.OwnerId) is null, "Owner");
        AddUnknown(validation, content.SquadId is { } squad && workflow.FindSquad(squad) is null, "Squad");
        TransactionGuards.CheckCustomValues(workflow, FieldTarget.ChangeRequest, content.CustomValues, validation);
        if (validation.HasErrors || status is null)
        {
            return TxResult.Abort<ChangeRequestId>(validation.ToError());
        }

        var created = ChangeRequest.Create(command.NewId, content, status, tx.UtcNow);
        tx.ChangeRequests.Insert(created);
        tx.Changes.UpsertChangeRequest(created);
        return TxResult.Commit(created.Id);
    }

    private static void AddUnknown(ValidationCollector validation, bool isUnknown, string field)
    {
        if (isUnknown)
        {
            validation.Add(new DomainError(DomainErrorCodes.UnknownReference, field));
        }
    }
}
