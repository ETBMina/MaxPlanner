using System.Globalization;
using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Tests.Testing;
using ViewState = MaxPlanner.Presentation.Workspace.ViewState;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// A Grid cell's typed value (its UI Automation value) becomes the open edit's draft, or is refused with a message
/// shown on the row and thrown to the caller.
/// </summary>
[Trait("Req", "B-07")]
public sealed class GridTypedValueTests
{
    private static readonly FieldId NumberField = new(Guid.NewGuid());
    private static readonly FieldId DateField = new(Guid.NewGuid());
    private static readonly FieldId LookupField = new(Guid.NewGuid());
    private static readonly FieldId PeopleField = new(Guid.NewGuid());
    private static readonly FieldId TextField = new(Guid.NewGuid());

    [Fact]
    public void A_status_name_is_matched_ignoring_case_and_surrounding_spaces()
    {
        using var h = Arrange(out var grid, out var row);
        var status = grid.StatusOptions.Last();
        grid.BeginEdit(row, GridColumns.Status).Should().BeTrue();

        row.SetDraftFromText("  " + status.Name.ToUpperInvariant() + " ", grid.StatusOptions, grid.PriorityOptions);

        row.DraftStatus.Should().Be(status);
        row.HasError.Should().BeFalse();
    }

    [Fact]
    public void A_priority_name_is_matched_ignoring_case()
    {
        using var h = Arrange(out var grid, out var row);
        var priority = grid.PriorityOptions.Last();
        grid.BeginEdit(row, GridColumns.Priority).Should().BeTrue();

        row.SetDraftFromText(priority.Name.ToLowerInvariant(), grid.StatusOptions, grid.PriorityOptions);

        row.DraftPriority.Should().Be(priority);
    }

    [Theory]
    [InlineData(GridColumns.Status, "Grid_ValueError_NoStatus")]
    [InlineData(GridColumns.Priority, "Grid_ValueError_NoPriority")]
    [InlineData(GridColumns.StartDate, "Grid_ValueError_NotDate")]
    [InlineData(GridColumns.EndDate, "Grid_ValueError_NotDate")]
    public void An_unknown_name_or_a_non_date_is_refused(string column, string message)
    {
        using var h = Arrange(out var grid, out var row);
        grid.BeginEdit(row, column).Should().BeTrue();

        var set = () => row.SetDraftFromText("No such value", grid.StatusOptions, grid.PriorityOptions);

        set.Should().Throw<ArgumentException>().Which.Message.Should().Be(message);
        row.ErrorMessage.Should().Be(message, "the refusal is shown on the row");
    }

    [Theory]
    [InlineData(GridColumns.Designer)]
    [InlineData(GridColumns.Developer)]
    [InlineData(GridColumns.Tags)]
    public void People_and_tags_change_only_through_the_picker(string column)
    {
        using var h = Arrange(out var grid, out var row);
        grid.BeginEdit(row, column).Should().BeTrue();

        var set = () => row.SetDraftFromText("Dana", grid.StatusOptions, grid.PriorityOptions);

        set.Should().Throw<ArgumentException>().Which.Message.Should().Be("Grid_ValueError_UsePicker");
        row.ErrorMessage.Should().Be("Grid_ValueError_UsePicker");
    }

    [Theory]
    [InlineData(GridColumns.StartDate)]
    [InlineData(GridColumns.EndDate)]
    public void A_date_parses_in_the_current_culture_and_an_empty_value_clears_it(string column)
    {
        using var h = Arrange(out var grid, out var row);
        var day = new DateOnly(2026, 3, 14);
        grid.BeginEdit(row, column).Should().BeTrue();

        row.SetDraftFromText(day.ToString("d", CultureInfo.CurrentCulture), grid.StatusOptions, grid.PriorityOptions);
        row.DraftDate.Should().Be(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue)));
        row.DraftDateCleared.Should().BeFalse();

        row.SetDraftFromText("   ", grid.StatusOptions, grid.PriorityOptions);
        row.DraftDate.Should().BeNull();
        row.DraftDateCleared.Should().BeTrue("an empty value is an explicit clear (B-04)");
    }

    [Theory]
    [InlineData(GridColumns.Title)]
    [InlineData(GridColumns.Service)]
    [InlineData(GridColumns.Notes)]
    public void Text_columns_take_the_value_as_typed(string column)
    {
        using var h = Arrange(out var grid, out var row);
        grid.BeginEdit(row, column).Should().BeTrue();

        row.SetDraftFromText("  New text ", grid.StatusOptions, grid.PriorityOptions);

        row.DraftText.Should().Be("  New text ", "text is not trimmed here; the commit applies the column's own rules");
    }

    [Fact]
    public void A_custom_number_parses_in_the_current_culture_and_an_empty_value_clears_it()
    {
        using var h = Arrange(out var grid, out var row);
        grid.BeginEdit(row, GridColumns.Custom(NumberField)).Should().BeTrue();

        row.SetDraftFromText(12.5.ToString(CultureInfo.CurrentCulture), grid.StatusOptions, grid.PriorityOptions);
        row.DraftCustom!.Number.Should().Be(12.5);

        row.SetDraftFromText(string.Empty, grid.StatusOptions, grid.PriorityOptions);
        row.DraftCustom.Number.Should().Be(double.NaN);

        var set = () => row.SetDraftFromText("twelve", grid.StatusOptions, grid.PriorityOptions);
        set.Should().Throw<ArgumentException>().Which.Message.Should().Be("Grid_ValueError_NotNumber");
        row.ErrorMessage.Should().Be("Grid_ValueError_NotNumber");
    }

    [Fact]
    public void A_custom_date_parses_in_the_current_culture_and_an_empty_value_clears_it()
    {
        using var h = Arrange(out var grid, out var row);
        var day = new DateOnly(2026, 11, 2);
        grid.BeginEdit(row, GridColumns.Custom(DateField)).Should().BeTrue();

        row.SetDraftFromText(day.ToString("d", CultureInfo.CurrentCulture), grid.StatusOptions, grid.PriorityOptions);
        row.DraftCustom!.Date.Should().Be(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue)));

        row.SetDraftFromText(" ", grid.StatusOptions, grid.PriorityOptions);
        row.DraftCustom.Date.Should().BeNull();

        var set = () => row.SetDraftFromText("someday", grid.StatusOptions, grid.PriorityOptions);
        set.Should().Throw<ArgumentException>().Which.Message.Should().Be("Grid_ValueError_NotDate");
        row.ErrorMessage.Should().Be("Grid_ValueError_NotDate");
    }

    [Fact]
    public void A_custom_lookup_matches_an_option_ignoring_case_and_an_empty_value_chooses_none()
    {
        using var h = Arrange(out var grid, out var row);
        grid.BeginEdit(row, GridColumns.Custom(LookupField)).Should().BeTrue();
        var custom = row.DraftCustom!;

        row.SetDraftFromText(" high ", grid.StatusOptions, grid.PriorityOptions);
        custom.SelectedOption!.Label.Should().Be("High");

        row.SetDraftFromText(string.Empty, grid.StatusOptions, grid.PriorityOptions);
        custom.SelectedOption.Should().Be(custom.Options[0]);
        custom.SelectedOption!.Id.Should().BeNull("the first choice is None");

        var set = () => row.SetDraftFromText("Medium", grid.StatusOptions, grid.PriorityOptions);
        set.Should().Throw<ArgumentException>().Which.Message.Should().Be("Grid_ValueError_NoOption");
        row.ErrorMessage.Should().Be("Grid_ValueError_NoOption");
    }

    [Fact]
    public void A_custom_people_field_changes_only_through_the_picker()
    {
        using var h = Arrange(out var grid, out var row);
        grid.BeginEdit(row, GridColumns.Custom(PeopleField)).Should().BeTrue();

        var set = () => row.SetDraftFromText("Dana", grid.StatusOptions, grid.PriorityOptions);

        set.Should().Throw<ArgumentException>().Which.Message.Should().Be("Grid_ValueError_UsePicker");
        row.ErrorMessage.Should().Be("Grid_ValueError_UsePicker");
    }

    [Fact]
    public void A_custom_text_field_takes_the_trimmed_value()
    {
        using var h = Arrange(out var grid, out var row);
        grid.BeginEdit(row, GridColumns.Custom(TextField)).Should().BeTrue();

        row.SetDraftFromText("  Risky  ", grid.StatusOptions, grid.PriorityOptions);

        row.DraftCustom!.Text.Should().Be("Risky");
    }

    private static WorkspaceHarness Arrange(out GridPageViewModel grid, out GridRowViewModel row)
    {
        TaskId task = default;
        var h = WorkspaceHarness.Create(async s =>
        {
            await AddFieldAsync(s, NumberField, "points", FieldKind.Number);
            await AddFieldAsync(s, DateField, "due", FieldKind.Date);
            await AddFieldAsync(s, LookupField, "risk", FieldKind.Lookup,
                new FieldOptionDraft(new FieldOptionId(Guid.NewGuid()), "High"), new FieldOptionDraft(new FieldOptionId(Guid.NewGuid()), "Low"));
            await AddFieldAsync(s, PeopleField, "reviewers", FieldKind.People);
            await AddFieldAsync(s, TextField, "note", FieldKind.Text);
            task = await s.TaskAsync("Task");
        });
        grid = h.Workspace.Grid;
        var opened = grid;
        h.PumpUntil(() => opened.State != ViewState.Loading);
        h.Settle();
        row = grid.Rows.Single(r => r.Key == task.Value);
        return h;
    }

    private static async Task AddFieldAsync(WorkspaceHarness.Seeder s, FieldId id, string key, FieldKind kind, params FieldOptionDraft[] options) =>
        (await new AddFieldDefinition(s.Coordinator).ExecuteAsync(
            new AddFieldDefinitionCommand(OperationId.New(), id, FieldTarget.Task, key, kind, new FieldDefinitionDraft(key, false, null, options)), default))
            .IsSuccess.Should().BeTrue();
}
