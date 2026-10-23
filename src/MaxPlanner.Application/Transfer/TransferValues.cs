using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Transfer;

/// <summary>Conversions between domain values and their transfer DTO forms, shared by export and import.</summary>
public static class TransferValues
{
    public static TransferValue ToTransfer(CustomValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var kind = value.Kind.ToString();
        return value switch
        {
            TextCustomValue text => new TransferValue(kind, Text: text.Value),
            MultilineTextCustomValue text => new TransferValue(kind, Text: text.Value),
            NumberCustomValue number => new TransferValue(kind, Number: number.Value),
            DateCustomValue date => new TransferValue(kind, Date: date.Value),
            LookupCustomValue lookup => new TransferValue(kind, Option: lookup.OptionId.Value),
            PeopleCustomValue people => new TransferValue(kind, Ids: [.. people.PersonIds.Select(p => p.Value)]),
            TagsCustomValue tags => new TransferValue(kind, Ids: [.. tags.TagIds.Select(t => t.Value)]),
            UrlCustomValue url => new TransferValue(kind, Text: url.Url.ToString()),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unknown custom value kind."),
        };
    }

    public static IReadOnlyDictionary<string, TransferValue> ToTransfer(IReadOnlyDictionary<FieldId, CustomValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.OrderBy(v => v.Key.Value).ToDictionary(v => v.Key.Value.ToString("D"), v => ToTransfer(v.Value));
    }

    /// <summary>
    /// The domain value of <paramref name="value"/> for a field of <paramref name="kind"/>, mapping option, person,
    /// and tag IDs through the given resolvers. Null when the kind differs or a reference does not resolve.
    /// </summary>
    public static CustomValue? ToDomain(
        TransferValue value,
        FieldKind kind,
        Func<Guid, Guid?> option,
        Func<Guid, Guid?> person,
        Func<Guid, Guid?> tag)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.TryParse<FieldKind>(value.Kind, ignoreCase: false, out var declared) || declared != kind)
        {
            return null;
        }

        return kind switch
        {
            FieldKind.Text when value.Text is { } text => new TextCustomValue(text),
            FieldKind.MultilineText when value.Text is { } text => new MultilineTextCustomValue(text),
            FieldKind.Number when value.Number is { } number => new NumberCustomValue(number),
            FieldKind.Date when value.Date is { } date => new DateCustomValue(date),
            FieldKind.Lookup when value.Option is { } id && option(id) is { } mapped => new LookupCustomValue(new FieldOptionId(mapped)),
            FieldKind.People when MapAll(value.Ids, person) is { } ids => new PeopleCustomValue([.. ids.Select(i => new PersonId(i))]),
            FieldKind.Tags when MapAll(value.Ids, tag) is { } ids => new TagsCustomValue([.. ids.Select(i => new TagId(i))]),
            FieldKind.Url when HttpUrl.CreateOptional(value.Text, "Custom") is { IsSuccess: true, Value: { } url } => new UrlCustomValue(url),
            _ => null,
        };
    }

    public static TransferPreserved? ToTransfer(PreservedMetadata preserved)
    {
        ArgumentNullException.ThrowIfNull(preserved);
        return preserved == PreservedMetadata.Empty || (preserved.PreLinkTitle is null && preserved.UnselectedCrReferences.Count == 0 && preserved.LegacyJson is null)
            ? null
            : new TransferPreserved(preserved.PreLinkTitle, [.. preserved.UnselectedCrReferences], preserved.LegacyJson);
    }

    public static PreservedMetadata ToDomain(TransferPreserved? preserved) =>
        preserved is null
            ? PreservedMetadata.Empty
            : new PreservedMetadata(preserved.PreLinkTitle, [.. preserved.UnselectedCrReferences ?? []], preserved.LegacyJson);

    /// <summary>Value equality for custom value maps (People/Tags hold lists, which records compare by reference).</summary>
    public static bool SameCustomValues(IReadOnlyDictionary<FieldId, CustomValue> left, IReadOnlyDictionary<FieldId, CustomValue> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var other) && SameValue(pair.Value, other));
    }

    private static bool SameValue(CustomValue left, CustomValue right) => (left, right) switch
    {
        (PeopleCustomValue a, PeopleCustomValue b) => a.PersonIds.SequenceEqual(b.PersonIds),
        (TagsCustomValue a, TagsCustomValue b) => a.TagIds.SequenceEqual(b.TagIds),
        _ => left == right,
    };

    private static List<Guid>? MapAll(IReadOnlyList<Guid>? ids, Func<Guid, Guid?> map)
    {
        var mapped = new List<Guid>();
        foreach (var id in ids ?? [])
        {
            if (map(id) is not { } target)
            {
                return null;
            }

            if (!mapped.Contains(target))
            {
                mapped.Add(target);
            }
        }

        return mapped;
    }
}
