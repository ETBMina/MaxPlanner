using AwesomeAssertions;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Tests.Testing;
using ViewState = MaxPlanner.Presentation.Workspace.ViewState;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>ADR 0024: the Grid's Designer and Developer cells edit one person with the single-person picker.</summary>
[Trait("Req", "B-07")]
public sealed class GridSinglePersonTests
{
    [Fact]
    [Trait("Req", "B-02.6")]
    public void A_designer_cell_edit_keeps_one_person_and_creates_a_new_name_only_on_commit()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            task = await s.TaskAsync("Onboarding copy");
            await s.CrAsync("CR-1", "Creates a person");
        });
        var grid = Open(h);
        var row = grid.Rows.Single(r => r.Key == task.Value);
        var people = h.Store.CurrentWorkflow!.People.Count;

        grid.BeginEdit(row, GridColumns.Designer).Should().BeTrue();
        var picker = row.DraftTokens!;
        picker.IsSingleSelection.Should().BeTrue();
        picker.Query = "Dana";
        picker.SubmitQuery().Should().BeTrue();
        picker.Query = "owner cr-1";
        picker.SubmitQuery().Should().BeTrue("the exact existing name is chosen case-insensitively");
        picker.Tokens.Select(t => t.Name).Should().Equal(["Owner CR-1"], "a second choice replaces the first");
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();

        h.Task(task).Designer!.Name.Should().Be("Owner CR-1");
        h.Task(task).Developer.Should().BeNull();
        h.Store.CurrentWorkflow!.People.Should().HaveCount(people, "Dana was replaced before the commit");
        row.DisplayValue(GridColumns.Designer).Should().Be("Owner CR-1");
        row.PeopleNames(GridColumns.Designer).Should().Equal(["Owner CR-1"]);
        row.DisplayValue(GridColumns.Developer).Should().BeEmpty();

        grid.BeginEdit(row, GridColumns.Developer).Should().BeTrue();
        row.DraftTokens!.Query = "Erin";
        row.DraftTokens.SubmitQuery().Should().BeTrue();
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();

        h.Task(task).Developer!.Name.Should().Be("Erin", "B-02.6: the new person is created by the commit");
        h.Store.CurrentWorkflow!.People.Should().HaveCount(people + 1);

        grid.BeginEdit(row, GridColumns.Designer).Should().BeTrue();
        row.DraftTokens!.Remove(row.DraftTokens.Tokens.Single());
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();

        h.Task(task).Designer.Should().BeNull("removing the chip clears the Designer");
        h.Task(task).Developer!.Name.Should().Be("Erin");
        row.DisplayValue(GridColumns.Designer).Should().BeEmpty();
        row.PeopleNames(GridColumns.Designer).Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "T-09")]
    public void A_layout_saved_with_the_plural_keys_keeps_the_designer_and_developer_columns()
    {
        var saved = new GridLayoutPreference(
            [
                new GridColumnPreference(GridColumns.Title, 300, true),
                new GridColumnPreference("Developers", 250, true),
                new GridColumnPreference("Designers", 210, false),
            ],
            SortColumnKey: "Developers",
            SortDescending: true);
        using var h = WorkspaceHarness.Create(preferences: UiPreferences.Default with { GridLayout = saved });
        var grid = Open(h);

        grid.Columns.Take(3).Select(c => (c.Key, c.Width, c.IsVisible)).Should().Equal(
            [(GridColumns.Title, 300d, true), (GridColumns.Developer, 250d, true), (GridColumns.Designer, 210d, false)],
            "the legacy keys keep their saved position, width, and visibility");
        grid.Columns.Should().ContainSingle(c => c.Key == GridColumns.Designer);
        grid.Columns.Should().ContainSingle(c => c.Key == GridColumns.Developer);
        grid.Columns.Single(c => c.Key == GridColumns.Developer).Sort.Should().Be(GridSortDirection.Descending);

        var layout = h.Preferences.Current.GridLayout!;
        layout.Columns.Select(c => c.Key).Should().Contain([GridColumns.Designer, GridColumns.Developer]).And.NotContain(["Designers", "Developers"]);
        layout.SortColumnKey.Should().Be(GridColumns.Developer, "the next save writes the current keys");
    }

    [Fact]
    [Trait("Req", "B-02.6")]
    public void A_commit_takes_the_text_still_in_the_person_box_as_ctrl_enter_and_light_dismiss_do()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            task = await s.TaskAsync("Onboarding copy");
            await s.CrAsync("CR-1", "Creates a person");
        });
        var grid = Open(h);
        var row = grid.Rows.Single(r => r.Key == task.Value);
        var people = h.Store.CurrentWorkflow!.People.Count;

        grid.BeginEdit(row, GridColumns.Designer).Should().BeTrue();
        row.DraftTokens!.Query = "owner cr-1";
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();
        h.Task(task).Designer!.Name.Should().Be("Owner CR-1", "an exact existing name in the box is committed");

        grid.BeginEdit(row, GridColumns.Developer).Should().BeTrue();
        row.DraftTokens!.Query = "Zed";
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();
        h.Task(task).Developer.Should().BeNull("unmatched text is dropped, not added");
        h.Store.CurrentWorkflow!.People.Should().HaveCount(people);

        grid.BeginEdit(row, GridColumns.Designer).Should().BeTrue();
        row.DraftTokens!.Query = string.Empty;
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();
        h.Task(task).Designer.Should().BeNull("an emptied box clears the Designer");
    }

    private static GridPageViewModel Open(WorkspaceHarness h)
    {
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.State != ViewState.Loading);
        h.Settle();
        return grid;
    }
}
