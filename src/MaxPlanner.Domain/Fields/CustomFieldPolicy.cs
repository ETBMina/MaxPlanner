using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Domain.Fields;

/// <summary>Validation of custom values against the field definitions of one entity type (B-11).</summary>
/// <remarks>
/// Values of inactive (removed) fields are neither validated nor required: they are preserved
/// untouched for export and backup, even if they would no longer satisfy a changed definition.
/// Whether People/Tags IDs exist is a workflow-lookup check made by the use case.
/// </remarks>
public static class CustomFieldPolicy
{
    /// <summary>The first rule violated by <paramref name="values"/>, or null when they are valid.</summary>
    public static DomainError? Validate(FieldTarget target, IReadOnlyCollection<FieldDefinition> definitions, IReadOnlyDictionary<FieldId, CustomValue> values) =>
        Errors(target, definitions, values) is [var first, ..] ? first : null;

    /// <summary>Every violation, in field display order, so an editor can mark all offending fields at once.</summary>
    public static IReadOnlyList<DomainError> Errors(FieldTarget target, IReadOnlyCollection<FieldDefinition> definitions, IReadOnlyDictionary<FieldId, CustomValue> values)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(values);

        var forTarget = definitions.Where(d => d.Target == target).ToDictionary(d => d.Id);
        var errors = new List<DomainError>();
        foreach (var definition in forTarget.Values.Where(d => d.IsActive).OrderBy(d => d.DisplayOrder).ThenBy(d => d.Key.Normalized, StringComparer.Ordinal))
        {
            var hasValue = values.TryGetValue(definition.Id, out var value) && !value.IsEmpty;
            if (!hasValue)
            {
                if (definition.IsRequired)
                {
                    errors.Add(new DomainError(DomainErrorCodes.Required, definition.ErrorField));
                }

                continue;
            }

            if (ValidateValue(definition, value!) is { } invalid)
            {
                errors.Add(invalid);
            }
        }

        errors.AddRange(values.Keys
            .Where(id => !forTarget.ContainsKey(id))
            .Select(id => new DomainError(DomainErrorCodes.UnknownField, $"Custom:{id}")));
        return errors;
    }

    /// <summary>Checks one value against its definition's kind, text limits, and lookup options.</summary>
    public static DomainError? ValidateValue(FieldDefinition definition, CustomValue value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(value);

        if (value.Kind != definition.Kind)
        {
            return new DomainError(DomainErrorCodes.CustomValueKindMismatch, definition.ErrorField);
        }

        return value switch
        {
            TextCustomValue text when text.Value.Length > DomainLimits.CustomText =>
                new DomainError(DomainErrorCodes.TooLong, definition.ErrorField, DomainLimits.CustomText),
            MultilineTextCustomValue text when text.Value.Length > DomainLimits.LongText =>
                new DomainError(DomainErrorCodes.TooLong, definition.ErrorField, DomainLimits.LongText),
            LookupCustomValue lookup when definition.Options.All(o => o.Id != lookup.OptionId) =>
                new DomainError(DomainErrorCodes.UnknownLookupOption, definition.ErrorField),
            _ => null,
        };
    }

    /// <summary>
    /// The values to store when an editor saves <paramref name="submitted"/> over <paramref name="stored"/>:
    /// the editor controls active fields only, and values of inactive fields are carried over unchanged.
    /// </summary>
    public static IReadOnlyDictionary<FieldId, CustomValue> MergeForUpdate(
        FieldTarget target,
        IReadOnlyCollection<FieldDefinition> definitions,
        IReadOnlyDictionary<FieldId, CustomValue> stored,
        IReadOnlyDictionary<FieldId, CustomValue> submitted)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(submitted);

        var inactive = definitions.Where(d => d.Target == target && !d.IsActive).Select(d => d.Id).ToHashSet();
        var merged = submitted.Where(v => !inactive.Contains(v.Key)).ToDictionary();
        foreach (var (id, value) in stored.Where(v => inactive.Contains(v.Key)))
        {
            merged[id] = value;
        }

        return merged.AsReadOnly();
    }
}
