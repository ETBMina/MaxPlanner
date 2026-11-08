using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.Common.Filters;
using MaxPlanner.Presentation.Dashboard;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Tests.Testing;
using ViewState = MaxPlanner.Presentation.Workspace.ViewState;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// The Filter by menu (design §2.6, owner decision 2026-11-07): every change applies to the scope at once and is
/// persisted, Clear all clears every field, date rows offer rolling presets and a Customised range, and Active date
/// (was "Period") keeps the records active during the range, on the Dashboard as well.
/// </summary>
[Trait("Req", "B-10")]
public sealed class FilterMenuViewModelTests
{
    private const int AllTime = 0;
    private const int Last7Days = 1;
    private const int Custom = 3;
    private static readonly DateOnly Today = Records.Today;

    [Fact]
    public void The_task_menu_has_the_rows_in_design_order_and_the_calendar_menu_its_own()
    {
        using var h = Seed();
        OpenGrid(h);
        var crFilters = h.Workspace.Context.CrFilters;
        h.Settle();

        h.Workspace.Context.TaskFilters.CreateMenu().Rows.Select(r => r.Key).Should().StartWith(
            ["Priority", "Status", "StartDate", "EndDate", "ActiveDate", "Squad", "Owner", "Designers", "Developers", "Tags", "Cr", "Service"]);
        crFilters.CreateMenu().Rows.Select(r => r.Key).Should().StartWith(
            ["Priority", "Status", "Planned", "ActiveDate", "Squad", "Owner"]);
        h.Workspace.Context.TaskFilters.CreateMenu().Rows.Select(r => r.AutomationId).Should().Contain("Filters_Row_ActiveDate");
    }

    [Fact]
    public void Checking_an_item_applies_at_once_with_a_chip_a_badge_and_the_saved_preference()
    {
        using var h = Seed();
        var grid = OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        var menu = filters.CreateMenu();
        var status = menu.Find<FilterChoiceGroupViewModel>("Status")!;
        var first = status.Choices[0];

        first.IsChecked = true;
        h.Settle();

        filters.Spec.Statuses.Should().ContainSingle().Which.Value.Should().Be(first.Id, "there is no Apply step");
        filters.ActiveFieldCount.Should().Be(1);
        filters.Chips.Select(c => c.Key).Should().Contain("Status");
        h.Preferences.Current.ViewFilters!["tasks"].Fields.Should().ContainKey("status");
        menu.Rows.Single(r => r.Key == "Status").IsSet.Should().BeTrue();
        menu.CanClearAll.Should().BeTrue();
        first.AutomationId.Should().StartWith("Filters_Status_");

        first.IsChecked = false;
        h.Settle();

        filters.Spec.Statuses.Should().BeEmpty();
        grid.Rows.Count(r => r.IsTask).Should().Be(3);
    }

    [Fact]
    public void Clear_all_clears_every_field_with_one_commit_and_keeps_the_search()
    {
        using var h = Seed();
        OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        filters.SearchText = "Recent";
        var menu = filters.CreateMenu();
        menu.Find<FilterChoiceGroupViewModel>("Status")!.Choices[0].IsChecked = true;
        menu.Find<ActiveDateFieldViewModel>("ActiveDate")!.Last7Days.IsChecked = true;
        menu.Find<FilterDateFieldViewModel>("StartDate")!.Presets[0].IsChecked = true;
        filters.ActiveFieldCount.Should().Be(3);
        var changes = 0;
        filters.Changed += (_, _) => changes++;

        menu.ClearAllCommand.Execute(null);
        h.Settle();

        changes.Should().Be(1);
        filters.ActiveFieldCount.Should().Be(0);
        filters.Spec.SearchText.Should().Be("Recent", "the search box is outside the menu");
        menu.CanClearAll.Should().BeFalse();
        menu.Rows.Should().OnlyContain(r => !r.IsSet);
    }

    [Fact]
    public void A_contains_box_applies_on_commit_not_on_every_keystroke()
    {
        using var h = Seed();
        OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        var service = filters.CreateMenu().Find<FilterTextFieldViewModel>("Service")!;

        service.Text = "Mob";
        filters.Spec.ServiceContains.Should().BeNull();

        service.Commit();
        filters.Spec.ServiceContains.Should().Be("Mob");
        service.AutomationId.Should().Be("Filters_Service_ContainsBox");
    }

    [Fact]
    public void The_cr_row_is_any_cr_no_cr_or_a_found_cr()
    {
        using var h = Seed();
        OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        var cr = filters.CreateMenu().Find<FilterCrFieldViewModel>("Cr")!;
        cr.AnyChoice.IsChecked.Should().BeTrue();

        cr.NoneChoice.IsChecked = true;
        filters.Spec.Cr.Should().Be(CrLinkFilter.NoCr);
        cr.AnyChoice.IsChecked.Should().BeFalse();

        cr.AnyChoice.IsChecked = true;
        filters.Spec.Cr.Should().Be(CrLinkFilter.Any);
        cr.NoneChoice.IsChecked.Should().BeFalse();

        cr.AnyChoice.IsChecked = false;
        cr.AnyChoice.IsChecked.Should().BeTrue("Any CR is what no CR filter looks like");
    }

    [Fact]
    public void Enter_in_an_empty_cr_box_changes_nothing()
    {
        using var h = Seed(crs: true);
        OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        var cr = filters.CreateMenu().Find<FilterCrFieldViewModel>("Cr")!;
        cr.UpdateSuggestions(string.Empty);
        cr.Suggestions.Should().NotBeEmpty();

        cr.Submit(string.Empty, null);
        cr.Submit("   ", null);
        filters.Spec.Cr.Should().Be(CrLinkFilter.Any, "empty text with no chosen suggestion is not a search");

        cr.Submit("CR-2", null);
        filters.Spec.Cr.Mode.Should().Be(CrLinkMode.Specific);
        cr.Text.Should().StartWith("CR-2");
    }

    [Fact]
    public void A_change_from_outside_the_open_menu_detaches_it_so_it_cannot_write_old_fields_back()
    {
        using var h = Seed();
        OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        var menu = filters.CreateMenu();
        var status = menu.Find<FilterChoiceGroupViewModel>("Status")!;
        status.Choices[0].IsChecked = true;
        menu.IsDetached.Should().BeFalse("its own changes keep it current");
        var detached = 0;
        menu.Detached += (_, _) => detached++;

        filters.ClearAll();
        menu.IsDetached.Should().BeTrue("Clear all filters (Ctrl+Shift+L) changed the scope from outside");
        detached.Should().Be(1, "the open menu closes");

        menu.Find<FilterChoiceGroupViewModel>("Priority")!.Choices[0].IsChecked = true;
        filters.Spec.Statuses.Should().BeEmpty("the stale menu cannot restore the cleared status");
        filters.Spec.Priorities.Should().BeEmpty();

        var reopened = filters.CreateMenu();
        reopened.Find<FilterChoiceGroupViewModel>("Priority")!.Choices[0].IsChecked = true;
        filters.Spec.Priorities.Should().ContainSingle();
        filters.Chips.Single(c => c.Key == "Priority").Remove();
        reopened.IsDetached.Should().BeTrue("removing a chip also changes the scope from outside");

        var closed = filters.CreateMenu();
        filters.ReleaseMenu(closed);
        closed.Find<FilterChoiceGroupViewModel>("Status")!.Choices[0].IsChecked = true;
        filters.Spec.Statuses.Should().BeEmpty("a closed menu is released and its late changes are ignored");
    }

    [Fact]
    public void Custom_field_filters_without_a_menu_row_are_kept()
    {
        // A menu opened before the workflow arrived has no custom field rows; its changes must not drop those filters.
        var field = new MaxPlanner.Domain.Identifiers.FieldId(Guid.NewGuid());
        var saved = new CustomFieldFilter(field, "abc", null, null, []);
        var menu = new FilterMenuViewModel(new FakeStrings(), _ => { });
        menu.EndBuild();

        MenuFields.ReadCustom(menu, [saved]).Should().Equal(saved);
    }

    [Fact]
    public void Date_presets_apply_at_once_and_are_saved_as_presets()
    {
        using var h = Seed();
        var grid = OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        var start = filters.CreateMenu().Find<FilterDateFieldViewModel>("StartDate")!;
        start.Presets.Select(p => p.AutomationId).Should().Equal(
            "Filters_StartDate_Past", "Filters_StartDate_Today", "Filters_StartDate_Tomorrow", "Filters_StartDate_ThisWeek",
            "Filters_StartDate_NextWeek", "Filters_StartDate_Future", "Filters_StartDate_NoDate");

        start.Presets.Single(p => p.AutomationId.EndsWith("_Past", StringComparison.Ordinal)).IsChecked = true;
        h.Settle();

        filters.Spec.StartDate.Should().Be(new DateRangeFilter(null, null, false, DatePresets.Past));
        grid.Rows.Count(r => r.IsTask).Should().Be(2, "the two dated tasks started before today");
        h.Preferences.Current.ViewFilters!["tasks"].Fields["start"].Should().Equal(string.Empty, string.Empty, string.Empty, "past");

        start.Presets.Single(p => p.AutomationId.EndsWith("_NoDate", StringComparison.Ordinal)).IsChecked = true;
        h.Settle();

        grid.Rows.Count(r => r.IsTask).Should().Be(3, "No date adds the undated task to the union");
        filters.Chips.Single(c => c.Key == "StartDate").Label.Should().Be("Filters_ChipLabel");
    }

    [Fact]
    public void The_customised_range_takes_two_picks_in_either_order_and_can_be_cleared()
    {
        using var h = Seed();
        var grid = OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        var start = filters.CreateMenu().Find<FilterDateFieldViewModel>("StartDate")!;
        var range = start.Range;
        range.Months.Should().HaveCount(2);
        range.Months[0].Days.Should().HaveCount(42);
        range.Months[0].Days[0].Date.DayOfWeek.Should().Be(DayOfWeek.Sunday, "weeks start on Sunday");
        range.Months[0].Days.Single(d => d.IsToday).Date.Should().Be(Today);

        range.Pick(Today.AddDays(-3));
        filters.Spec.StartDate.Should().Be(new DateRangeFilter(Today.AddDays(-3), Today.AddDays(-3), false), "the first pick is a one-day range");
        start.IsCustom.Should().BeTrue();

        range.Pick(Today.AddDays(-12));
        h.Settle();

        filters.Spec.StartDate.Should().Be(new DateRangeFilter(Today.AddDays(-12), Today.AddDays(-3), false), "the second pick extends it, either order");
        grid.Rows.Count(r => r.IsTask).Should().Be(2);
        range.Months.SelectMany(m => m.Days).Single(d => d.IsInMonth && d.Date == Today.AddDays(-5)).IsRangeMiddle.Should().BeTrue();

        range.ClearRangeCommand.Execute(null);
        filters.Spec.StartDate.Should().BeNull();
        start.IsCustom.Should().BeFalse();
    }

    [Fact]
    public void The_range_picker_moves_by_month_and_goes_back_to_today()
    {
        using var h = Seed();
        OpenGrid(h);
        var range = h.Workspace.Context.TaskFilters.CreateMenu().Find<FilterDateFieldViewModel>("EndDate")!.Range;
        var first = range.Months[0].Days.First(d => d.IsInMonth).Date;

        range.NextMonthCommand.Execute(null);
        range.Months[0].Days.First(d => d.IsInMonth).Date.Should().Be(first.AddMonths(1));
        range.PreviousMonthCommand.Execute(null);
        range.PreviousMonthCommand.Execute(null);
        range.Months[0].Days.First(d => d.IsInMonth).Date.Should().Be(first.AddMonths(-1));

        range.GoToTodayCommand.Execute(null);
        range.Months[0].Days.Should().Contain(d => d.IsToday);
        h.Workspace.Context.TaskFilters.Spec.EndDate.Should().BeNull("navigating changes nothing");
    }

    [Fact]
    public void An_older_saved_range_shows_as_customised_plus_no_date()
    {
        var saved = new ViewFilterPreference(null, new Dictionary<string, IReadOnlyList<string>> { ["start"] = ["2026-10-01", "2026-10-20", "undated"] });
        using var h = Seed(UiPreferences.Default with { ViewFilters = new Dictionary<string, ViewFilterPreference> { ["tasks"] = saved } });
        var grid = OpenGrid(h);
        var start = h.Workspace.Context.TaskFilters.CreateMenu().Find<FilterDateFieldViewModel>("StartDate")!;

        start.IsCustom.Should().BeTrue();
        start.Range.From.Should().Be(new DateOnly(2026, 10, 1));
        start.Range.To.Should().Be(new DateOnly(2026, 10, 20));
        start.Presets.Single(p => p.AutomationId.EndsWith("_NoDate", StringComparison.Ordinal)).IsChecked.Should().BeTrue();
        start.Presets.Where(p => !p.AutomationId.EndsWith("_NoDate", StringComparison.Ordinal)).Should().OnlyContain(p => !p.IsChecked);
        grid.Rows.Count(r => r.IsTask).Should().Be(3, "both dated tasks started in the range and the undated one is kept");
    }

    [Fact]
    public void A_rolling_date_preset_moves_with_the_local_day()
    {
        using var h = Seed();
        var grid = OpenGrid(h);
        var start = h.Workspace.Context.TaskFilters.CreateMenu().Find<FilterDateFieldViewModel>("StartDate")!;
        start.Presets.Single(p => p.AutomationId.EndsWith("_ThisWeek", StringComparison.Ordinal)).IsChecked = true;
        h.Settle();
        grid.Rows.Count(r => r.IsTask).Should().Be(1, "only the task that started 3 days ago is in this week");

        h.Days.Raise(Today.AddDays(7));
        h.PumpUntil(() => h.Store.Today == Today.AddDays(7));
        h.Settle();

        grid.Rows.Count(r => r.IsTask).Should().Be(0, "a week later, this week is a different week");
    }

    [Fact]
    public void Active_date_is_one_choice_at_a_time_and_none_is_all_time()
    {
        using var h = Seed();
        var grid = OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        var active = filters.CreateMenu().Find<ActiveDateFieldViewModel>("ActiveDate")!;

        active.Last7Days.IsChecked = true;
        h.Settle();
        filters.Spec.Period.Should().Be(PeriodFilter.Last7Days);
        grid.Rows.Count(r => r.IsTask).Should().Be(1);
        filters.Chips.Select(c => c.Key).Should().Contain("ActiveDate");
        h.Preferences.Current.ViewFilters!["tasks"].Fields[FilterPersistence.PeriodKey].Should().Equal("last7");

        active.Last30Days.IsChecked = true;
        active.Last7Days.IsChecked.Should().BeFalse();
        filters.Spec.Period.Should().Be(PeriodFilter.Last30Days);

        active.Range.Pick(Today.AddDays(-10));
        active.Range.Pick(Today.AddDays(-9));
        active.Last30Days.IsChecked.Should().BeFalse("a customised range replaces the preset");
        filters.Spec.Period.Should().Be(PeriodFilter.Custom(Today.AddDays(-10), Today.AddDays(-9)));
        h.Settle();
        grid.Rows.Count(r => r.IsTask).Should().Be(1, "only the task active 10 to 9 days ago");

        active.Last7Days.IsChecked = true;
        active.IsCustom.Should().BeFalse();
        active.Last7Days.IsChecked = false;
        filters.Spec.Period.Should().BeNull("no choice is All time");
        filters.ActiveFieldCount.Should().Be(0);
    }

    [Fact]
    public void The_calendar_scope_has_planned_dates_and_the_active_date_too()
    {
        using var h = Seed();
        var filters = h.Workspace.Context.CrFilters;
        var menu = filters.CreateMenu();

        menu.Find<FilterDateFieldViewModel>("Planned")!.Presets.Single(p => p.AutomationId == "Filters_Planned_NoDate").IsChecked = true;
        menu.Find<ActiveDateFieldViewModel>("ActiveDate")!.Range.Pick(new DateOnly(2026, 11, 7));
        menu.Find<ActiveDateFieldViewModel>("ActiveDate")!.Range.Pick(new DateOnly(2026, 11, 1));

        filters.Spec.Planned.Should().Be(new DateRangeFilter(null, null, true));
        filters.Spec.Period.Should().Be(PeriodFilter.Custom(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7)));
        filters.ActiveFieldCount.Should().Be(2);
        h.Preferences.Current.ViewFilters!["calendar"].Fields[FilterPersistence.PeriodKey].Should().Equal("custom", "2026-11-01", "2026-11-07");
    }

    [Fact]
    public void A_reopened_menu_shows_the_applied_filter()
    {
        using var h = Seed();
        OpenGrid(h);
        var filters = h.Workspace.Context.TaskFilters;
        filters.CreateMenu().Find<ActiveDateFieldViewModel>("ActiveDate")!.Last7Days.IsChecked = true;

        var reopened = filters.CreateMenu();

        reopened.Find<ActiveDateFieldViewModel>("ActiveDate")!.Last7Days.IsChecked.Should().BeTrue();
        reopened.Rows.Single(r => r.Key == "ActiveDate").IsSet.Should().BeTrue();
        reopened.CanClearAll.Should().BeTrue();

        filters.Chips.Single(c => c.Key == "ActiveDate").Remove();
        h.Settle();
        filters.Spec.Period.Should().BeNull();
    }

    [Fact]
    [Trait("Req", "B-15")]
    public void The_dashboard_active_date_narrows_the_counts_and_clear_filters_resets_it()
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
        dashboard.ActiveTasks.Value.Should().Be("1", "only the task active 10 to 9 days ago");

        dashboard.Period.From = Picker(Today);
        h.Settle();

        dashboard.Period.HasError.Should().BeTrue();
        dashboard.Period.Error.Should().Be("Filters_InvertedRange");
        dashboard.ActiveTasks.Value.Should().Be("1", "an inverted range is never applied");
    }

    [Fact]
    [Trait("Req", "B-15")]
    public void A_saved_dashboard_active_date_is_restored_and_a_drill_down_carries_it_into_the_grid()
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
        filters.Chips.Select(c => c.Key).Should().Contain("ActiveDate");
        grid.Rows.Count(r => r.IsTask).Should().Be(1, "the drill-down shows exactly what the card counts");
    }

    [Fact]
    [Trait("Req", "B-15")]
    public void The_dashboard_active_date_ignores_a_no_selection_write_and_keeps_the_saved_range()
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
    [Trait("Req", "AC-21")]
    public void A_rolling_active_date_moves_with_the_local_day()
    {
        using var h = Seed();
        var grid = OpenGrid(h);
        ApplyLast7Days(h);
        grid.Rows.Count(r => r.IsTask).Should().Be(1);

        h.Days.Raise(Today.AddDays(5));
        h.PumpUntil(() => h.Store.Today == Today.AddDays(5));
        h.Settle();

        grid.Rows.Count(r => r.IsTask).Should().Be(0, "the task active 3 days ago ended before the new range");
    }

    [Fact]
    [Trait("Req", "AC-21")]
    public void A_new_day_that_arrives_with_a_snapshot_still_moves_a_rolling_active_date()
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
    public void The_board_follows_a_rolling_active_date_across_midnight()
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
    public void Clear_filters_is_available_while_the_active_date_box_shows_an_unapplied_custom_range()
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
        h.Workspace.Context.TaskFilters.CreateMenu().Find<ActiveDateFieldViewModel>("ActiveDate")!.Last7Days.IsChecked = true;
        h.Settle();
    }

    /// <summary>Three tasks: active on the day 3 days ago only, active from 10 to 9 days ago, and undated; optionally two CRs.</summary>
    private static WorkspaceHarness Seed(UiPreferences? preferences = null, bool crs = false) =>
        WorkspaceHarness.Create(
            async s =>
            {
                await s.DatedTaskAsync("Recent", Today.AddDays(-3), Today.AddDays(-3));
                await s.DatedTaskAsync("Older", Today.AddDays(-10), Today.AddDays(-9));
                await s.TaskAsync("Undated");
                if (crs)
                {
                    await s.CrAsync("CR-1", "Card freeze");
                    await s.CrAsync("CR-2", "Instant transfer");
                }
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
