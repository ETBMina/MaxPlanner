using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

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
/// <param name="NewOwner">
/// A person typed into the Owner picker that did not exist yet (B-02.6 add-new). It is created only by this
/// save; <see cref="ChangeRequestDraft.OwnerId"/> refers to its preallocated ID.
/// </param>
/// <param name="MakeDefaultOwner">B-11 first-CR prompt: also make the owner the default CR Owner.</param>
public sealed record CreateChangeRequestCommand(
    OperationId OperationId,
    ChangeRequestId NewId,
    ChangeRequestDraft Draft,
    CrStatusId? StatusId = null,
    NewPerson? NewOwner = null,
    bool MakeDefaultOwner = false);

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
        var content = ChangeRequestRules.Parse(command.Draft, command.NewOwner, validation);
        if (validation.HasErrors || content is null)
        {
            return UseCaseResults.Failed<ChangeRequestId>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx => Execute(tx, command, content), cancellationToken);
    }

    private static TxResult<ChangeRequestId> Execute(IWriteTransaction tx, CreateChangeRequestCommand command, ChangeRequestContent content)
    {
        if (TransactionGuards.EnsureNewId(tx, EntityKind.ChangeRequest, command.NewId.Value, tx.ChangeRequests.Find(command.NewId) is not null) is { } idError)
        {
            return TxResult.Abort<ChangeRequestId>(idError);
        }

        if (ChangeRequestRules.CheckUniqueNumber(tx, content.Number, self: null) is { } duplicate)
        {
            return TxResult.Abort<ChangeRequestId>(duplicate);
        }

        content = ChangeRequestRules.ApplyNewOwner(tx, command.NewOwner, content, out var lookupsChanged);
        var workflow = tx.Workflow.Read();
        content = content with { PriorityId = command.Draft.PriorityId ?? workflow.Defaults.DefaultPriorityId };
        var status = workflow.FindCrStatus(command.StatusId ?? workflow.Defaults.DefaultCrStatusId);
        var invalid = ChangeRequestRules.Validate(workflow, content, status);
        if (invalid is not null || status is null)
        {
            return TxResult.Abort<ChangeRequestId>(invalid ?? ChangeRequestRules.UnknownStatus);
        }

        var created = ChangeRequest.Create(command.NewId, content, status, tx.UtcNow);
        tx.ChangeRequests.Insert(created);
        tx.Changes.UpsertChangeRequest(created);
        ChangeRequestRules.FinishLookups(tx, command.MakeDefaultOwner, created.OwnerId, lookupsChanged);
        return TxResult.Commit(created.Id);
    }
}

/// <summary>The field rules the CR create and update use cases share (B-03.1–3, B-11).</summary>
internal static class ChangeRequestRules
{
    public static ValidationFailedError UnknownStatus { get; } = new(new DomainError(DomainErrorCodes.UnknownReference, "Status"));

    /// <returns>The content with <see cref="ChangeRequestContent.PriorityId"/> still to be defaulted, or null when any field is invalid.</returns>
    public static ChangeRequestContent? Parse(ChangeRequestDraft draft, NewPerson? newOwner, ValidationCollector validation)
    {
        ArgumentNullException.ThrowIfNull(draft);
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

        if (newOwner is not null)
        {
            validation.Check(LookupName.Create(newOwner.Name, "Owner"));
        }

        return validation.HasErrors || draft.OwnerId is not { } owner
            ? null
            : new ChangeRequestContent(number.Value, title.Value, owner, draft.SquadId, draft.PriorityId ?? default, planned.Value,
                emailSubject.Value, url.Value, description.Value, taskNumber.Value, draft.CustomValues);
    }

    /// <summary>B-03.2: numbers are unique after trim/case normalization; the error names the existing CR.</summary>
    public static DuplicateCrNumberError? CheckUniqueNumber(IWriteTransaction tx, CrNumber number, ChangeRequestId? self) =>
        tx.ChangeRequests.FindByNormalizedNumber(number.Normalized) is { } existing && existing.Id != self
            ? new DuplicateCrNumberError(existing.Number.Display, existing.Id)
            : null;

    /// <summary>
    /// Creates the typed new owner when the draft uses it. A person with the same name created meanwhile is
    /// used instead of a duplicate (B-02.6 trim and deduplicate).
    /// </summary>
    public static ChangeRequestContent ApplyNewOwner(IWriteTransaction tx, NewPerson? newOwner, ChangeRequestContent content, out bool inserted)
    {
        inserted = false;
        if (newOwner is null || content.OwnerId != newOwner.Id)
        {
            return content;
        }

        var name = LookupName.Create(newOwner.Name, "Owner").Value;
        if (tx.Workflow.Read().People.FirstOrDefault(p => p.Name.Normalized == name.Normalized) is { } existing)
        {
            return content with { OwnerId = existing.Id };
        }

        tx.Lookups.InsertPerson(new PersonDefinition(newOwner.Id, name));
        inserted = true;
        return content;
    }

    /// <summary>Every reference must resolve in the current workflow (B-04: references always resolve).</summary>
    public static ValidationFailedError? Validate(WorkflowSnapshot workflow, ChangeRequestContent content, CrStatusDefinition? status)
    {
        var validation = new ValidationCollector();
        AddUnknown(validation, status is null, "Status");
        AddUnknown(validation, workflow.FindPriority(content.PriorityId) is null, "Priority");
        AddUnknown(validation, workflow.FindPerson(content.OwnerId) is null, "Owner");
        AddUnknown(validation, content.SquadId is { } squad && workflow.FindSquad(squad) is null, "Squad");
        TransactionGuards.CheckCustomValues(workflow, FieldTarget.ChangeRequest, content.CustomValues, validation);
        return validation.HasErrors ? validation.ToError() : null;
    }

    /// <summary>Applies the B-11 default-owner choice and publishes the workflow when people or defaults changed.</summary>
    public static void FinishLookups(IWriteTransaction tx, bool makeDefaultOwner, PersonId ownerId, bool lookupsChanged)
    {
        if (makeDefaultOwner && tx.Workflow.Read().Defaults.DefaultCrOwnerId != ownerId)
        {
            tx.Lookups.SetDefaultCrOwner(ownerId);
            lookupsChanged = true;
        }

        if (lookupsChanged)
        {
            tx.Changes.ReplaceWorkflow(tx.Workflow.Read());
        }
    }

    private static void AddUnknown(ValidationCollector validation, bool isUnknown, string field)
    {
        if (isUnknown)
        {
            validation.Add(new DomainError(DomainErrorCodes.UnknownReference, field));
        }
    }
}
