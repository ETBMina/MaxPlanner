using System.Text.Json;
using System.Text.Json.Serialization;
using MaxPlanner.Application.Transfer;

namespace MaxPlanner.Infrastructure.Transfer;

/// <summary>
/// System.Text.Json source generation for the transfer DTOs (ADR 0012). The camel-case names are the published
/// <c>docs/schemas/transfer-v1.schema.json</c> format; null members are omitted and read back as null.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    MaxDepth = TransferLimits.MaxJsonDepth,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TransferDocument))]
[JsonSerializable(typeof(List<Guid>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, TransferValue>))]
[JsonSerializable(typeof(TransferValue))]
[JsonSerializable(typeof(TransferPreserved))]
[JsonSerializable(typeof(List<TransferFieldOption>))]
internal sealed partial class TransferJsonContext : JsonSerializerContext;

/// <summary>Reads and writes the JSON envelope (B-12) with the T-10 limits.</summary>
internal static class TransferJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = TransferLimits.MaxJsonDepth };

    public static byte[] Serialize(TransferDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, TransferJsonContext.Default.TransferDocument);

    /// <summary>Parses a Max Planner transfer file. The format marker and version are checked before the body is bound.</summary>
    /// <exception cref="TransferFormatException">The content is not a supported transfer document.</exception>
    public static TransferDocument Deserialize(ReadOnlyMemory<byte> json)
    {
        try
        {
            using (var probe = JsonDocument.Parse(json, DocumentOptions))
            {
                var root = probe.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String
                    || format.GetString() != TransferFormat.Marker)
                {
                    throw new TransferFormatException(TransferFailure.Malformed, "format");
                }

                if (!root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var number) || number < 1)
                {
                    throw new TransferFormatException(TransferFailure.Malformed, "formatVersion");
                }

                if (number > TransferFormat.CurrentVersion)
                {
                    // B-12: a newer format is rejected with instructions (update the app), never read partially.
                    throw new TransferFormatException(TransferFailure.UnsupportedVersion, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            return JsonSerializer.Deserialize(json.Span, TransferJsonContext.Default.TransferDocument)
                ?? throw new TransferFormatException(TransferFailure.Malformed, "$");
        }
        catch (JsonException ex)
        {
            throw new TransferFormatException(TransferFailure.Malformed, ex.Path ?? "$", ex);
        }
    }

    public static string SerializeList(IReadOnlyList<Guid> ids) => JsonSerializer.Serialize([.. ids], TransferJsonContext.Default.ListGuid);

    public static string SerializeList(IReadOnlyList<string> values) => JsonSerializer.Serialize([.. values], TransferJsonContext.Default.ListString);

    public static string? SerializeCustomValues(IReadOnlyDictionary<string, TransferValue>? values) =>
        values is null || values.Count == 0 ? null : JsonSerializer.Serialize(values.ToDictionary(), TransferJsonContext.Default.DictionaryStringTransferValue);

    public static string? SerializePreserved(TransferPreserved? preserved) =>
        preserved is null ? null : JsonSerializer.Serialize(preserved, TransferJsonContext.Default.TransferPreserved);

    public static string? SerializeValue(TransferValue? value) =>
        value is null ? null : JsonSerializer.Serialize(value, TransferJsonContext.Default.TransferValue);

    public static string SerializeOptions(IReadOnlyList<TransferFieldOption> options) =>
        JsonSerializer.Serialize([.. options], TransferJsonContext.Default.ListTransferFieldOption);

    public static T? ParseCell<T>(string? text, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string location)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(text, type);
        }
        catch (JsonException ex)
        {
            throw new TransferFormatException(TransferFailure.Malformed, location, ex);
        }
    }
}

/// <summary>A transfer file could not be read or written; translated to <see cref="TransferFileError"/> at the port.</summary>
internal sealed class TransferFormatException : Exception
{
    public TransferFormatException(TransferFailure failure, string? detail, Exception? inner = null)
        : base($"Transfer file problem {failure} at {detail}.", inner)
    {
        Failure = failure;
        Detail = detail;
    }

    public TransferFormatException()
    {
    }

    public TransferFormatException(string message)
        : base(message)
    {
    }

    public TransferFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TransferFailure Failure { get; }

    public string? Detail { get; }
}
