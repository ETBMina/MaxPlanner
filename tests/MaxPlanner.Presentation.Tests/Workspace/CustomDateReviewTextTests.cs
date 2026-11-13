using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// U-12: the conflict review shows a custom date field as a record date with the year, like the editor's own dates,
/// while the Grid cell's draft text keeps the short date that can be typed back into the cell.
/// </summary>
public sealed class CustomDateReviewTextTests
{
    [Fact]
    public void A_custom_date_reads_as_a_record_date_in_the_conflict_review_and_as_a_short_date_in_the_grid_draft()
    {
        TaskId task = default;
        var field = new FieldId(Guid.NewGuid());
        using var h = WorkspaceHarness.Create(async s =>
        {
            (await new AddFieldDefinition(s.Coordinator).ExecuteAsync(
                new AddFieldDefinitionCommand(OperationId.New(), field, FieldTarget.Task, "golive", FieldKind.Date, new FieldDefinitionDraft("Go-live", false, null, [])), default))
                .IsSuccess.Should().BeTrue();
            task = await s.TaskAsync("Task");
        });
        using var dialogs = new TaskEditorTests.DialogRecorder(h.Workspace.Context);
        _ = h.Workspace.Context.Tasks.OpenAsync(task);
        var editor = dialogs.Next<TaskEditorViewModel>(h);
        var custom = editor.CustomFields.Fields.Single(f => f.Field.Id == field);
        var day = new DateOnly(2026, 11, 12);

        custom.Date = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));

        custom.ReviewText.Should().Be(DateText.RecordDate(day));
        editor.CustomFields.DisplayText("None").Should().Be("Go-live: " + DateText.RecordDate(day));
        custom.DisplayText.Should().Be(DateText.ShortDate(day), "the Grid cell's draft text is unchanged");
        editor.DiscardChanges();
    }
}
