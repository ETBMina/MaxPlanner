using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.Dashboard;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Tests.Testing;
using ViewState = MaxPlanner.Presentation.Workspace.ViewState;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// The Period field on the Dashboard and in the Filters flyout: start date in the period, rolling presets that move
/// with the local day, a custom inclusive range with the inverted-range error, chips, and drill-downs that keep it.
/// </summary>
[Trait("Req", "B-10")]
public sealed class PeriodFilterViewModelTests
{
    private const int AllTime = 0;
    private const int Last7Days = 1;
    private const int Custom = 3;
    private static readonly DateOnly Today = Records.Today;

    [Fact]
    [Trait("Req", "B-15")]
    public void The_dashboard_period_narrows_the_counts_and_clear_filters_resets_it()
    {
        using var h = Seed();
        var dashboard = OpenDashboard(h);
        dashboard.ActiveTasks.Value.Should().Be("3");

        dashboard.Period.SelectedIndex = Last7Days;
        h.Settle();

        dashboard.ActiveTasks.Value.Should().Be("1");
        dashboard.IsFiltered.Should().BeTrue();
        dashboard.Period.IsCustom.Should().BeFalse();
        h.Preferences.Current.ViewFilters!["dashboard"].Fields[FilterPersistence.PeriodKey].Should().Equal("last7");

        dashboard.ClearFiltersCommand.Execute(null);
        h.Settle();

        dashboard.Period.SelectedIndex.Should().Be(AllTime);
        dashboard.ActiveTasks.Value.Should().Be("3");
        dashboard.IsFiltered.Should().BeFalse();
        h.Preferences.Current.ViewFilters!["dashboard"].Fields.Should().NotContainKey(FilterPersistence.PeriodKey);
    }

    [Fact]
    [Trait("Req", "B-15")]
    public void A_custom_dashboard_range_applies_when_valid_and_an_inverted_one_keeps_the_last_valid_range()
    {
        using var h = Seed();
        var dashboard = OpenDashboard(h);

        dashboard.Period.SelectedIndex = Custom;
        dashboard.Period.IsCustom.Should().BeTrue();
        dashboard.Period.From = Picker(Today.AddDays(-10));
        dashboard.Period.To = Picker(Today.AddDays(-9));
        h.Settle();
        dashboard.ActiveTasks.Value.Should().Be("1", "only the task that started 10 days ago");

        dashboard.Period.From = Picker(Today);
        h.Settle();

        dashboard.Period.HasError.Should().BeTrue();
        dashboard.Period.Error.Should().Be("Filters_InvertedRange");
        dashboard.ActiveTasks.Value.Should().Be("1", "an inverted range is never applied");
    }

    [Fact]
    [Trait("Req", "B-15")]
    public void A_saved_dashboard_period_is_restored_and_a_drill_down_carries_it_into_the_grid()
    {
        var saved = new ViewFilterPreference(null, new Dictionary<string, IReadOnlyList<string>> { [FilterPersistence.PeriodKey] = ["last7"] });
        using var h = Seed(UiPreferences.Default with { ViewFilters = new Dictionary<string, ViewFilterPreference> { ["dashboard"] = saved } });
        var dashboard = OpenDashboard(h);

        dashboard.Period.SelectedIndex.Should().Be(Last7Days);
        dashboard.ActiveTasks.Value.Should().Be("1");

        dashboard.ActiveTasks.DrillDownCommand.Execute(null);
        h.Settle();
        var grid = OpenGrid(h);

        var filters = h.Workspace.Context.TaskFilters;
        filters.Spec.Period.Should().Be(PeriodFilter.Last7Days);
        filters.Chips.Select(c => c.Key).Should().Contain("Period");
        grid.Rows.Count(r => r.IsTask).Should().Be(1, "the drill-down shows exactly what the card counts");
    }

    [Fact]
    public void The_flyout_period_is_a_draft_field_with_a_chip_and_a_badge_count()
    {
        using var h = Seed();
        var grid = OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;

        var draft = filters.CreateDraft();
        draft.Period.Should().NotBeNull();
        draft.Period!.SelectedIndex = Last7Days;
        draft.HeaderText.Should().Be("Filters_HeaderOne", "the draft counts one field set");
        filters.Spec.Period.Should().BeNull("nothing changes until Apply");

        filters.Apply(draft);
        h.Settle();

        filters.Spec.Period.Should().Be(PeriodFilter.Last7Days);
        filters.ActiveFieldCount.Should().Be(1);
        filters.Chips.Single(c => c.Key == "Period").Label.Should().Be("Filters_ChipLabel");
        grid.Rows.Count(r => r.IsTask).Should().Be(1);
        h.Preferences.Current.ViewFilters!["tasks"].Fields[FilterPersistence.PeriodKey].Should().Equal("last7");

        var cleared = filters.CreateDraft();
        cleared.Period!.SelectedIndex.Should().Be(Last7Days, "a new draft starts from the applied filter");
        cleared.ClearCommand.Execute(null);
        cleared.Period.SelectedIndex.Should().Be(AllTime);
        filters.Spec.Period.Should().Be(PeriodFilter.Last7Days, "Clear changes the draft only");

        filters.Chips.Single(c => c.Key == "Period").Remove();
        h.Settle();
        filters.Spec.Period.Should().BeNull();
        grid.Rows.Count(r => r.IsTask).Should().Be(3);
    }

    [Fact]
    public void A_reopened_flyout_keeps_the_applied_period_when_the_combo_box_reports_no_selection()
    {
        using var h = Seed();
        var filters = h.Workspace.Context.TaskFilters;
        ApplyLast7Days(h);

        // Reopening the flyout: a ComboBox whose items are being replaced reports SelectedIndex -1 through TwoWay.
        var draft = filters.CreateDraft();
        draft.Period!.SelectedIndex = -1;

        draft.Period.SelectedIndex.Should().Be(Last7Days, "an out-of-range index is ignored");
        draft.Period.Filter.Should().Be(PeriodFilter.Last7Days);
        draft.HeaderText.Should().Be("Filters_HeaderOne", "the draft still counts the period");
        draft.Period.SelectedIndex = 4;
        draft.Period.Filter.Should().Be(PeriodFilter.Last7Days);

        filters.Apply(draft);
        filters.Spec.Period.Should().Be(PeriodFilter.Last7Days, "applying the reopened draft keeps the period");
    }

    [Fact]
    [Trait("Req", "B-15")]
    public void The_dashboard_period_ignores_a_no_selection_write_and_keeps_the_saved_period()
    {
        using var h = Seed();
        var dashboard = OpenDashboard(h);
        dashboard.Period.SelectedIndex = Last7Days;
        h.Settle();

        dashboard.Period.SelectedIndex = -1;
        h.Settle();

        dashboard.Period.SelectedIndex.Should().Be(Last7Days);
        dashboard.ActiveTasks.Value.Should().Be("1");
        h.Preferences.Current.ViewFilters!["dashboard"].Fields[FilterPersistence.PeriodKey].Should().Equal("last7");
    }

    [Fact]
    public void An_inverted_custom_period_blocks_apply()
    {
        using var h = Seed();
        var draft = h.Workspace.Context.TaskFilters.CreateDraft();

        draft.Period!.SelectedIndex = Custom;
        draft.Period.From = Picker(Today);
        draft.Period.To = Picker(Today.AddDays(-1));

        draft.Period.HasError.Should().BeTrue();
        draft.CanApply.Should().BeFalse();

        draft.Period.To = null;
        draft.CanApply.Should().BeTrue("an open-ended range is valid");
        draft.Period.Filter.Should().Be(PeriodFilter.Custom(Today, null));
    }

    [Fact]
    public void The_calendar_scope_has_the_period_field_too()
    {
        using var h = Seed();
        var filters = h.Workspace.Context.CrFilters;
        var draft = filters.CreateDraft();

        draft.Period!.SelectedIndex = Custom;
        draft.Period.From = Picker(new DateOnly(2026, 11, 1));
        draft.Period.To = Picker(new DateOnly(2026, 11, 7));
        filters.Apply(draft);

        filters.Spec.Period.Should().Be(PeriodFilter.Custom(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7)));
        filters.ActiveFieldCount.Should().Be(1);
        h.Preferences.Current.ViewFilters!["calendar"].Fields[FilterPersistence.PeriodKey].Should().Equal("custom", "2026-11-01", "2026-11-07");
    }

    [Fact]
    [Trait("Req", "AC-21")]
    public void A_rolling_period_moves_with_the_local_day()
    {
        using var h = Seed();
        var grid = OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        var draft = filters.CreateDraft();
        draft.Period!.SelectedIndex = Last7Days;
        filters.Apply(draft);
        h.Settle();
        grid.Rows.Count(r => r.IsTask).Should().Be(1);

        h.Days.Raise(Today.AddDays(5));
        h.PumpUntil(() => h.Store.Today == Today.AddDays(5));
        h.Settle();

        grid.Rows.Count(r => r.IsTask).Should().Be(0, "the task that started 3 days ago is now 8 days old");
    }

    [Fact]
    [Trait("Req", "AC-21")]
    public void A_new_day_that_arrives_with_a_snapshot_still_moves_a_rolling_period()
    {
        using var h = Seed();
        var grid = OpenGrid(h);
        ApplyLast7Days(h);
        grid.Rows.Count(r => r.IsTask).Should().Be(1);

        // A commit that requires a full reload (as an import does) makes the store resync from a snapshot read on the
        // new day. No day tick is raised, so only the snapshot commit carries the new day.
        h.Clock.UtcNow = Records.Now.AddDays(5);
        h.Await(h.Coordinator.ExecuteAsync(
            OperationId.New(),
            "test.full-reload",
            tx =>
            {
                tx.Changes.RequireFullReload();
                return TxResult.Commit(0L);
            },
            CancellationToken.None));
        h.PumpUntil(() => h.Store.Today == Today.AddDays(5));
        h.Settle();

        grid.Rows.Count(r => r.IsTask).Should().Be(0);
    }

    [Fact]
    [Trait("Req", "AC-21")]
    public void The_board_follows_a_rolling_period_across_midnight()
    {
        using var h = Seed();
        var board = h.Workspace.Board;
        h.PumpUntil(() => board.Buckets.Count > 0);
        h.Settle();
        ApplyLast7Days(h);
        board.Buckets.Sum(b => b.Cards.Count).Should().Be(1);

        h.Days.Raise(Today.AddDays(5));
        h.PumpUntil(() => h.Store.Today == Today.AddDays(5));
        h.Settle();

        board.Buckets.Sum(b => b.Cards.Count).Should().Be(0);
    }

    [Fact]
    [Trait("Req", "B-15")]
    public void Clear_filters_is_available_while_the_period_box_shows_an_unapplied_custom_range()
    {
        using var h = Seed();
        var dashboard = OpenDashboard(h);
        dashboard.ClearFiltersCommand.CanExecute(null).Should().BeFalse();

        dashboard.Period.SelectedIndex = Custom;
        h.Settle();

        dashboard.IsFiltered.Should().BeFalse("a custom range without dates narrows nothing");
        dashboard.ClearFiltersCommand.CanExecute(null).Should().BeTrue();
        dashboard.ClearFiltersCommand.Execute(null);
        dashboard.Period.SelectedIndex.Should().Be(AllTime);
        dashboard.ClearFiltersCommand.CanExecute(null).Should().BeFalse();
    }

    private static void ApplyLast7Days(WorkspaceHarness h)
    {
        var filters = h.Workspace.Context.TaskFilters;
        var draft = filters.CreateDraft();
        draft.Period!.SelectedIndex = Last7Days;
        filters.Apply(draft);
        h.Settle();
    }

    /// <summary>Three tasks: started 3 days ago, started 10 days ago, and undated.</summary>
    private static WorkspaceHarness Seed(UiPreferences? preferences = null) =>
        WorkspaceHarness.Create(
            async s =>
            {
                await s.DatedTaskAsync("Recent", Today.AddDays(-3));
                await s.DatedTaskAsync("Older", Today.AddDays(-10));
                await s.TaskAsync("Undated");
            },
            preferences);

    private static DashboardPageViewModel OpenDashboard(WorkspaceHarness h)
    {
        var dashboard = h.Workspace.Dashboard;
        h.PumpUntil(() => dashboard.State != ViewState.Loading);
        h.Settle();
        return dashboard;
    }

    private static GridPageViewModel OpenGrid(WorkspaceHarness h)
    {
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.State != ViewState.Loading);
        h.Settle();
        return grid;
    }

    private static DateTimeOffset Picker(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue));
}
