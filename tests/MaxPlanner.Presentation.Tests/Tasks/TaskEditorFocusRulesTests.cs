using AwesomeAssertions;
using MaxPlanner.Presentation.Tasks;

namespace MaxPlanner.Presentation.Tests.Tasks;

/// <summary>Which task editor field the first Tab reaches from the dialog's neutral first focus.</summary>
public sealed class TaskEditorFocusRulesTests
{
    [Theory]
    [InlineData(true, true, true, "Title")]
    [InlineData(false, true, false, "Service")]
    [InlineData(false, true, true, "Service")]
    [InlineData(false, false, true, "Title")]
    public void The_first_tab_goes_to_the_title_when_editable_otherwise_the_first_editable_field(
        bool isTitleEditable, bool canEditFields, bool isTitleBoxShown, string expected) =>
        TaskEditorFocusRules.FirstFieldKey(isTitleEditable, canEditFields, isTitleBoxShown).Should().Be(expected);

    [Fact]
    public void An_archived_linked_task_has_no_first_field_and_no_special_case() =>
        TaskEditorFocusRules.FirstFieldKey(isTitleEditable: false, canEditFields: false, isTitleBoxShown: false).Should().BeNull();
}
