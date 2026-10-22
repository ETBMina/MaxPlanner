using AwesomeAssertions;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Infrastructure.Preferences;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Extensions.Time.Testing;

namespace MaxPlanner.Infrastructure.Tests.Preferences;

[Trait("Req", "T-09")]
public sealed class PreferencesStoreTests : IDisposable
{
    private static readonly UiPreferences Saved = new()
    {
        Theme = ThemePreference.Dark,
        Window = new WindowPlacementPreference(new PixelRect(-1200, 40, 1100, 760), WindowStatePreference.Maximized, new PixelRect(-1920, 0, 1920, 1040)),
        ActivePage = "grid",
        GridLayout = new GridLayoutPreference([new GridColumnPreference("title", 320, true), new GridColumnPreference("owner", 140, false)], "created", true),
        ViewFilters = new Dictionary<string, ViewFilterPreference>
        {
            ["board"] = new("card", new Dictionary<string, IReadOnlyList<string>> { ["squad"] = ["Retail Cards", "General"] }),
        },
        CalendarMode = CalendarModePreference.Week,
        ShowArchivedTasks = true,
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "MaxPlanner Tëst Prefs", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    public PreferencesStoreTests() => Paths = DataPaths.ForDirectory(_root);

    private DataPaths Paths { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Preferences_round_trip_through_a_unicode_path()
    {
        using (var store = NewStore())
        {
            store.Load().Recovery.Should().Be(PreferencesRecovery.None);
            store.Update(_ => Saved);
            await store.FlushAsync(Ct);
        }

        using var reopened = NewStore();
        var loaded = reopened.Load();

        loaded.Recovery.Should().Be(PreferencesRecovery.None);
        var preferences = loaded.Preferences;
        preferences.Theme.Should().Be(ThemePreference.Dark);
        preferences.Window.Should().Be(Saved.Window);
        preferences.ActivePage.Should().Be("grid");
        preferences.GridLayout!.Columns.Should().Equal(Saved.GridLayout!.Columns);
        preferences.GridLayout.SortColumnKey.Should().Be("created");
        preferences.GridLayout.SortDescending.Should().BeTrue();
        preferences.ViewFilters!["board"].SearchText.Should().Be("card");
        preferences.ViewFilters["board"].Fields["squad"].Should().Equal("Retail Cards", "General");
        preferences.CalendarMode.Should().Be(CalendarModePreference.Week);
        preferences.ShowArchivedTasks.Should().BeTrue();
        File.ReadAllText(Paths.ConfigurationFile).Should().Contain("\"configurationVersion\": 1");
    }

    [Fact]
    public void A_missing_file_is_a_first_run_with_defaults_and_nothing_to_report()
    {
        using var store = NewStore();

        var loaded = store.Load();

        loaded.Should().Be(new PreferencesLoadResult(UiPreferences.Default, PreferencesRecovery.None));
        File.Exists(Paths.ConfigurationFile).Should().BeFalse("loading never writes");
    }

    [Fact]
    public async Task Saving_replaces_the_file_atomically_and_keeps_the_previous_one_as_last_good()
    {
        using var store = NewStore();
        store.Load();
        store.Update(p => p with { Theme = ThemePreference.Light });
        await store.FlushAsync(Ct);
        File.Exists(Paths.LastGoodConfigurationFile).Should().BeFalse("the first save has nothing to keep");

        store.Update(p => p with { Theme = ThemePreference.Dark });
        await store.FlushAsync(Ct);

        File.ReadAllText(Paths.ConfigurationFile).Should().Contain("\"theme\": \"Dark\"");
        File.ReadAllText(Paths.LastGoodConfigurationFile).Should().Contain("\"theme\": \"Light\"");
        File.Exists(Paths.ConfigurationFile + ".tmp").Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task An_unreadable_file_falls_back_to_the_last_good_copy()
    {
        await SaveTwiceAsync(ThemePreference.Light, ThemePreference.Dark);
        File.WriteAllText(Paths.ConfigurationFile, "{ \"configurationVersion\": 1, \"theme\": ");

        using var store = NewStore();
        var loaded = store.Load();

        loaded.Recovery.Should().Be(PreferencesRecovery.RestoredLastGood);
        loaded.Preferences.Theme.Should().Be(ThemePreference.Light);
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task When_both_copies_are_unreadable_the_defaults_are_used_and_reported()
    {
        await SaveTwiceAsync(ThemePreference.Light, ThemePreference.Dark);
        File.WriteAllText(Paths.ConfigurationFile, "not json");
        File.WriteAllBytes(Paths.LastGoodConfigurationFile, [0xFF, 0xFE, 0x00]);

        using var store = NewStore();
        var loaded = store.Load();

        loaded.Should().Be(new PreferencesLoadResult(UiPreferences.Default, PreferencesRecovery.ResetToDefaults));
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task Saving_after_an_unreadable_file_never_turns_it_into_the_last_good_copy()
    {
        await SaveTwiceAsync(ThemePreference.Light, ThemePreference.Dark);
        File.WriteAllText(Paths.ConfigurationFile, "garbage");
        using var store = NewStore();
        store.Load();

        store.Update(p => p with { ActivePage = "board" });
        await store.FlushAsync(Ct);

        File.ReadAllText(Paths.LastGoodConfigurationFile).Should().Contain("\"theme\": \"Light\"");
        File.ReadAllText(Path.Combine(Paths.DataDirectory, "configuration.unreadable.json")).Should().Be("garbage");
        File.ReadAllText(Paths.ConfigurationFile).Should().Contain("\"activePage\": \"board\"");
    }

    [Fact]
    public void A_file_from_a_newer_configuration_version_is_not_trusted()
    {
        Directory.CreateDirectory(Paths.DataDirectory);
        File.WriteAllText(Paths.ConfigurationFile, """{ "configurationVersion": 2, "theme": "Dark" }""");

        using var store = NewStore();

        store.Load().Should().Be(new PreferencesLoadResult(UiPreferences.Default, PreferencesRecovery.ResetToDefaults));
    }

    [Fact]
    public void Invalid_and_unknown_values_are_reset_individually_without_rejecting_the_file()
    {
        Directory.CreateDirectory(Paths.DataDirectory);
        File.WriteAllText(Paths.ConfigurationFile, """
            {
              // Hand-edited: comments and trailing commas are tolerated.
              "configurationVersion": 1,
              "theme": "Purple",
              "window": { "bounds": { "x": 10, "y": 10, "width": -5, "height": 700 }, "state": "Normal" },
              "activePage": "Nowhere",
              "calendarMode": "1",
              "gridLayout": { "columns": [ { "key": "", "width": 100 }, { "key": "title", "width": 99999 }, { "key": "title", "width": 50 }, null ] },
              "showArchivedTasks": false,
              "someFutureSetting": { "x": 1 },
            }
            """);

        using var store = NewStore();
        var loaded = store.Load();

        loaded.Recovery.Should().Be(PreferencesRecovery.None);
        var preferences = loaded.Preferences;
        preferences.Theme.Should().Be(ThemePreference.System);
        preferences.Window.Should().BeNull();
        preferences.ActivePage.Should().BeNull();
        preferences.CalendarMode.Should().BeNull("numbers are not accepted as enum names");
        preferences.GridLayout!.Columns.Should().Equal(new GridColumnPreference("title", 4000, true));
        preferences.ShowArchivedTasks.Should().BeFalse();
    }

    [Fact]
    public void A_minimized_window_state_reopens_as_a_normal_window()
    {
        Directory.CreateDirectory(Paths.DataDirectory);
        File.WriteAllText(Paths.ConfigurationFile, """
            { "configurationVersion": 1, "window": { "bounds": { "x": 0, "y": 0, "width": 800, "height": 600 }, "state": "Minimized" } }
            """);

        using var store = NewStore();

        store.Load().Preferences.Window!.State.Should().Be(WindowStatePreference.Normal);
    }

    [Fact]
    public void Updates_are_written_once_after_the_debounce_interval()
    {
        using var store = NewStore();
        store.Load();

        store.Update(p => p with { Theme = ThemePreference.Dark });
        _time.Advance(TimeSpan.FromMilliseconds(400));
        store.Update(p => p with { ActivePage = "calendar" });
        _time.Advance(TimeSpan.FromMilliseconds(499));
        File.Exists(Paths.ConfigurationFile).Should().BeFalse("each update restarts the 500 ms debounce");

        _time.Advance(TimeSpan.FromMilliseconds(1));

        var written = File.ReadAllText(Paths.ConfigurationFile);
        written.Should().Contain("\"theme\": \"Dark\"").And.Contain("\"activePage\": \"calendar\"");
    }

    [Fact]
    public async Task An_update_that_changes_nothing_writes_nothing()
    {
        using var store = NewStore();
        store.Load();

        store.Update(p => p with { Theme = ThemePreference.System });
        await store.FlushAsync(Ct);

        File.Exists(Paths.ConfigurationFile).Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Some tests never create the folder.
        }
    }

    private JsonUiPreferencesStore NewStore() => new(Paths, _time);

    private async Task SaveTwiceAsync(ThemePreference first, ThemePreference second)
    {
        using var store = NewStore();
        store.Load();
        store.Update(p => p with { Theme = first });
        await store.FlushAsync(Ct);
        store.Update(p => p with { Theme = second });
        await store.FlushAsync(Ct);
    }
}
