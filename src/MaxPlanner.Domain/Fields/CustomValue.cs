using System.Collections.ObjectModel;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Fields;

/// <summary>
/// A typed custom-field value. The hierarchy is closed (one sealed record per <see cref="FieldKind"/>),
/// so a value can never be stored with a kind its field does not declare (B-11: no silent coercion).
/// </summary>
public abstract record CustomValue
{
    private protected CustomValue()
    {
    }

    /// <summary>The value map of a record that has no custom values.</summary>
    public static IReadOnlyDictionary<FieldId, CustomValue> NoValues { get; } = ReadOnlyDictionary<FieldId, CustomValue>.Empty;

    public abstract FieldKind Kind { get; }

    /// <summary>True for blank text and empty lists; a required field treats such a value as missing.</summary>
    public abstract bool IsEmpty { get; }
}

public sealed record TextCustomValue(string Value) : CustomValue
{
    public override FieldKind Kind => FieldKind.Text;

    public override bool IsEmpty => string.IsNullOrWhiteSpace(Value);
}

public sealed record MultilineTextCustomValue(string Value) : CustomValue
{
    public override FieldKind Kind => FieldKind.MultilineText;

    public override bool IsEmpty => string.IsNullOrWhiteSpace(Value);
}

public sealed record NumberCustomValue(decimal Value) : CustomValue
{
    public override FieldKind Kind => FieldKind.Number;

    public override bool IsEmpty => false;
}

/// <summary>A calendar date, never converted through UTC (T-05).</summary>
public sealed record DateCustomValue(DateOnly Value) : CustomValue
{
    public override FieldKind Kind => FieldKind.Date;

    public override bool IsEmpty => false;
}

public sealed record LookupCustomValue(FieldOptionId OptionId) : CustomValue
{
    public override FieldKind Kind => FieldKind.Lookup;

    public override bool IsEmpty => false;
}

public sealed record PeopleCustomValue(IReadOnlyList<PersonId> PersonIds) : CustomValue
{
    public override FieldKind Kind => FieldKind.People;

    public override bool IsEmpty => PersonIds.Count == 0;
}

public sealed record TagsCustomValue(IReadOnlyList<TagId> TagIds) : CustomValue
{
    public override FieldKind Kind => FieldKind.Tags;

    public override bool IsEmpty => TagIds.Count == 0;
}

public sealed record UrlCustomValue(HttpUrl Url) : CustomValue
{
    public override FieldKind Kind => FieldKind.Url;

    public override bool IsEmpty => false;
}
