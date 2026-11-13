using AwesomeAssertions;
using MaxPlanner.Presentation.Tasks;

namespace MaxPlanner.Presentation.Tests.Tasks;

/// <summary>Which task editor field takes the dialog's first focus.</summary>
public sealed class TaskEditorFocusRulesTests
{
    [Theory]
    [InlineData(true, true, true, "Title")]
    [InlineData(false, true, false, "Service")]
    [InlineData(false, true, true, "Service")]
    [InlineData(false, false, true, "Title")]
    [InlineData(false, false, false, null)]
    public void The_first_focus_is_the_title_when_editable_otherwise_the_first_editable_field(
        bool isTitleEditable, bool canEditFields, bool isTitleBoxShown, string? expected) =>
        TaskEditorFocusRules.FirstFieldKey(isTitleEditable, canEditFields, isTitleBoxShown).Should().Be(expected);
}
