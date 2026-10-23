using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Settings;

/// <summary>One choice of a Lookup field as the field dialog edits it; existing options keep their IDs.</summary>
public sealed record FieldOptionDraft(FieldOptionId Id, string Label);

/// <summary>The editable part of a custom field definition (B-11; design §4.6 field dialog).</summary>
/// <param name="DefaultValue">Prefilled into new records; validated against the kind and options.</param>
/// <param name="Options">Lookup choices in display order; empty for every other kind.</param>
public sealed record FieldDefinitionDraft(string Label, bool IsRequired, CustomValue? DefaultValue, IReadOnlyList<FieldOptionDraft> Options);

/// <param name="Id">Preallocated, so a retry cannot create the field twice (T-05).</param>
/// <param name="Key">The stable key (B-11): unique per target, never a built-in field name, fixed after this save.</param>
public sealed record AddFieldDefinitionCommand(OperationId OperationId, FieldId Id, FieldTarget Target, string Key, FieldKind Kind, FieldDefinitionDraft Draft);

/// <summary>
/// Adds a typed custom field to tasks or CRs (B-11). It appears after the existing fields. A required field can
/// only be added while no record of its target exists; otherwise existing records would be invalid, so it must
/// start optional (B-11: required only after a reviewed backfill).
/// </summary>
public sealed class AddFieldDefinition
{
    public const string OperationKind = "workflow.field-add";

    private readonly IMutationCoordinator _coordinator;

    public AddFieldDefinition(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(AddFieldDefinitionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var key = validation.Check(FieldKey.Create(command.Key));
        var parts = FieldDrafts.Parse(command.Draft, validation);
        if (validation.HasErrors || !key.IsSuccess || parts is null)
        {
            return UseCaseResults.Failed<long>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            var workflow = tx.Workflow.Read();
            if (workflow.Fields.Any(f => f.Id == command.Id))
            {
                return TransactionGuards.Reject<long>(DomainErrorCodes.DuplicateKey, "Id");
            }

            // Keys stay reserved by deactivated fields too: their values are still exported under the key.
            if (workflow.FieldsFor(command.Target).Any(f => f.Key.Normalized == key.Value.Normalized))
            {
                return TransactionGuards.Reject<long>(DomainErrorCodes.DuplicateKey, "Key");
            }

            var order = workflow.FieldsFor(command.Target) is { Count: > 0 } fields ? fields.Max(f => f.DisplayOrder) + 1 : 0;
            var created = FieldDefinition.Create(
                command.Id, key.Value, command.Target, parts.Value.Label, command.Kind, parts.Value.IsRequired, command.Draft.DefaultValue, order, parts.Value.Options);
            if (created.Error is { } invalid)
            {
                return TransactionGuards.Reject<long>(invalid);
            }

            if (created.Value.IsRequired && FieldDrafts.CountMissing(tx, created.Value) is > 0 and var missing)
            {
                return TransactionGuards.Reject<long>(DomainErrorCodes.RequiredFieldHasMissingValues, "IsRequired", missing);
            }

            if (FieldDrafts.CheckLookupDefault(workflow, created.Value) is { } unknown)
            {
                return TransactionGuards.Reject<long>(unknown);
            }

            return SettingsTransactions.Save(tx, workflow with { Fields = [.. workflow.Fields, created.Value] });
        }, cancellationToken);
    }
}

/// <param name="ExpectedStamp"><see cref="WorkflowStamps.Field"/> as the dialog loaded it.</param>
public sealed record UpdateFieldDefinitionCommand(OperationId OperationId, FieldId Id, string ExpectedStamp, FieldDefinitionDraft Draft);

/// <summary>
/// Edits a custom field's label, required flag, default, and Lookup options (B-11). Stored values are kept:
/// labels and options are referenced by ID. Making a field required is refused while records of its target
/// have no value, and a Lookup option that stored values use cannot be removed.
/// </summary>
public sealed class UpdateFieldDefinition
{
    public const string OperationKind = "workflow.field-update";

    private readonly IMutationCoordinator _coordinator;

    public UpdateFieldDefinition(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(UpdateFieldDefinitionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var validation = new ValidationCollector();
        var parts = FieldDrafts.Parse(command.Draft, validation);
        if (validation.HasErrors || parts is null)
        {
            return UseCaseResults.Failed<long>(validation.ToError());
        }

        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            var workflow = tx.Workflow.Read();
            if (WorkflowStamps.Field(workflow, command.Id) != command.ExpectedStamp || workflow.Fields.FirstOrDefault(f => f.Id == command.Id) is not { } field)
            {
                return TxResult.Abort<long>(WorkflowChangedError.Instance);
            }

            var edited = field.Edit(parts.Value.Label, parts.Value.IsRequired, command.Draft.DefaultValue, parts.Value.Options);
            if (edited.Error is { } invalid)
            {
                return TransactionGuards.Reject<long>(invalid);
            }

            var removedOptions = field.Options.Select(o => o.Id).Except(edited.Value.Options.Select(o => o.Id)).ToHashSet();
            if (removedOptions.Count > 0 && FieldDrafts.UsesAnyOption(tx, field, removedOptions))
            {
                return TransactionGuards.Reject<long>(DomainErrorCodes.OptionInUse, "Options");
            }

            if (edited.Value.IsRequired && !field.IsRequired && FieldDrafts.CountMissing(tx, edited.Value) is > 0 and var missing)
            {
                return TransactionGuards.Reject<long>(DomainErrorCodes.RequiredFieldHasMissingValues, "IsRequired", missing);
            }

            if (FieldDrafts.CheckLookupDefault(workflow, edited.Value) is { } unknown)
            {
                return TransactionGuards.Reject<long>(unknown);
            }

            return SettingsTransactions.Save(tx, workflow with { Fields = [.. workflow.Fields.Select(f => f.Id == field.Id ? edited.Value : f)] });
        }, cancellationToken);
    }
}

/// <param name="ExpectedOrderStamp"><see cref="WorkflowStamps.FieldOrder"/> of the list the user saw.</param>
/// <param name="Offset">-1 moves the field up, +1 down, among the active fields of its target.</param>
public sealed record MoveFieldDefinitionCommand(OperationId OperationId, FieldId Id, string ExpectedOrderStamp, int Offset);

/// <summary>Reorders the active custom fields of one target (B-11); stored values are untouched.</summary>
public sealed class MoveFieldDefinition
{
    public const string OperationKind = "workflow.field-move";

    private readonly IMutationCoordinator _coordinator;

    public MoveFieldDefinition(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(MoveFieldDefinitionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            var workflow = tx.Workflow.Read();
            if (workflow.Fields.FirstOrDefault(f => f.Id == command.Id) is not { } field
                || WorkflowStamps.FieldOrder(workflow, field.Target) != command.ExpectedOrderStamp)
            {
                return TxResult.Abort<long>(WorkflowChangedError.Instance);
            }

            var active = workflow.FieldsFor(field.Target).Where(f => f.IsActive).OrderBy(f => f.DisplayOrder).ThenBy(f => f.Id.Value).ToList();
            var index = active.FindIndex(f => f.Id == field.Id);
            var target = index + command.Offset;
            if (command.Offset is not (-1 or 1) || index < 0 || target < 0 || target >= active.Count)
            {
                return TransactionGuards.Reject<long>(DomainErrorCodes.ItemNotFound, "Order");
            }

            (active[index], active[target]) = (active[target], active[index]);
            var orders = active.Select((f, i) => (f.Id, i)).ToDictionary(p => p.Id, p => p.i);
            var fields = workflow.Fields.Select(f => orders.TryGetValue(f.Id, out var order) ? f.WithDisplayOrder(order) : f);
            return SettingsTransactions.Save(tx, workflow with { Fields = [.. fields] });
        }, cancellationToken);
    }
}

/// <param name="ExpectedStamp"><see cref="WorkflowStamps.Field"/> as the user saw it.</param>
public sealed record DeactivateFieldDefinitionCommand(OperationId OperationId, FieldId Id, string ExpectedStamp);

/// <summary>
/// Removes a custom field from the UI (B-11 design default): the definition is deactivated, and every stored
/// value stays in records, full exports, and backups. A saved Grid layout that names the field simply drops it.
/// </summary>
public sealed class DeactivateFieldDefinition
{
    public const string OperationKind = "workflow.field-deactivate";

    private readonly IMutationCoordinator _coordinator;

    public DeactivateFieldDefinition(IMutationCoordinator coordinator) => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AppResult<CommandOutcome<long>>> ExecuteAsync(DeactivateFieldDefinitionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(command.OperationId, OperationKind, tx =>
        {
            var workflow = tx.Workflow.Read();
            if (WorkflowStamps.Field(workflow, command.Id) != command.ExpectedStamp || workflow.Fields.FirstOrDefault(f => f.Id == command.Id) is not { IsActive: true } field)
            {
                return TxResult.Abort<long>(WorkflowChangedError.Instance);
            }

            return SettingsTransactions.Save(tx, workflow with { Fields = [.. workflow.Fields.Select(f => f.Id == field.Id ? f.Deactivate() : f)] });
        }, cancellationToken);
    }
}

internal static class FieldDrafts
{
    public static (LookupName Label, bool IsRequired, IReadOnlyList<FieldOption> Options)? Parse(FieldDefinitionDraft draft, ValidationCollector validation)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var label = validation.Check(LookupName.Create(draft.Label, "Label"));
        var options = new List<FieldOption>();
        for (var i = 0; i < draft.Options.Count; i++)
        {
            var option = validation.Check(LookupName.Create(draft.Options[i].Label, "Options"));
            if (option.IsSuccess)
            {
                options.Add(new FieldOption(draft.Options[i].Id, option.Value, i));
            }
        }

        return validation.HasErrors ? null : (label.Value, draft.IsRequired, options);
    }

    /// <summary>B-11: how many records of the field's target (archived tasks included) have no value for it.</summary>
    public static int CountMissing(IWriteTransaction tx, FieldDefinition field)
    {
        bool Missing(IReadOnlyDictionary<FieldId, CustomValue> values) => !values.TryGetValue(field.Id, out var value) || value.IsEmpty;
        return field.Target == FieldTarget.Task
            ? tx.Tasks.ListAll().Count(t => Missing(t.CustomValues))
            : tx.ChangeRequests.ListAll().Count(c => Missing(c.CustomValues));
    }

    public static bool UsesAnyOption(IWriteTransaction tx, FieldDefinition field, HashSet<FieldOptionId> options)
    {
        bool Uses(IReadOnlyDictionary<FieldId, CustomValue> values) =>
            values.TryGetValue(field.Id, out var value) && value is LookupCustomValue lookup && options.Contains(lookup.OptionId);
        return field.Target == FieldTarget.Task
            ? tx.Tasks.ListAll().Any(t => Uses(t.CustomValues))
            : tx.ChangeRequests.ListAll().Any(c => Uses(c.CustomValues));
    }

    /// <summary>A People or Tags default must name existing lookup values.</summary>
    public static DomainError? CheckLookupDefault(WorkflowSnapshot workflow, FieldDefinition field) => field.DefaultValue switch
    {
        PeopleCustomValue people when people.PersonIds.Any(id => workflow.FindPerson(id) is null) => new DomainError(DomainErrorCodes.UnknownReference, "DefaultValue"),
        TagsCustomValue tags when tags.TagIds.Any(id => workflow.FindTag(id) is null) => new DomainError(DomainErrorCodes.UnknownReference, "DefaultValue"),
        _ => null,
    };
}
