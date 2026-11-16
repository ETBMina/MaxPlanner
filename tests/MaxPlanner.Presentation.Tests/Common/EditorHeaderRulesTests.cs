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

    [Fact]
    public void A_hidden_fact_takes_no_cell_and_the_facts_after_it_close_up()
    {
        // A new Subtask card: Service, CR number, Task number, Squad, Owner shown, Created hidden.
        EditorFactsLayout.Cells([true, true, true, true, true, false], 4).Should().Equal(
            (0, 0), (0, 1), (0, 2), (0, 3), (1, 0), null);

        // A hidden fact in the middle leaves no gap: the next shown fact takes its cell.
        EditorFactsLayout.Cells([true, false, true, true], 2).Should().Equal((0, 0), null, (0, 1), (1, 0));
    }

    [Fact]
    public void With_every_fact_hidden_no_cell_is_used() =>
        EditorFactsLayout.Cells([false, false], 4).Should().HaveCount(2).And.AllSatisfy(cell => cell.Should().BeNull());

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
    public void The_tag_field_itself_is_the_tab_stop_while_its_on_demand_input_is_hidden(bool isEditable, bool isInputOnDemand, bool isInputShown, bool isTabStop) =>
        TokenPickerRules.IsFieldTabStop(isEditable, isInputOnDemand, isInputShown).Should().Be(isTabStop);

    [Theory]
    [InlineData(true, true, 0, InputRevealStep.Focused)]
    [InlineData(true, true, 1, InputRevealStep.Focused)]
    [InlineData(false, false, 0, InputRevealStep.WaitForLoad)]
    [InlineData(false, false, 1, InputRevealStep.GiveUp)]
    [InlineData(false, true, 0, InputRevealStep.GiveUp)]
    [InlineData(false, true, 1, InputRevealStep.GiveUp)]
    public void A_reveal_focuses_a_loaded_text_box_waits_once_for_one_still_loading_and_never_waits_forever(
        bool focused, bool isTextBoxLoaded, int attempt, InputRevealStep step) =>
        TokenPickerRules.NextRevealStep(focused, isTextBoxLoaded, attempt).Should().Be(step);

    [Theory]
    [InlineData(true, "tag-1", TagInputEscapeStep.CloseList)]
    [InlineData(true, null, TagInputEscapeStep.CloseList)]
    [InlineData(false, "tag-1", TagInputEscapeStep.ClearText)]
    [InlineData(false, "", TagInputEscapeStep.LeaveInput)]
    [InlineData(false, null, TagInputEscapeStep.LeaveInput)]
    public void Esc_in_the_tag_input_closes_the_list_then_clears_the_text_then_leaves(bool isListOpen, string? query, TagInputEscapeStep step) =>
        TokenPickerRules.TagInputEscape(isListOpen, query).Should().Be(step);

    [Theory]
    [InlineData(20, 30, 30)]
    [InlineData(30, 30, 30)]
    [InlineData(34, 30, 34)]
    [InlineData(0, 30, 30)]
    public void A_tag_field_line_is_never_lower_than_the_input_line(double tallestChild, double minLineHeight, double lineHeight) =>
        TokenPickerRules.FieldLineHeight(tallestChild, minLineHeight).Should().Be(lineHeight);

    [Theory]
    [InlineData('f', true, "f")]
    [InlineData('7', true, "7")]
    [InlineData(' ', true, "")]
    [InlineData('\r', false, "")]
    [InlineData('\t', false, "")]
    [InlineData('\b', false, "")]
    public void A_typed_character_reveals_the_tag_input_and_starts_the_query(char character, bool reveals, string queryStart)
    {
        TokenPickerRules.RevealsInput(character).Should().Be(reveals);
        TokenPickerRules.QueryStart(character).Should().Be(queryStart);
    }
}
