using AwesomeAssertions;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Presentation.Grid;
using Microsoft.UI.Xaml;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// Design §4.3: editable choice cells look like their picker (a chevron for drop-downs, a calendar for dates). The
/// affordance shows while the pointer is over the cell or the cell has non-pointer focus, never only on hover.
/// </summary>
[Trait("Req", "B-07")]
public sealed class GridPickerAffordanceTests
{
    [Theory]
    [InlineData(GridColumns.Status, GridPickerKind.DropDown)]
    [InlineData(GridColumns.Priority, GridPickerKind.DropDown)]
    [InlineData(GridColumns.StartDate, GridPickerKind.Calendar)]
    [InlineData(GridColumns.EndDate, GridPickerKind.Calendar)]
    [InlineData(GridColumns.Title, GridPickerKind.None)]
    [InlineData(GridColumns.Notes, GridPickerKind.None)]
    [InlineData(GridColumns.Designer, GridPickerKind.None)]
    [InlineData(GridColumns.Tags, GridPickerKind.None)]
    [InlineData(GridColumns.CrNumber, GridPickerKind.None)]
    public void Built_in_columns_get_the_picker_of_their_editor(string key, GridPickerKind expected) =>
        new GridColumnViewModel(key, key, null).PickerKind.Should().Be(expected);

    [Theory]
    [InlineData(FieldKind.Lookup, GridPickerKind.DropDown)]
    [InlineData(FieldKind.Date, GridPickerKind.Calendar)]
    [InlineData(FieldKind.Text, GridPickerKind.None)]
    [InlineData(FieldKind.Number, GridPickerKind.None)]
    [InlineData(FieldKind.People, GridPickerKind.None)]
    [InlineData(FieldKind.Url, GridPickerKind.None)]
    public void Custom_columns_get_a_drop_down_for_lookups_and_a_calendar_for_dates(FieldKind kind, GridPickerKind expected)
    {
        var field = Field(kind);

        new GridColumnViewModel(GridColumns.Custom(field.Id), "Field", field).PickerKind.Should().Be(expected);
    }

    [Theory]
    [InlineData(false, FocusState.Unfocused, false)]
    [InlineData(false, FocusState.Pointer, false)]
    [InlineData(false, FocusState.Keyboard, true)]
    [InlineData(false, FocusState.Programmatic, false)]
    [InlineData(true, FocusState.Unfocused, true)]
    [InlineData(true, FocusState.Pointer, true)]
    public void The_affordance_shows_on_hover_or_keyboard_focus_only(bool isPointerOver, FocusState focusState, bool expected) =>
        GridCellContent.IsPickerAffordanceShown(isPointerOver, focusState).Should().Be(expected);

    [Theory]
    [InlineData("Title", "Locked", "Title\nLocked")]
    [InlineData("Title", null, "Title")]
    [InlineData("Title", "", "Title")]
    [InlineData(null, "Locked", "Locked")]
    [InlineData("", "Locked", "Locked")]
    [InlineData(null, null, "")]
    [InlineData("", "", "")]
    public void The_locked_title_tooltip_joins_the_title_and_the_lock_reason(string? title, string? lockText, string expected) =>
        GridCellContent.TitleToolTip(title, lockText).Should().Be(expected.Replace("\n", Environment.NewLine, StringComparison.Ordinal));

    [Fact]
    public void The_Kind_column_is_narrow_enough_for_the_icon_and_row_menu_only() =>
        GridColumns.DefaultWidth(GridColumns.Kind).Should().BeLessThan(100);

    private static FieldDefinition Field(FieldKind kind)
    {
        IReadOnlyList<FieldOption> options = kind == FieldKind.Lookup
            ? [new FieldOption(new FieldOptionId(Guid.NewGuid()), LookupName.Create("High").Value, 0)]
            : [];
        return FieldDefinition.Create(new FieldId(Guid.NewGuid()), FieldKey.Create("field").Value, FieldTarget.Task, LookupName.Create("Field").Value,
            kind, false, null, 0, options).Value;
    }
}
