using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Domain.Workflow;

/// <summary>
/// The reference change of one confirmed lookup removal (B-04, B-11): every reference to
/// <see cref="RemovedId"/> moves to <see cref="ReplacementId"/>, or is cleared where clearing is allowed.
/// </summary>
/// <remarks>
/// Applying a remap is not a user edit or a status transition: dates are kept and archived records are
/// remapped too, so every record keeps resolving after the lookup row is deleted (AC-08).
/// <see cref="LookupRemovalPolicy"/> decides beforehand whether clearing is allowed; a remap that would
/// clear a required reference (a status, a priority, a CR Owner) is a defect and throws.
/// </remarks>
public sealed record LookupRemap(LookupKind Kind, Guid RemovedId, Guid? ReplacementId)
{
    /// <summary>The replacement for a required single reference (status, priority, CR Owner).</summary>
    /// <exception cref="InvalidOperationException">The removal has no replacement although the reference is required.</exception>
    public Guid RequiredReplacement => ReplacementId
        ?? throw new InvalidOperationException($"A {Kind} reference is required, so its removal needs a replacement.");

    public bool Targets(LookupKind kind, Guid id) => Kind == kind && id == RemovedId;

    /// <summary>A list of references with the removed value replaced (or dropped) and duplicates removed; null when unchanged.</summary>
    public IReadOnlyList<T>? MapList<T>(LookupKind kind, IReadOnlyList<T> ids, Func<T, Guid> valueOf, Func<Guid, T> create)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (Kind != kind || !ids.Any(id => valueOf(id) == RemovedId))
        {
            return null;
        }

        var mapped = ids.Select(id => valueOf(id) == RemovedId ? ReplacementId : valueOf(id))
            .OfType<Guid>()
            .Distinct()
            .Select(create);
        return [.. mapped];
    }

    /// <summary>People and Tags custom values with the removed person or tag remapped; null when no value changes.</summary>
    public IReadOnlyDictionary<FieldId, CustomValue>? MapCustomValues(IReadOnlyDictionary<FieldId, CustomValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Kind is not (LookupKind.Person or LookupKind.Tag))
        {
            return null;
        }

        Dictionary<FieldId, CustomValue>? changed = null;
        foreach (var (field, value) in values)
        {
            CustomValue? mapped = value switch
            {
                PeopleCustomValue people when MapList(LookupKind.Person, people.PersonIds, p => p.Value, g => new PersonId(g)) is { } ids => new PeopleCustomValue(ids),
                TagsCustomValue tags when MapList(LookupKind.Tag, tags.TagIds, t => t.Value, g => new TagId(g)) is { } ids => new TagsCustomValue(ids),
                _ => null,
            };
            if (mapped is not null)
            {
                changed ??= values.ToDictionary();
                changed[field] = mapped;
            }
        }

        return changed?.AsReadOnly();
    }

    /// <summary>True when a custom value refers to the removed person or tag.</summary>
    public bool References(CustomValue value) => (Kind, value) switch
    {
        (LookupKind.Person, PeopleCustomValue people) => people.PersonIds.Any(p => p.Value == RemovedId),
        (LookupKind.Tag, TagsCustomValue tags) => tags.TagIds.Any(t => t.Value == RemovedId),
        _ => false,
    };
}
