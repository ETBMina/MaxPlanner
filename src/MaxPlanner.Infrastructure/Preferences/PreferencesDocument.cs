using System.Text.Json.Serialization;

namespace MaxPlanner.Infrastructure.Preferences;

// The on-disk shape of configuration.json. Every member is nullable and enums are strings, so an unknown
// or missing value is reset to its default during mapping instead of making the whole file unreadable.

internal sealed class PreferencesDocument
{
    public int? ConfigurationVersion { get; set; }

    public string? Theme { get; set; }

    public WindowDocument? Window { get; set; }

    public string? ActivePage { get; set; }

    public GridLayoutDocument? GridLayout { get; set; }

    public Dictionary<string, ViewFilterDocument?>? ViewFilters { get; set; }

    public string? CalendarMode { get; set; }

    public bool? ShowArchivedTasks { get; set; }
}

internal sealed class RectDocument
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

internal sealed class WindowDocument
{
    public RectDocument? Bounds { get; set; }

    public string? State { get; set; }

    public RectDocument? MonitorWorkArea { get; set; }
}

internal sealed class GridLayoutDocument
{
    public List<GridColumnDocument?>? Columns { get; set; }

    public string? SortColumnKey { get; set; }

    public bool? SortDescending { get; set; }
}

internal sealed class GridColumnDocument
{
    public string? Key { get; set; }

    public double? Width { get; set; }

    public bool? IsVisible { get; set; }
}

internal sealed class ViewFilterDocument
{
    public string? SearchText { get; set; }

    public Dictionary<string, List<string?>?>? Fields { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PreferencesDocument))]
internal sealed partial class PreferencesJsonContext : JsonSerializerContext;
