using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Infrastructure.Persistence.Mapping;

// Storage DTOs for the JSON TEXT columns (ADR 0009). They are infrastructure-only so the domain stays
// free of serialization concerns; property names are part of the stored format and must not be renamed.

/// <summary>One custom value; <see cref="Kind"/> selects which other property holds it.</summary>
internal sealed record CustomValueJson(string Kind, string? Text = null, decimal? Number = null, string? Date = null, Guid? Option = null, IReadOnlyList<Guid>? Ids = null);

internal sealed record PreservedJson(string? PreLinkTitle, IReadOnlyList<string>? UnselectedCrReferences, string? LegacyJson);

internal sealed record FieldOptionJson(Guid Id, string Label, int Order);

internal sealed record MigrationHistoryEntry(int Version, string Name, DateTimeOffset AppliedAtUtc);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, CustomValueJson>))]
[JsonSerializable(typeof(CustomValueJson))]
[JsonSerializable(typeof(PreservedJson))]
[JsonSerializable(typeof(List<FieldOptionJson>))]
[JsonSerializable(typeof(List<MigrationHistoryEntry>))]
[JsonSerializable(typeof(TaskId))]
[JsonSerializable(typeof(ChangeRequestId))]
[JsonSerializable(typeof(SubtaskCardId))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(CascadeDeletionSummary))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;

internal static class StorageJson
{
    /// <summary>
    /// The use-case result types the operation ledger can record and replay. Adding a use case with a new
    /// result type means registering it here and in <see cref="StorageJsonContext"/>; an unregistered type
    /// fails before its transaction starts instead of committing an unreplayable ledger row.
    /// </summary>
    private static readonly HashSet<Type> LedgerResultTypes =
    [
        typeof(TaskId), typeof(ChangeRequestId), typeof(SubtaskCardId), typeof(long), typeof(CascadeDeletionSummary),
    ];

    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is not a registered ledger result type.</exception>
    public static JsonTypeInfo<T> LedgerTypeInfo<T>() =>
        LedgerResultTypes.Contains(typeof(T)) && StorageJsonContext.Default.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> info
            ? info
            : throw new NotSupportedException($"{typeof(T)} is not a registered operation-ledger result type.");

    public static string? WriteCustomValues(IReadOnlyDictionary<FieldId, CustomValue> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var map = values.ToDictionary(v => v.Key.Value.ToString("D"), v => ToJson(v.Value));
        return JsonSerializer.Serialize(map, StorageJsonContext.Default.DictionaryStringCustomValueJson);
    }

    public static IReadOnlyDictionary<FieldId, CustomValue> ReadCustomValues(string? json, string table)
    {
        const string Column = "custom_values_json";
        if (json is null)
        {
            return CustomValue.NoValues;
        }

        var map = Parse(json, StorageJsonContext.Default.DictionaryStringCustomValueJson, table, Column);
        return map.ToDictionary(
            v => Guid.TryParseExact(v.Key, "D", out var id) ? new FieldId(id) : throw new CorruptStoredDataException(table, Column, "field ID"),
            v => FromJson(v.Value, table, Column)).AsReadOnly();
    }

    public static string? WriteDefaultValue(CustomValue? value) =>
        value is null ? null : JsonSerializer.Serialize(ToJson(value), StorageJsonContext.Default.CustomValueJson);

    public static CustomValue? ReadDefaultValue(string? json, string table, string column) =>
        json is null ? null : FromJson(Parse(json, StorageJsonContext.Default.CustomValueJson, table, column), table, column);

    /// <summary>Null when there is nothing to preserve, so ordinary rows keep the column empty.</summary>
    public static string? WritePreserved(PreservedMetadata preserved) =>
        preserved.PreLinkTitle is null && preserved.UnselectedCrReferences.Count == 0 && preserved.LegacyJson is null
            ? null
            : JsonSerializer.Serialize(
                new PreservedJson(preserved.PreLinkTitle, [.. preserved.UnselectedCrReferences], preserved.LegacyJson),
                StorageJsonContext.Default.PreservedJson);

    public static PreservedMetadata ReadPreserved(string? json, string table)
    {
        if (json is null)
        {
            return PreservedMetadata.Empty;
        }

        var stored = Parse(json, StorageJsonContext.Default.PreservedJson, table, "preserved_json");
        return new PreservedMetadata(stored.PreLinkTitle, [.. stored.UnselectedCrReferences ?? []], stored.LegacyJson);
    }

    public static string WriteOptions(IReadOnlyList<FieldOption> options) =>
        JsonSerializer.Serialize([.. options.Select(o => new FieldOptionJson(o.Id.Value, o.Label.Display, o.Order))], StorageJsonContext.Default.ListFieldOptionJson);

    public static IReadOnlyList<FieldOption> ReadOptions(string json, string table) =>
        [.. Parse(json, StorageJsonContext.Default.ListFieldOptionJson, table, "options_json")
            .Select(o => new FieldOption(new FieldOptionId(o.Id), SqlValues.Require(LookupName.Create(o.Label), table, "options_json"), o.Order))];

    public static string WriteHistory(List<MigrationHistoryEntry> history) =>
        JsonSerializer.Serialize(history, StorageJsonContext.Default.ListMigrationHistoryEntry);

    public static List<MigrationHistoryEntry> ReadHistory(string json) =>
        Parse(json, StorageJsonContext.Default.ListMigrationHistoryEntry, "app_metadata", "migration_history_json");

    public static string WriteLedgerResult<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.Serialize(value, info);

    public static T ReadLedgerResult<T>(string json, JsonTypeInfo<T> info) => Parse(json, info, "operation_ledger", "result_json");

    internal static CustomValueJson ToJson(CustomValue value) => value switch
    {
        TextCustomValue text => new("Text", Text: text.Value),
        MultilineTextCustomValue text => new("MultilineText", Text: text.Value),
        NumberCustomValue number => new("Number", Number: number.Value),
        DateCustomValue date => new("Date", Date: SqlValues.FormatDate(date.Value)),
        LookupCustomValue lookup => new("Lookup", Option: lookup.OptionId.Value),
        PeopleCustomValue people => new("People", Ids: [.. people.PersonIds.Select(p => p.Value)]),
        TagsCustomValue tags => new("Tags", Ids: [.. tags.TagIds.Select(t => t.Value)]),
        UrlCustomValue url => new("Url", Text: url.Url.ToString()),
        _ => throw new NotSupportedException($"Unknown custom value type {value.GetType()}."),
    };

    internal static CustomValue FromJson(CustomValueJson json, string table, string column)
    {
        T Present<T>(T? value)
            where T : class => value ?? throw new CorruptStoredDataException(table, column, $"{json.Kind} value missing");

        return json.Kind switch
        {
            "Text" => new TextCustomValue(Present(json.Text)),
            "MultilineText" => new MultilineTextCustomValue(Present(json.Text)),
            "Number" => new NumberCustomValue(json.Number ?? throw new CorruptStoredDataException(table, column, "Number value missing")),
            "Date" => new DateCustomValue(DateOnly.TryParseExact(json.Date, SqlValues.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : throw new CorruptStoredDataException(table, column, "Date value")),
            "Lookup" => new LookupCustomValue(new FieldOptionId(json.Option ?? throw new CorruptStoredDataException(table, column, "Lookup value missing"))),
            "People" => new PeopleCustomValue([.. Present(json.Ids).Select(id => new PersonId(id))]),
            "Tags" => new TagsCustomValue([.. Present(json.Ids).Select(id => new TagId(id))]),
            "Url" => new UrlCustomValue(SqlValues.Require(HttpUrl.CreateOptional(Present(json.Text)), table, column)
                ?? throw new CorruptStoredDataException(table, column, "Url value missing")),
            _ => throw new CorruptStoredDataException(table, column, "unknown custom value kind"),
        };
    }

    private static T Parse<T>(string json, JsonTypeInfo<T> info, string table, string column)
    {
        try
        {
            return JsonSerializer.Deserialize(json, info) ?? throw new CorruptStoredDataException(table, column, "null JSON");
        }
        catch (JsonException ex)
        {
            throw new CorruptStoredDataException($"Stored data in {table}.{column} is not valid JSON.", ex);
        }
    }
}
