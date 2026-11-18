namespace MaxPlanner.Application.Preferences;

/// <summary>The app appearance mode (T-08, AC-23). System follows Windows while the app runs.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum WindowStatePreference
{
    Normal,
    Maximized,
}

public enum CalendarModePreference
{
    Month,
    Week,
    Records,
}

/// <summary>A rectangle in physical desktop pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>The main window's restored (non-maximized) bounds and state.</summary>
/// <param name="Bounds">Physical desktop pixels of the restored window, even when it was closed maximized.</param>
/// <param name="MonitorWorkArea">
/// The work area of the display the window was on when saved. It is a hint only: when no current display
/// has this work area (monitor removed, resolution changed), the bounds are re-fitted to a visible display.
/// </param>
public sealed record WindowPlacementPreference(PixelRect Bounds, WindowStatePreference State, PixelRect? MonitorWorkArea);

public sealed record GridColumnPreference(string Key, double Width, bool IsVisible);

/// <summary>Reserved for the Grid page (Phase 2+): column order, widths, visibility, and sort.</summary>
public sealed record GridLayoutPreference(IReadOnlyList<GridColumnPreference> Columns, string? SortColumnKey, bool SortDescending);

/// <summary>Reserved for per-view filters (B-10): search text and selected values per field key.</summary>
public sealed record ViewFilterPreference(string? SearchText, IReadOnlyDictionary<string, IReadOnlyList<string>> Fields);

/// <summary>
/// UI preferences only (T-09). Business settings such as lookups, defaults, and fields live in SQLite and
/// never appear here. Every member is optional; a null value means "use the default".
/// </summary>
public sealed record UiPreferences
{
    /// <summary>The <c>configuration.json</c> format version this app reads and writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Page keys a saved <see cref="ActivePage"/> may name; anything else is discarded on load.</summary>
    public static IReadOnlyList<string> KnownPageKeys { get; } = ["dashboard", "board", "grid", "calendar", "checklists", "settings"];

    public static UiPreferences Default { get; } = new();

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public WindowPlacementPreference? Window { get; init; }

    public string? ActivePage { get; init; }

    public GridLayoutPreference? GridLayout { get; init; }

    /// <summary>Keyed by view (for example <c>board</c>, <c>grid</c>, <c>calendar</c>).</summary>
    public IReadOnlyDictionary<string, ViewFilterPreference>? ViewFilters { get; init; }

    public CalendarModePreference? CalendarMode { get; init; }

    public bool? ShowArchivedTasks { get; init; }

    /// <summary>Settings › Appearance › Show tags on Board cards (design §4.6); null means the default, on.</summary>
    public bool? ShowBoardCardTags { get; init; }

    /// <summary>
    /// Settings › Integrations › Azure DevOps (ADR 0031, design §4.6): per-PC values, kept by <see cref="RestoreDefaults"/>.
    /// Null means the defaults (integration off). The token is never here; it lives in Windows Credential Manager.
    /// </summary>
    public AzureDevOpsPreferences? AzureDevOps { get; init; }

    /// <summary>
    /// Restore default UI preferences (design §4.6): every UI preference goes back to its default, but the per-PC
    /// integration settings (<see cref="AzureDevOps"/>) are kept, because they are machine setup, not UI state.
    /// </summary>
    public UiPreferences RestoreDefaults() => Default with { AzureDevOps = AzureDevOps };
}

/// <summary>
/// The Azure DevOps pull request settings (ADR 0031, B-18.2). Per-PC preferences: not part of the dataset, export or
/// import. Text values are stored as typed (trimmed); null or blank means "not set" or "use the default".
/// </summary>
public sealed record AzureDevOpsPreferences
{
    public const string DefaultTargetBranch = "uat";

    public static AzureDevOpsPreferences Default { get; } = new();

    /// <summary>The integration switch; off by default. Off hides every pull request command.</summary>
    public bool Enabled { get; init; }

    /// <summary><c>https://server/collection</c>, without a project or <c>/_git/</c>.</summary>
    public string? CollectionUrl { get; init; }

    public string? Project { get; init; }

    public string? Repository { get; init; }

    /// <summary>Null or blank means <see cref="DefaultTargetBranch"/>.</summary>
    public string? DefaultTarget { get; init; }

    /// <summary>Null means on.</summary>
    public bool? AutoComplete { get; init; }

    /// <summary>Null or blank means the default title template.</summary>
    public string? TitleTemplate { get; init; }

    public string EffectiveDefaultTarget => string.IsNullOrWhiteSpace(DefaultTarget) ? DefaultTargetBranch : DefaultTarget.Trim();

    public bool EffectiveAutoComplete => AutoComplete ?? true;
}

public enum PreferencesRecovery
{
    /// <summary>The saved preferences were read (possibly with individual invalid values reset), or none existed yet.</summary>
    None,

    /// <summary><c>configuration.json</c> was unreadable; the last good copy was used instead.</summary>
    RestoredLastGood,

    /// <summary>Neither the file nor its last good copy was readable; defaults are used.</summary>
    ResetToDefaults,
}

/// <param name="Recovery">Reported to the user (AC-15); business data is never affected.</param>
public sealed record PreferencesLoadResult(UiPreferences Preferences, PreferencesRecovery Recovery);

/// <summary>
/// Reads and writes the UI preferences file (T-09). Broken preferences never block opening business data.
/// </summary>
public interface IUiPreferencesStore
{
    /// <summary>The preferences as last loaded or updated in memory.</summary>
    UiPreferences Current { get; }

    /// <summary>
    /// Loads and validates the saved preferences, falling back to the last good copy and then to defaults.
    /// Never throws for a missing, unreadable, or invalid file. Synchronous: call it before the UI starts
    /// or from a background thread.
    /// </summary>
    PreferencesLoadResult Load();

    /// <summary>
    /// Applies <paramref name="change"/> to <see cref="Current"/> and schedules a debounced background save.
    /// Safe to call from any thread, including the UI thread; it performs no I/O itself.
    /// </summary>
    void Update(Func<UiPreferences, UiPreferences> change);

    /// <summary>Writes any pending change now and waits for it. A write failure is logged, not thrown.</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}
