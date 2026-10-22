using System.Text.Json.Serialization;

namespace GridGate;

/// <summary>Per-column layout as it would be persisted to configuration.json (B-07: independent of data).</summary>
public sealed record ColumnLayout(string Key, int DisplayIndex, double Width, bool IsVisible, string? Sort);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<ColumnLayout>))]
internal sealed partial class LayoutJsonContext : JsonSerializerContext;
