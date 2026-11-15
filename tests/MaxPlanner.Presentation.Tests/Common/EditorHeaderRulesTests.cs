using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Controls;
using MaxPlanner.Presentation.Common.Dialogs;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// The decisions of the editors' shared header (design §5.0, §5.1): the info box's columns, the reading-order cells of
/// its facts, and when the task editor's on-demand tag search box shows. The query arrives through x:Bind, which turns
/// a null string into an empty one, so null and empty must mean the same.
/// </summary>
public sealed class EditorHeaderRulesTests
{
    [Theory]
    [InlineData(1920, 4)]
    [InlineData(1008, 4)]
    [InlineData(1007, 2)]
    [InlineData(640, 2)]
    [InlineData(400, 2)]
    public void The_info_box_has_four_columns_at_L_and_two_below(double windowWidth, int columns) =>
        EditorFactsLayout.Columns(windowWidth).Should().Be(columns);

    [Theory]
    [InlineData(0, 4, 0, 0)]
    [InlineData(3, 4, 0, 3)]
    [InlineData(4, 4, 1, 0)]
    [InlineData(5, 4, 1, 1)]
    [InlineData(1, 2, 0, 1)]
    [InlineData(2, 2, 1, 0)]
    [InlineData(5, 2, 2, 1)]
    public void Facts_fill_the_columns_in_reading_order(int index, int columns, int row, int column)
    {
        EditorFactsLayout.Row(index, columns).Should().Be(row);
        EditorFactsLayout.Column(index, columns).Should().Be(column);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_column_count_below_one_counts_as_one(int columns)
    {
        EditorFactsLayout.Row(3, columns).Should().Be(3);
        EditorFactsLayout.Column(3, columns).Should().Be(0);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    [InlineData(true, null, true)]
    [InlineData(true, "", true)]
    [InlineData(false, "fin", true)]
    public void An_on_demand_tag_input_shows_only_once_revealed_or_while_it_holds_text(bool isRevealed, string? query, bool shown) =>
        TokenPickerRules.ShowsInlineInput(isEditable: true, isInputOnDemand: true, isRevealed, query).Should().Be(shown);

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "")]
    [InlineData(true, "fin")]
    public void An_always_on_tag_input_shows_whenever_the_field_is_editable(bool isRevealed, string? query) =>
        TokenPickerRules.ShowsInlineInput(isEditable: true, isInputOnDemand: false, isRevealed, query).Should().BeTrue();

    [Theory]
    [InlineData(true, true, "fin")]
    [InlineData(false, true, "")]
    [InlineData(true, false, null)]
    public void A_read_only_tag_field_never_shows_its_input(bool isInputOnDemand, bool isRevealed, string? query) =>
        TokenPickerRules.ShowsInlineInput(isEditable: false, isInputOnDemand, isRevealed, query).Should().BeFalse();

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    public void The_add_tag_button_stands_in_for_a_hidden_on_demand_input(bool isEditable, bool isInputOnDemand, bool isInputShown, bool shown) =>
        TokenPickerRules.ShowsAddButton(isEditable, isInputOnDemand, isInputShown).Should().Be(shown);
}
