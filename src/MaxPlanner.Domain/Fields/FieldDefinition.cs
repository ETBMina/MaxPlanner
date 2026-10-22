using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Fields;

/// <summary>The fixed set of custom-field kinds (B-11). A fixed concept, so an enum (T-04).</summary>
public enum FieldKind
{
    Text,
    MultilineText,
    Number,
    Date,
    Lookup,
    People,
    Tags,
    Url,
}

/// <summary>The entity type a custom field belongs to. Keys are unique per target.</summary>
public enum FieldTarget
{
    Task,
    ChangeRequest,
}

/// <summary>
/// A custom field's stable key: an identifier (letter first, then letters, digits, '_' or '-').
/// <see cref="Normalized"/> is the per-target uniqueness form.
/// </summary>
public sealed record FieldKey
{
    // B-11: built-in identity, link, audit, and inherited fields cannot be replaced by a custom field.
    // Compared without '_' and '-', so "start_date" cannot shadow StartDate either.
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.Ordinal)
    {
        "id", "kind", "title", "owntitle", "subtitle", "service", "status", "statusid", "priority", "priorityid",
        "designers", "designerids", "developers", "developerids", "tags", "tagids", "notes", "startdate", "enddate",
        "archived", "isarchived", "order", "rank", "checklist", "checklistitems", "subtaskcards", "parenttaskid",
        "changerequest", "changerequestid", "cr", "crnumber", "number", "normalizednumber", "tasknumber", "squad",
        "squadid", "owner", "ownerid", "plannedstart", "plannedend", "emailsubject", "solutiondesignurl",
        "solutiondesign", "description", "customvalues", "preservedmetadata", "prelinktitle", "createdatutc",
        "createdat", "updatedatutc", "updatedat", "revision",
    };

    private FieldKey(string display)
    {
        Display = display;
        Normalized = display.ToLowerInvariant();
    }

    public string Display { get; }

    public string Normalized { get; }

    public static DomainResult<FieldKey> Create(string? raw) =>
        Parse(raw).Bind(key => ReservedKeys.Contains(key.Display.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant())
            ? DomainResult.Failure<FieldKey>(DomainErrorCodes.ReservedKey, "Key")
            : DomainResult.Success(key));

    /// <summary>
    /// Rebuilds a stored key. The identifier format is still checked, but not the reserved-name list: that is
    /// a creation rule, and a later version may reserve a name an existing field already uses.
    /// </summary>
    public static DomainResult<FieldKey> Rehydrate(string? raw) => Parse(raw);

    private static DomainResult<FieldKey> Parse(string? raw)
    {
        var text = TextRules.RequiredLine(raw, DomainLimits.FieldKey, "Key");
        if (!text.IsSuccess)
        {
            return text.Map(v => new FieldKey(v));
        }

        var key = text.Value;
        return !char.IsAsciiLetter(key[0]) || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? DomainResult.Failure<FieldKey>(DomainErrorCodes.InvalidKey, "Key")
            : DomainResult.Success(new FieldKey(key));
    }

    public override string ToString() => Display;
}

/// <summary>One choice of a Lookup field. The ID, not the renamable label, is what values store.</summary>
public sealed record FieldOption(FieldOptionId Id, LookupName Label, int Order);

/// <summary>
/// A typed custom-field definition (B-11). Removing a field deactivates it: its stored values stay
/// in records, exports, and backups, but the UI no longer shows or validates them.
/// </summary>
public sealed record FieldDefinition
{
    private FieldDefinition()
    {
    }

    public FieldId Id { get; private init; }

    public FieldKey Key { get; private init; } = null!;

    public FieldTarget Target { get; private init; }

    public LookupName Label { get; private init; } = null!;

    public FieldKind Kind { get; private init; }

    public bool IsRequired { get; private init; }

    /// <summary>The value an editor prefills for a new record; validated against the kind and options.</summary>
    public CustomValue? DefaultValue { get; private init; }

    public int DisplayOrder { get; private init; }

    /// <summary>The choices of a Lookup field; empty for every other kind.</summary>
    public IReadOnlyList<FieldOption> Options { get; private init; } = [];

    public bool IsActive { get; private init; }

    /// <summary>The <see cref="DomainError.Field"/> name for this field, e.g. <c>Custom:risk</c>.</summary>
    public string ErrorField => $"Custom:{Key.Normalized}";

    public static DomainResult<FieldDefinition> Create(
        FieldId id,
        FieldKey key,
        FieldTarget target,
        LookupName label,
        FieldKind kind,
        bool isRequired,
        CustomValue? defaultValue,
        int displayOrder,
        IReadOnlyList<FieldOption> options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(options);

        if ((kind == FieldKind.Lookup) != (options.Count > 0) || options.DistinctBy(o => o.Id).Count() != options.Count)
        {
            return DomainResult.Failure<FieldDefinition>(DomainErrorCodes.InvalidFieldOptions, "Options");
        }

        if (options.DistinctBy(o => o.Label.Normalized).Count() != options.Count)
        {
            return DomainResult.Failure<FieldDefinition>(DomainErrorCodes.DuplicateName, "Options");
        }

        var definition = new FieldDefinition
        {
            Id = id,
            Key = key,
            Target = target,
            Label = label,
            Kind = kind,
            IsRequired = isRequired,
            DefaultValue = defaultValue,
            DisplayOrder = displayOrder,
            Options = [.. options.OrderBy(o => o.Order)],
            IsActive = true,
        };

        return defaultValue is not null && CustomFieldPolicy.ValidateValue(definition, defaultValue) is { } invalid
            ? DomainResult.Failure<FieldDefinition>(invalid with { Field = "DefaultValue" })
            : DomainResult.Success(definition);
    }

    /// <summary>
    /// Rebuilds a stored definition exactly as persisted, including <see cref="IsActive"/> and the stored option
    /// order. Creation rules (reserved keys, duplicate option labels, default-value text limits) are not re-run;
    /// structural invariants are checked so a corrupt row is reported instead of loaded.
    /// </summary>
    /// <remarks>
    /// Checked: Lookup kinds have options and other kinds have none, option IDs are distinct, and a default
    /// value has the field's kind (a Lookup default must name one of the options).
    /// </remarks>
    public static DomainResult<FieldDefinition> Rehydrate(
        FieldId id,
        FieldKey key,
        FieldTarget target,
        LookupName label,
        FieldKind kind,
        bool isRequired,
        CustomValue? defaultValue,
        int displayOrder,
        IReadOnlyList<FieldOption> options,
        bool isActive)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.IsDefined(target))
        {
            return DomainResult.Failure<FieldDefinition>(DomainErrorCodes.InvalidStoredValue, "Target");
        }

        if (!Enum.IsDefined(kind))
        {
            return DomainResult.Failure<FieldDefinition>(DomainErrorCodes.InvalidStoredValue, "Kind");
        }

        if ((kind == FieldKind.Lookup) != (options.Count > 0) || options.DistinctBy(o => o.Id).Count() != options.Count)
        {
            return DomainResult.Failure<FieldDefinition>(DomainErrorCodes.InvalidStoredValue, "Options");
        }

        if (defaultValue is not null
            && (defaultValue.Kind != kind || (defaultValue is LookupCustomValue lookup && options.All(o => o.Id != lookup.OptionId))))
        {
            return DomainResult.Failure<FieldDefinition>(DomainErrorCodes.InvalidStoredValue, "DefaultValue");
        }

        return DomainResult.Success(new FieldDefinition
        {
            Id = id,
            Key = key,
            Target = target,
            Label = label,
            Kind = kind,
            IsRequired = isRequired,
            DefaultValue = defaultValue,
            DisplayOrder = displayOrder,
            Options = [.. options],
            IsActive = isActive,
        });
    }

    /// <summary>B-11 design default: removal deactivates the definition and keeps every stored value.</summary>
    public FieldDefinition Deactivate() => this with { IsActive = false };
}
