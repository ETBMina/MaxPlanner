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
        editor.UseSelectedCrCommand.CanExecute(null).Should().BeTrue();
        editor.UseSelectedCrCommand.Execute(null);

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

    [Fact]
    public void The_task_editor_pop_out_and_add_card_commands_create_subtask_cards()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task", checklist: "Item"));
        using var editor = new TaskEditorViewModel(h.Workspace.Context, id, null);

        h.Await(editor.PopOutCommand.ExecuteAsync(editor.ChecklistItems.Single()));
        h.Settle();
        h.Store.SubtaskCards.Count.Should().Be(1);

        editor.NewSubtaskCardText = "Card";
        h.Await(editor.AddSubtaskCardCommand.ExecuteAsync(null));
        h.Settle();
        h.Store.SubtaskCards.Count.Should().Be(2);
        editor.NewSubtaskCardText.Should().BeEmpty();
    }

    [Fact]
    public void A_real_editor_whose_save_throws_stays_open_with_its_draft_and_saves_on_retry()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;
        editor.Title = "Renamed";

        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));
        h.Await(editor.SaveCommand.ExecuteAsync(null));

        (editor.IsSaving, editor.IsDirty, editor.Title, closed).Should().Be((false, true, "Renamed", 0));
        editor.SaveError.Should().Be(h.Strings.GetString("Error_Unexpected"));
        h.Workspace.Context.SaveStatus.IsSaving.Should().BeFalse();

        h.Await(editor.SaveCommand.ExecuteAsync(null));
        h.Settle();
        closed.Should().Be(1);
        h.Store.ChangeRequests.Lookup(cr).Value.Title.Should().Be("Renamed");
    }

    [Fact]
    public void The_copy_task_commands_walk_link_review_and_create()
    {
        using var h = WorkspaceHarness.Create(async s =>
        {
            await s.TaskAsync("Source");
            await s.CrAsync("CR-1042", "Instant card freeze");
        });
        var copy = new CopyTaskViewModel(h.Workspace.Context, h.Store.Tasks.Items.Single());

        copy.BeginLinkCommand.Execute(null);
        copy.IsLinkPickStep.Should().BeTrue();
        copy.CrPicker.Selected = copy.CrPicker.Results.Single();
        copy.LinkNextCommand.Execute(null);
        copy.IsLinkConfirmStep.Should().BeTrue();
        copy.StepBackCommand.Execute(null);
        copy.IsLinkPickStep.Should().BeTrue();
        copy.LinkNextCommand.Execute(null);
        copy.Service = "Cards API";
        copy.UseSelectedCrCommand.Execute(null);
        copy.HasPendingLink.Should().BeTrue();
        copy.ClearLinkCommand.Execute(null);
        copy.HasPendingLink.Should().BeFalse();

        h.Await(copy.PrimaryCommand.ExecuteAsync(null));
        copy.IsReviewStep.Should().BeTrue();
        h.Await(copy.PrimaryCommand.ExecuteAsync(null));
        h.Settle();
        copy.CreatedTaskId.Should().NotBeNull();
        h.Store.Tasks.Items.Should().HaveCount(2);
    }

    [Fact]
    public void The_cr_editor_save_and_create_command_saves_then_closes_for_the_task()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);
        editor.Title = "Renamed";

        editor.BeginCreateTaskCommand.Execute(null);
        editor.IsSaveAndCreateStep.Should().BeTrue("a dirty draft is saved first");
        h.Await(editor.SaveAndCreateTaskCommand.ExecuteAsync(null));
        h.Settle();

        editor.FollowUp.Should().Be(CrEditorFollowUp.CreateTask);
        h.Store.ChangeRequests.Lookup(cr).Value.Title.Should().Be("Renamed");
    }

    [Fact]
    public void The_cr_editor_duplicate_backlog_and_url_commands_run_their_actions()
    {
        ChangeRequestId second = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            await s.CrAsync("CR-1", "First");
            second = await s.CrAsync("CR-2", "Second");
        });
        using var duplicate = new CrEditorViewModel(h.Workspace.Context, second);
        duplicate.Number = "CR-1";
        h.Await(duplicate.SaveAsync()).Should().BeFalse();
        duplicate.HasDuplicate.Should().BeTrue();
        duplicate.DiscardChanges();

        using var open = new CrEditorViewModel(h.Workspace.Context, second);
        open.Number = "CR-1";
        h.Await(open.SaveAsync()).Should().BeFalse();
        open.OpenDuplicateCommand.Execute(null);
        open.FollowUp.Should().Be(CrEditorFollowUp.OpenOtherCr);

        using var backlog = new CrEditorViewModel(h.Workspace.Context, second);
        backlog.RequestMoveToBacklogCommand.Execute(null);
        backlog.FollowUp.Should().Be(backlog.CanMoveToBacklog ? CrEditorFollowUp.MoveToBacklog : CrEditorFollowUp.None);

        using var url = new CrEditorViewModel(h.Workspace.Context, second);
        url.SolutionDesignUrl = "https://example.com/design";
        h.Await(url.OpenUrlCommand.ExecuteAsync(null));
        url.SaveError.Should().BeNull("opening a link changes nothing in the editor");
    }

    [Fact]
    public void The_conflict_commands_review_apply_and_reload_in_the_cr_and_subtask_card_editors()
    {
        ChangeRequestId cr = default;
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            cr = await s.CrAsync("CR-1", "Original");
            card = await s.CardAsync(await s.TaskAsync("Parent"), "Card");
        });

        using var mine = new CrEditorViewModel(h.Workspace.Context, cr);
        mine.Title = "Mine";
        using (var other = new CrEditorViewModel(h.Workspace.Context, cr))
        {
            other.Title = "Theirs";
            h.Await(other.SaveAsync()).Should().BeTrue();
            h.Settle();
        }

        h.Await(mine.SaveAsync()).Should().BeFalse();
        mine.ReviewConflictCommand.Execute(null);
        mine.IsConflictReviewStep.Should().BeTrue();
        mine.StepBackCommand.Execute(null);
        mine.BeginReloadCommand.Execute(null);
        mine.IsReloadConfirmStep.Should().BeTrue();
        mine.ConfirmReloadCommand.Execute(null);
        (mine.IsConflict, mine.Title).Should().Be((false, "Theirs"));

        using var cardMine = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card);
        cardMine.Subtitle = "Mine";
        using (var cardOther = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card))
        {
            cardOther.Subtitle = "Theirs";
            h.Await(cardOther.SaveAsync()).Should().BeTrue();
            h.Settle();
        }

        h.Await(cardMine.SaveAsync()).Should().BeFalse();
        cardMine.ReviewConflictCommand.Execute(null);
        cardMine.ApplyConflictReviewCommand.Execute(null);
        (cardMine.IsConflict, cardMine.Subtitle).Should().Be((false, "Mine"));
    }

    [Fact]
    public void The_subtask_card_clear_date_commands_clear_the_dates()
    {
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s => card = await s.CardAsync(await s.TaskAsync("Parent"), "Card"));
        using var editor = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card);
        editor.StartDate = DateTimeOffset.Now;
        editor.EndDate = DateTimeOffset.Now;

        editor.ClearStartDateCommand.Execute(null);
        editor.ClearEndDateCommand.Execute(null);

        (editor.StartDate, editor.EndDate).Should().Be((null, null));
    }

    [Fact]
    public void The_review_editor_reload_and_open_cr_commands_run_their_actions()
    {
        var review = new ReviewItemId(Guid.NewGuid());
        using var h = WorkspaceHarness.Create(async s =>
        {
            await s.CrAsync("CR-1", "Matching");
            (await new CreateReview(s.Coordinator).ExecuteAsync(
                new CreateReviewCommand(OperationId.New(), review, new ReviewDraft("Review", "CR-1", null, [], null, null), null, false), default))
                .IsSuccess.Should().BeTrue();
        });
        using var mine = new ReviewEditorViewModel(h.Workspace.Context, review);
        mine.Title = "Mine";
        using (var other = new ReviewEditorViewModel(h.Workspace.Context, review))
        {
            other.Title = "Theirs";
            h.Await(other.SaveAsync()).Should().BeTrue();
            h.Settle();
        }

        h.Await(mine.SaveAsync()).Should().BeFalse();
        mine.ReloadCommand.Execute(null);
        (mine.IsConflict, mine.Title).Should().Be((false, "Theirs"));

        mine.HasMatchingCr.Should().BeTrue();
        mine.OpenMatchingCrCommand.Execute(null);
        mine.FollowUp.Should().Be(ReviewEditorFollowUp.OpenCr);
    }

    [Fact]
    public void The_review_row_edit_command_opens_the_review_editor()
    {
        using var h = WorkspaceHarness.Create(async s =>
            (await new CreateReview(s.Coordinator).ExecuteAsync(
                new CreateReviewCommand(OperationId.New(), new ReviewItemId(Guid.NewGuid()), new ReviewDraft("Review", null, null, [], null, null), null, false), default))
                .IsSuccess.Should().BeTrue());
        using var dialogs = new TaskEditorTests.DialogRecorder(h.Workspace.Context);
        var reviews = h.Workspace.Checklists.Reviews;
        h.PumpUntil(() => reviews.Open.Count == 1);

        reviews.Open.Single().EditCommand.Execute(null);

        var editor = dialogs.Next<ReviewEditorViewModel>(h);
        editor.Title.Should().Be("Review");
        editor.DiscardChanges();
    }
}
