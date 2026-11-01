using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Domain.Fields;

/// <summary>
/// The reference change of one removed Lookup field option (ADR 0026: an option removed by a configuration import is
/// resolved like a lookup value): every stored value of <see cref="RemovedId"/> moves to <see cref="ReplacementId"/>,
/// or is cleared when the replacement is null.
/// </summary>
/// <remarks>Like <c>LookupRemap</c>, applying it is not a user edit: archived records are remapped too.</remarks>
public sealed record FieldOptionRemap(FieldId FieldId, FieldOptionId RemovedId, FieldOptionId? ReplacementId)
{
    /// <summary>True when <paramref name="values"/> holds the removed option for this field.</summary>
    public bool References(IReadOnlyDictionary<FieldId, CustomValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.TryGetValue(FieldId, out var value) && value is LookupCustomValue lookup && lookup.OptionId == RemovedId;
    }

    /// <summary>The values with the removed option replaced or cleared; null when no value changes.</summary>
    public IReadOnlyDictionary<FieldId, CustomValue>? MapCustomValues(IReadOnlyDictionary<FieldId, CustomValue> values)
    {
        if (!References(values))
        {
            return null;
        }

        var changed = values.ToDictionary();
        if (ReplacementId is { } replacement)
        {
            changed[FieldId] = new LookupCustomValue(replacement);
        }
        else
        {
            changed.Remove(FieldId);
        }

        return changed.AsReadOnly();
    }
}
