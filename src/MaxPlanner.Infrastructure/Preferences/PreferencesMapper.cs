using MaxPlanner.Application.Preferences;

namespace MaxPlanner.Infrastructure.Preferences;

/// <summary>Validates and clamps a loaded document into <see cref="UiPreferences"/>, and maps back for saving.</summary>
internal static class PreferencesMapper
{
    // Generous limits that only reject nonsense (overflowed or hand-edited values); fitting the window to
    // the current displays happens later, against the real work areas.
    private const int MaxCoordinate = 100_000;
    private const int MaxExtent = 65_535;
    private const double MinColumnWidth = 16;
    private const double MaxColumnWidth = 4_000;
    private const int MaxTextLength = 500;

    /// <param name="resetValues">How many present values were invalid and replaced by their default.</param>
    public static UiPreferences ToPreferences(PreferencesDocument document, out int resetValues)
    {
        var resets = 0;
        var preferences = new UiPreferences
        {
            Theme = ParseEnum<ThemePreference>(document.Theme, ref resets) ?? ThemePreference.System,
            Window = MapWindow(document.Window, ref resets),
            ActivePage = MapPage(document.ActivePage, ref resets),
            GridLayout = MapGridLayout(document.GridLayout),
            ViewFilters = MapViewFilters(document.ViewFilters),
            CalendarMode = ParseEnum<CalendarModePreference>(document.CalendarMode, ref resets),
            ShowArchivedTasks = document.ShowArchivedTasks,
        };
        resetValues = resets;
        return preferences;
    }

    public static PreferencesDocument ToDocument(UiPreferences preferences) => new()
    {
        ConfigurationVersion = UiPreferences.CurrentVersion,
        Theme = preferences.Theme.ToString(),
        Window = preferences.Window is { } window
            ? new WindowDocument { Bounds = ToRect(window.Bounds), State = window.State.ToString(), MonitorWorkArea = window.MonitorWorkArea is { } area ? ToRect(area) : null }
            : null,
        ActivePage = preferences.ActivePage,
        GridLayout = preferences.GridLayout is { } grid
            ? new GridLayoutDocument
            {
                Columns = [.. grid.Columns.Select(c => new GridColumnDocument { Key = c.Key, Width = c.Width, IsVisible = c.IsVisible })],
                SortColumnKey = grid.SortColumnKey,
                SortDescending = grid.SortDescending,
            }
            : null,
        ViewFilters = preferences.ViewFilters?.ToDictionary(
            pair => pair.Key,
            pair => (ViewFilterDocument?)new ViewFilterDocument
            {
                SearchText = pair.Value.SearchText,
                Fields = pair.Value.Fields.ToDictionary(f => f.Key, f => (List<string?>?)[.. f.Value], StringComparer.Ordinal),
            },
            StringComparer.Ordinal),
        CalendarMode = preferences.CalendarMode?.ToString(),
        ShowArchivedTasks = preferences.ShowArchivedTasks,
    };

    // Names only: Enum.TryParse would also accept numbers such as "7".
    private static TEnum? ParseEnum<TEnum>(string? value, ref int resets)
        where TEnum : struct, Enum
    {
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, out _) && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        resets++;
        return null;
    }

    private static WindowPlacementPreference? MapWindow(WindowDocument? window, ref int resets)
    {
        if (window is null)
        {
            return null;
        }

        if (MapRect(window.Bounds) is not { } bounds)
        {
            resets++;
            return null;
        }

        // A minimized or unknown state reopens as a normal window; the app never starts minimized.
        var state = ParseEnum<WindowStatePreference>(window.State, ref resets) ?? WindowStatePreference.Normal;
        var monitor = MapRect(window.MonitorWorkArea);
        if (window.MonitorWorkArea is not null && monitor is null)
        {
            resets++;
        }

        return new WindowPlacementPreference(bounds, state, monitor);
    }

    private static PixelRect? MapRect(RectDocument? rect) =>
        rect is not null
        && Math.Abs(rect.X) <= MaxCoordinate && Math.Abs(rect.Y) <= MaxCoordinate
        && rect.Width is > 0 and <= MaxExtent && rect.Height is > 0 and <= MaxExtent
            ? new PixelRect(rect.X, rect.Y, rect.Width, rect.Height)
            : null;

    private static string? MapPage(string? page, ref int resets)
    {
        if (page is null)
        {
            return null;
        }

        var key = page.Trim().ToLowerInvariant();
        if (UiPreferences.KnownPageKeys.Contains(key))
        {
            return key;
        }

        resets++;
        return null;
    }

    private static GridLayoutPreference? MapGridLayout(GridLayoutDocument? grid)
    {
        if (grid is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var columns = new List<GridColumnPreference>();
        foreach (var column in grid.Columns ?? [])
        {
            if (column is { Key: { } key, Width: { } width } && !string.IsNullOrWhiteSpace(key) && double.IsFinite(width) && seen.Add(key.Trim()))
            {
                columns.Add(new GridColumnPreference(key.Trim(), Math.Clamp(width, MinColumnWidth, MaxColumnWidth), column.IsVisible ?? true));
            }
        }

        var sortKey = string.IsNullOrWhiteSpace(grid.SortColumnKey) ? null : grid.SortColumnKey.Trim();
        return new GridLayoutPreference(columns, sortKey, grid.SortDescending ?? false);
    }

    private static Dictionary<string, ViewFilterPreference>? MapViewFilters(Dictionary<string, ViewFilterDocument?>? filters)
    {
        if (filters is null)
        {
            return null;
        }

        var result = new Dictionary<string, ViewFilterPreference>(StringComparer.Ordinal);
        foreach (var (view, filter) in filters)
        {
            if (string.IsNullOrWhiteSpace(view) || filter is null)
            {
                continue;
            }

            var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (field, values) in filter.Fields ?? [])
            {
                if (!string.IsNullOrWhiteSpace(field) && values is not null)
                {
                    fields[field] = [.. values.OfType<string>()];
                }
            }

            var search = filter.SearchText is { Length: > 0 } text ? text[..Math.Min(text.Length, MaxTextLength)] : null;
            result[view] = new ViewFilterPreference(search, fields);
        }

        return result;
    }

    private static RectDocument ToRect(PixelRect rect) => new() { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };
}
