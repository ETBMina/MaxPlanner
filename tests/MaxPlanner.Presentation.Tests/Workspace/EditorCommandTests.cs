using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Reviews;
using MaxPlanner.Presentation.SubtaskCards;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// The editors' buttons bind commands (A-02). Each command runs the method the button used to call, and none has a
/// CanExecute rule of its own: the buttons keep their IsEnabled bindings, so a command never disables a button.
/// </summary>
public sealed class EditorCommandTests
{
    [Fact]
    public void The_task_editor_link_commands_walk_the_link_steps()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            id = await s.TaskAsync("Own title", service: "Cards API");
            await s.CrAsync("CR-1042", "Instant card freeze");
        });
        using var editor = new TaskEditorViewModel(h.Workspace.Context, id, null);

        editor.BeginLinkCommand.Execute(null);
        editor.IsLinkPickStep.Should().BeTrue();
        editor.CrPicker.Selected = editor.CrPicker.Results.Single();
        editor.LinkNextCommand.Execute(null);
        editor.IsLinkConfirmStep.Should().BeTrue();
        editor.StepBackCommand.Execute(null);
        editor.IsLinkPickStep.Should().BeTrue("Back from the confirmation returns to the pick step");
        editor.LinkNextCommand.Execute(null);
        editor.ConfirmLinkCommand.CanExecute(null).Should().BeTrue();
        editor.ConfirmLinkCommand.Execute(null);

        editor.IsFormStep.Should().BeTrue();
        editor.HasPendingLink.Should().BeTrue();
    }

    [Fact]
    public void The_task_editor_archive_restore_and_clear_commands_run_their_actions()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task"));
        using var editor = new TaskEditorViewModel(h.Workspace.Context, id, null);

        h.Await(editor.ArchiveCommand.ExecuteAsync(null));
        h.Settle();
        editor.IsArchived.Should().BeTrue();
        editor.RestoreCommand.CanExecute(null).Should().BeTrue();
        h.Await(editor.RestoreCommand.ExecuteAsync(null));
        h.Settle();
        editor.IsArchived.Should().BeFalse();

        editor.EndDate = DateTimeOffset.Now;
        editor.ClearEndDateCommand.Execute(null);
        editor.EndDate.Should().BeNull();
        editor.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void The_task_editor_follow_up_commands_close_for_their_follow_up()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task"));
        using var copy = new TaskEditorViewModel(h.Workspace.Context, id, null);
        var closed = 0;
        copy.CloseRequested += (_, _) => closed++;

        copy.RequestCopyCommand.Execute(null);

        copy.FollowUp.Should().Be(EditorFollowUp.Copy);
        closed.Should().Be(1);

        using var delete = new TaskEditorViewModel(h.Workspace.Context, id, null);
        delete.RequestDeleteCommand.Execute(null);
        delete.FollowUp.Should().Be(EditorFollowUp.Delete);

        using var card = new TaskEditorViewModel(h.Workspace.Context, id, null);
        card.RequestAddSubtaskCardCommand.Execute(null);
        card.FollowUp.Should().Be(EditorFollowUp.AddSubtaskCard);

        using var unlinked = new TaskEditorViewModel(h.Workspace.Context, id, null);
        unlinked.RequestOpenCrCommand.Execute(null);
        unlinked.FollowUp.Should().Be(EditorFollowUp.None, "an unlinked task has no CR to open, as before");
    }

    [Fact]
    public void The_cr_editor_delete_commands_open_the_step_and_back_leaves_it()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);

        editor.BeginDeleteCommand.Execute(null);
        editor.IsDeleteStep.Should().BeTrue();
        editor.StepBackCommand.Execute(null);
        editor.IsFormStep.Should().BeTrue();

        editor.BeginDeleteCommand.Execute(null);
        h.Await(editor.ConfirmDeleteCommand.ExecuteAsync(null));
        h.Settle();
        h.Store.ChangeRequests.Lookup(cr).HasValue.Should().BeFalse();
    }

    [Fact]
    public void The_cr_editor_clear_commands_clear_the_planned_dates()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);
        editor.PlannedStart = DateTimeOffset.Now;
        editor.PlannedEnd = DateTimeOffset.Now;

        editor.ClearPlannedStartCommand.Execute(null);
        editor.ClearPlannedEndCommand.Execute(null);

        editor.PlannedStart.Should().BeNull();
        editor.PlannedEnd.Should().BeNull();
    }

    [Fact]
    public void The_subtask_card_editor_follow_up_commands_close_for_their_follow_up()
    {
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s => card = await s.CardAsync(await s.TaskAsync("Parent"), "Card"));
        using var parent = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card);
        parent.RequestOpenParentCommand.Execute(null);
        parent.FollowUp.Should().Be(SubtaskEditorFollowUp.OpenParent);

        using var delete = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card);
        delete.RequestDeleteCommand.Execute(null);
        delete.FollowUp.Should().Be(SubtaskEditorFollowUp.Delete);
    }

    [Fact]
    public void The_review_editor_delete_command_closes_for_the_delete_follow_up()
    {
        var review = new ReviewItemId(Guid.NewGuid());
        using var h = WorkspaceHarness.Create(async s =>
            (await new CreateReview(s.Coordinator).ExecuteAsync(
                new CreateReviewCommand(OperationId.New(), review, new ReviewDraft("Review", null, null, [], null, null), null, false), default))
                .IsSuccess.Should().BeTrue());
        using var editor = new ReviewEditorViewModel(h.Workspace.Context, review);
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;

        editor.RequestDeleteCommand.Execute(null);

        editor.FollowUp.Should().Be(ReviewEditorFollowUp.Delete);
        closed.Should().Be(1);
    }
}
