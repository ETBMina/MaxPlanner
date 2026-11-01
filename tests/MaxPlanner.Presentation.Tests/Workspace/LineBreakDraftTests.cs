using AwesomeAssertions;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Reviews;
using MaxPlanner.Presentation.SubtaskCards;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;
using ViewState = MaxPlanner.Presentation.Workspace.ViewState;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// ADR 0025: stored text uses LF line breaks while a WinUI text box returns CR, so a draft that differs from the stored
/// text only in its line breaks is unchanged: not dirty in an editor, and not committed from a Grid cell or an inline
/// rename (ADR 0010).
/// </summary>
[Trait("Req", "B-02")]
public sealed class LineBreakDraftTests
{
    private const string TextBoxText = "First line\rSecond line\r\rLast";
    private const string StoredText = "First line\nSecond line\n\nLast";

    [Fact]
    public void Notes_typed_with_carriage_returns_are_stored_with_line_feeds_and_the_reopened_editor_is_not_dirty()
    {
        using var h = BoardMoveTests.Board(out var board, "Task");
        using var dialogs = new TaskEditorTests.DialogRecorder(h.Workspace.Context);
        var card = board.Columns[0].Cards.Single();
        var flow = board.OpenAsync(card);
        var editor = dialogs.Next<TaskEditorViewModel>(h);
        editor.Notes = TextBoxText;
        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Await(flow);
        h.Settle();
        h.Task(new TaskId(card.Key)).Notes.Should().Be(StoredText);

        _ = board.OpenAsync(board.Columns[0].Cards.Single());
        var reopened = dialogs.Next<TaskEditorViewModel>(h);
        reopened.Notes = TextBoxText;

        reopened.IsDirty.Should().BeFalse("the text box's CR line breaks are the stored LF line breaks");
        reopened.Notes = TextBoxText + "\rMore";
        reopened.IsDirty.Should().BeTrue();
        reopened.DiscardChanges();
    }

    [Fact]
    public void A_checklist_row_and_a_multiline_custom_field_reloaded_with_carriage_returns_leave_the_task_editor_clean()
    {
        TaskId task = default;
        var field = new FieldId(Guid.NewGuid());
        using var h = WorkspaceHarness.Create(async s =>
        {
            (await new AddFieldDefinition(s.Coordinator).ExecuteAsync(
                new AddFieldDefinitionCommand(OperationId.New(), field, FieldTarget.Task, "summary", FieldKind.MultilineText, new FieldDefinitionDraft("Summary", false, null, [])), default))
                .IsSuccess.Should().BeTrue();
            task = await s.TaskAsync("Task", "Service", null, StoredText);
            await SetCustomAsync(s, task, field, revision: 2);
        });
        using var dialogs = new TaskEditorTests.DialogRecorder(h.Workspace.Context);
        _ = h.Workspace.Context.Tasks.OpenAsync(task);
        var editor = dialogs.Next<TaskEditorViewModel>(h);
        var custom = editor.CustomFields.Fields.Single(f => f.Field.Id == field);
        custom.Text.Should().Be(StoredText);

        editor.ChecklistItems.Single().Text = TextBoxText;
        custom.Text = TextBoxText;

        editor.IsDirty.Should().BeFalse();
        custom.Text = TextBoxText + "\rMore";
        editor.IsDirty.Should().BeTrue();
        editor.DiscardChanges();
    }

    [Fact]
    public void A_cr_description_reloaded_with_carriage_returns_leaves_the_editor_clean()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Line breaks"));
        using var dialogs = new TaskEditorTests.DialogRecorder(h.Workspace.Context);
        var flow = h.Workspace.Context.ChangeRequests.OpenAsync(cr);
        var editor = dialogs.Next<CrEditorViewModel>(h);
        editor.Description = TextBoxText;
        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Await(flow);
        h.Settle();

        _ = h.Workspace.Context.ChangeRequests.OpenAsync(cr);
        var reopened = dialogs.Next<CrEditorViewModel>(h);
        reopened.Description.Should().Be(StoredText);
        reopened.Description = TextBoxText;

        reopened.IsDirty.Should().BeFalse();
        reopened.DiscardChanges();
    }

    [Fact]
    public void Review_comments_reloaded_with_carriage_returns_leave_the_editor_clean()
    {
        using var h = WorkspaceHarness.Create(async s =>
            (await new CreateReview(s.Coordinator).ExecuteAsync(new CreateReviewCommand(
                OperationId.New(), new ReviewItemId(Guid.NewGuid()), new ReviewDraft("Review", null, null, [], null, StoredText), null, false), default))
                .IsSuccess.Should().BeTrue());
        using var dialogs = new TaskEditorTests.DialogRecorder(h.Workspace.Context);
        var reviews = h.Workspace.Checklists.Reviews;
        h.PumpUntil(() => reviews.Open.Count == 1);
        _ = reviews.EditAsync(reviews.Open.Single());
        var editor = dialogs.Next<ReviewEditorViewModel>(h);
        editor.Comments.Should().Be(StoredText);

        editor.Comments = TextBoxText;

        editor.IsDirty.Should().BeFalse();
        editor.Comments = TextBoxText + "\rMore";
        editor.IsDirty.Should().BeTrue();
        editor.DiscardChanges();
    }

    [Fact]
    public void Subtask_card_notes_reloaded_with_carriage_returns_leave_the_editor_clean()
    {
        var card = new SubtaskCardId(Guid.NewGuid());
        using var h = WorkspaceHarness.Create(async s =>
        {
            var parent = await s.TaskAsync("Parent");
            (await new CreateSubtaskCard(s.Coordinator).ExecuteAsync(new CreateSubtaskCardCommand(OperationId.New(), parent, card, "Card", StoredText), default))
                .IsSuccess.Should().BeTrue();
        });
        using var dialogs = new TaskEditorTests.DialogRecorder(h.Workspace.Context);
        _ = h.Workspace.Context.Tasks.OpenSubtaskCardAsync(card);
        var editor = dialogs.Next<SubtaskCardEditorViewModel>(h);
        editor.Notes.Should().Be(StoredText);

        editor.Notes = TextBoxText;

        editor.IsDirty.Should().BeFalse();
        editor.Notes = TextBoxText + "\rMore";
        editor.IsDirty.Should().BeTrue();
        editor.DiscardChanges();
    }

    [Fact]
    public void A_grid_notes_draft_that_differs_only_in_line_breaks_is_not_committed()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Notes task"));
        var grid = OpenGrid(h);
        var row = grid.Rows.Single(r => r.Key == task.Value);
        grid.BeginEdit(row, GridColumns.Notes).Should().BeTrue();
        row.DraftText = TextBoxText;
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();
        var revision = h.Task(task).Revision;
        h.Task(task).Notes.Should().Be(StoredText);

        grid.BeginEdit(row, GridColumns.Notes).Should().BeTrue();
        row.DraftText = TextBoxText;

        row.IsDraftChanged().Should().BeFalse();
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();
        h.Task(task).Revision.Should().Be(revision, "nothing was written");
    }

    [Fact]
    public void A_grid_multiline_custom_cell_reloaded_with_carriage_returns_is_not_a_change()
    {
        TaskId task = default;
        var field = new FieldId(Guid.NewGuid());
        using var h = WorkspaceHarness.Create(async s =>
        {
            (await new AddFieldDefinition(s.Coordinator).ExecuteAsync(
                new AddFieldDefinitionCommand(OperationId.New(), field, FieldTarget.Task, "summary", FieldKind.MultilineText, new FieldDefinitionDraft("Summary", false, null, [])), default))
                .IsSuccess.Should().BeTrue();
            task = await s.TaskAsync("Task");
            await SetCustomAsync(s, task, field, revision: 1);
        });
        var grid = OpenGrid(h);
        var row = grid.Rows.Single(r => r.Key == task.Value);
        grid.BeginEdit(row, GridColumns.Custom(field)).Should().BeTrue();

        row.DraftCustom!.Text = TextBoxText;

        row.IsDraftChanged().Should().BeFalse();
        row.DraftCustom.Text = TextBoxText + "\rMore";
        row.IsDraftChanged().Should().BeTrue();
    }

    [Fact]
    public void An_unchanged_checklist_rename_with_carriage_returns_writes_nothing()
    {
        using var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Task", "Service", null, StoredText));
        var checklists = h.Workspace.Checklists;
        h.PumpUntil(() => checklists.Groups.Count == 1);
        var item = checklists.Groups.Single().OpenItems.Single();
        var sequence = h.Store.Sequence;
        item.BeginRename();

        item.RenameText = TextBoxText;

        item.IsRenameUnchanged.Should().BeTrue();
        h.Await(item.CommitRenameAsync()).Should().BeTrue();
        h.Settle();
        item.IsRenaming.Should().BeFalse();
        h.Store.Sequence.Should().Be(sequence, "nothing was written");
    }

    [Fact]
    public void An_unchanged_general_todo_rename_with_carriage_returns_writes_nothing()
    {
        using var h = WorkspaceHarness.Create(async s =>
            (await new AddGeneralTodo(s.Coordinator).ExecuteAsync(new AddGeneralTodoCommand(OperationId.New(), new GeneralTodoId(Guid.NewGuid()), StoredText), default))
                .IsSuccess.Should().BeTrue());
        var general = h.Workspace.Checklists.General;
        h.PumpUntil(() => general.OpenItems.Count == 1);
        var item = general.OpenItems.Single();
        var sequence = h.Store.Sequence;
        item.BeginRename();

        item.RenameText = TextBoxText;

        item.IsRenameUnchanged.Should().BeTrue();
        h.Await(item.CommitRenameAsync()).Should().BeTrue();
        h.Settle();
        h.Store.Sequence.Should().Be(sequence, "nothing was written");
        item.RenameText = TextBoxText + "\rMore";
        item.IsRenameUnchanged.Should().BeFalse();
    }

    private static async Task SetCustomAsync(WorkspaceHarness.Seeder s, TaskId task, FieldId field, long revision) =>
        (await new UpdateTaskContent(s.Coordinator).ExecuteAsync(new UpdateTaskContentCommand(
            OperationId.New(), task, revision, new TaskContentDraft("Task", "Service", null, null, [], null,
                new Dictionary<FieldId, CustomValue> { [field] = new MultilineTextCustomValue(TextBoxText) })), default))
            .IsSuccess.Should().BeTrue();

    private static GridPageViewModel OpenGrid(WorkspaceHarness h)
    {
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.State != ViewState.Loading);
        h.Settle();
        return grid;
    }
}
