using AwesomeAssertions;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// Phase 4 task editor and its routes, through the real use cases and SQLite (B-02, B-05, B-06, T-13): draft,
/// validation, dirty close, conflict review, link-once, copy, archive, and delete. Dialogs are answered by the
/// test in place of the shell's host.
/// </summary>
[Trait("Req", "B-02")]
public sealed class TaskEditorTests
{
    [Fact]
    [Trait("Req", "B-02.1")]
    [Trait("Req", "B-02.6")]
    [Trait("Req", "B-02.9")]
    public void A_column_plus_opens_a_new_draft_in_that_status_that_validates_then_creates_with_a_new_person()
    {
        using var h = BoardMoveTests.Board(out var board, "Existing");
        using var dialogs = new DialogRecorder(h.Workspace.Context);
        var review = board.Columns[3];

        var flow = board.NewTaskAsync(review);
        var editor = dialogs.Next<TaskEditorViewModel>(h);
        editor.IsNew.Should().BeTrue();
        editor.SelectedStatus!.Id.Should().Be(review.StatusId);

        h.Await(editor.SaveAsync()).Should().BeFalse();
        editor.TitleError.Should().Be("Field_Title is required.");
        editor.ValidationSummary.Should().Be("TaskEditor_FixField");
        h.Store.Tasks.Count.Should().Be(1, "nothing is created by a refused draft");

        editor.Title = "Brand new";
        editor.Designers.Query = "Dana";
        editor.Designers.SubmitQuery().Should().BeTrue();
        editor.Designers.Tokens.Single().IsNew.Should().BeTrue();
        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Await(flow);
        h.Settle();

        var created = h.Task(editor.CreatedTaskId!.Value);
        created.Status.Id.Should().Be(review.StatusId);
        created.Designers.Single().Name.Should().Be("Dana", "B-02.6: the new person is created by the save");
        board.Columns[3].Cards.Select(c => c.Title).Should().Contain("Brand new");
    }

    [Fact]
    [Trait("Req", "B-02.8")]
    public void Closing_a_dirty_draft_asks_first_and_discarding_creates_nothing()
    {
        using var h = BoardMoveTests.Board(out var board, "Task");
        using var dialogs = new DialogRecorder(h.Workspace.Context);
        var card = board.Columns[0].Cards.Single();
        var people = h.Store.CurrentWorkflow!.People.Count;

        var flow = board.OpenAsync(card);
        var editor = dialogs.Next<TaskEditorViewModel>(h);
        editor.IsDirty.Should().BeFalse();
        editor.TryClose().Should().BeTrue("a clean draft closes at once");
        dialogs.CloseCurrent();
        h.Await(flow);

        flow = board.OpenAsync(card);
        editor = dialogs.Next<TaskEditorViewModel>(h);
        editor.Title = "Changed";
        editor.Tags.Query = "Brand-new tag";
        editor.Tags.SubmitQuery();
        editor.IsDirty.Should().BeTrue();

        editor.TryClose().Should().BeFalse();
        editor.IsDirtyClosePromptOpen.Should().BeTrue();
        editor.ContinueEditing();
        editor.IsDirtyClosePromptOpen.Should().BeFalse();
        editor.TryClose().Should().BeFalse();
        editor.DiscardChanges();
        h.Await(flow);
        h.Settle();

        card.Title.Should().Be("Task");
        h.Store.CurrentWorkflow!.Tags.Should().BeEmpty("B-02.6: Cancel must not create lookups");
        h.Store.CurrentWorkflow!.People.Count.Should().Be(people);
    }

    [Fact]
    [Trait("Req", "AC-07")]
    [Trait("Req", "B-02.9")]
    public void A_stale_save_is_a_conflict_that_can_be_reviewed_and_applied_without_losing_the_draft()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Original", "Mobile Banking"));
        var board = BoardMoveTests.Ready(h);
        using var dialogs = new DialogRecorder(h.Workspace.Context);
        var flow = board.OpenAsync(board.Columns[0].Cards.Single());
        var editor = dialogs.Next<TaskEditorViewModel>(h);
        editor.Title = "Mine";
        editor.Notes = "My notes";

        // Another route saves first.
        h.External(u => u.UpdateTaskContent.ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), id, 1, new TaskContentDraft("Theirs", "Cards API", [], [], [], null, CustomValue.NoValues)),
            TestContext.Current.CancellationToken));
        editor.Title.Should().Be("Mine", "a dirty draft is never replaced silently");

        h.Await(editor.SaveAsync()).Should().BeFalse();
        editor.IsConflict.Should().BeTrue();
        editor.SaveError.Should().Be("TaskEditor_ConflictMessage");

        editor.ReviewConflict();
        editor.IsConflictReviewStep.Should().BeTrue();
        editor.ConflictRows.Select(r => (r.Key, r.Mine, r.Saved)).Should().BeEquivalentTo(
            [("Title", "Mine", "Theirs"), ("Service", "Mobile Banking", "Cards API"), ("Notes", "My notes", "Common_None")]);
        editor.ConflictRows.Single(r => r.Key == "Service").UseMine = false;
        editor.ApplyConflictReview();

        editor.IsConflict.Should().BeFalse();
        editor.Title.Should().Be("Mine");
        editor.Service.Should().Be("Cards API");
        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Await(flow);
        h.Settle();
        var saved = h.Task(id);
        (saved.EffectiveTitle, saved.Service, saved.Notes, saved.Revision).Should().Be(("Mine", "Cards API", "My notes", 3L));
    }

    [Fact]
    [Trait("Req", "B-02.9")]
    public void Reload_discards_the_draft_only_after_confirmation()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Original"));
        var board = BoardMoveTests.Ready(h);
        using var dialogs = new DialogRecorder(h.Workspace.Context);
        _ = board.OpenAsync(board.Columns[0].Cards.Single());
        var editor = dialogs.Next<TaskEditorViewModel>(h);
        editor.Title = "Mine";
        h.External(u => u.UpdateTaskContent.ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), id, 1, new TaskContentDraft("Theirs", "Mobile Banking", [], [], [], null, CustomValue.NoValues)),
            TestContext.Current.CancellationToken));
        h.Await(editor.SaveAsync());

        editor.BeginReload();
        editor.Title.Should().Be("Mine");
        editor.ConfirmReload();

        editor.Title.Should().Be("Theirs");
        editor.IsDirty.Should().BeFalse();
        editor.IsConflict.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "AC-06")]
    public void A_clean_editor_follows_a_checklist_toggle_saved_elsewhere()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task", "Service", null, "Draft"));
        var board = BoardMoveTests.Ready(h);
        using var dialogs = new DialogRecorder(h.Workspace.Context);
        _ = board.OpenAsync(board.Columns[0].Cards.Single());
        var editor = dialogs.Next<TaskEditorViewModel>(h);
        var item = h.Task(id).ChecklistItems.Single();

        h.External(u => u.SetChecklistItemCompleted.ExecuteAsync(
            new SetChecklistItemCompletedCommand(OperationId.New(), id, 2, item.Id, true), TestContext.Current.CancellationToken));

        editor.ChecklistItems.Single().IsCompleted.Should().BeTrue();
        editor.IsDirty.Should().BeFalse();
        editor.CanPopOut.Should().BeTrue();
        editor.Notes = "now dirty";
        editor.CanPopOut.Should().BeFalse("pop out runs immediately, so it is off while the draft has changes");
    }

    [Fact]
    [Trait("Req", "B-02.4")]
    [Trait("Req", "AC-28")]
    [Trait("Req", "AC-29")]
    public void Linking_needs_the_permanence_step_and_a_service_and_is_offered_only_once()
    {
        TaskId id = default;
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            id = await s.TaskAsync("Own title", service: "");
            cr = await s.CrAsync("CR-1042", "Instant card freeze");
        });
        var board = BoardMoveTests.Ready(h);
        using var dialogs = new DialogRecorder(h.Workspace.Context);
        var flow = board.OpenAsync(board.Columns[0].Cards.Single());
        var editor = dialogs.Next<TaskEditorViewModel>(h);
        editor.CanLinkToCr.Should().BeTrue();

        editor.BeginLink();
        editor.CrPicker.Query = "1042";
        editor.CrPicker.Results.Should().ContainSingle();
        editor.CrPicker.Selected = editor.CrPicker.Results.Single();
        editor.LinkNext();
        editor.IsLinkConfirmStep.Should().BeTrue();
        editor.ConfirmLink().Should().BeFalse("D10: a link needs a Service first");
        editor.ServiceError.Should().Be("Error_ServiceRequiredWhenLinked");
        editor.Service = "Cards API";
        editor.ConfirmLink().Should().BeTrue();
        editor.HasPendingLink.Should().BeTrue();
        editor.IsTitleEditable.Should().BeFalse();
        editor.InheritedTitle.Should().Be("Instant card freeze");

        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Await(flow);
        h.Settle();
        var linked = h.Task(id);
        linked.Cr.ChangeRequestId.Should().Be(cr);
        linked.EffectiveTitle.Should().Be("Instant card freeze");

        flow = board.OpenAsync(board.Columns[0].Cards.Single());
        editor = dialogs.Next<TaskEditorViewModel>(h);
        editor.IsLinked.Should().BeTrue();
        editor.CanLinkToCr.Should().BeFalse("AC-29: no route offers a second link");
        editor.LinkedCrNumber.Should().Be("CR-1042");
        editor.BeginLink();
        editor.IsFormStep.Should().BeTrue();
        editor.DiscardChanges();
        h.Await(flow);
    }

    [Fact]
    [Trait("Req", "B-06")]
    [Trait("Req", "AC-09")]
    public void Copy_from_the_board_menu_reviews_then_creates_an_unlinked_copy_in_the_kept_status()
    {
        using var h = BoardMoveTests.Board(out var board, "Source");
        using var dialogs = new DialogRecorder(h.Workspace.Context);
        h.Await(board.MoveToAdjacentColumnAsync(board.Columns[0].Cards.Single(), 1));
        h.Settle();
        var source = board.Columns[1].Cards.Single();

        var flow = board.CopyAsync(source);
        var copy = dialogs.Next<CopyTaskViewModel>(h);
        copy.Title.Should().Be("Source");
        copy.Service.Should().BeEmpty();
        copy.SelectedStatus!.Id.Should().Be(board.Columns[0].StatusId, "B-06: the default status unless Keep current status");
        copy.KeepCurrentStatus = true;
        copy.SelectedStatus!.Id.Should().Be(board.Columns[1].StatusId);

        h.Await(copy.PrimaryAsync()).Should().BeFalse();
        copy.IsReviewStep.Should().BeTrue();
        copy.ReviewLines.Should().Contain("Copy_Unlinked");
        h.Await(copy.PrimaryAsync()).Should().BeTrue();
        h.Await(flow);
        h.Settle();

        var created = h.Task(copy.CreatedTaskId!.Value);
        created.Status.Id.Should().Be(board.Columns[1].StatusId);
        created.Cr.ChangeRequestId.Should().BeNull();
        board.Columns[1].Cards.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Req", "B-05")]
    public void Delete_names_the_content_and_offers_archive_instead()
    {
        using var h = WorkspaceHarness.Create(async s =>
        {
            var task = await s.TaskAsync("Doomed", "Service", null, "One", "Two");
            await s.CardAsync(task, "Card");
            await s.TaskAsync("Kept");
        });
        var board = BoardMoveTests.Ready(h);
        h.Strings.With("DeleteTask_Message", "Delete '{0}'? {1} items, {2} cards.");
        using var dialogs = new DialogRecorder(h.Workspace.Context) { Answer = ConfirmationResult.Secondary };
        var kept = board.Columns[0].Cards.Single(c => c.Title == "Kept");

        h.Await(board.DeleteAsync(kept)).Should().BeTrue();
        h.Settle();
        board.Columns[0].Cards.Should().NotContain(c => c.Title == "Kept", "Archive instead hides it from the Board");
        h.Store.Tasks.Items.Single(t => t.EffectiveTitle == "Kept").IsArchived.Should().BeTrue();

        dialogs.Answer = ConfirmationResult.Primary;
        var doomed = board.Columns[0].Cards.Single(c => c.Title == "Doomed");
        h.Await(board.DeleteAsync(doomed)).Should().BeTrue();
        h.Settle();

        dialogs.Shown.OfType<ConfirmationViewModel>().Last().Message.Should().Be("Delete 'Doomed'? 2 items, 1 cards.");
        board.Columns[0].Cards.Should().BeEmpty("the task and its Subtask card are gone");
        h.Store.Tasks.Items.Should().ContainSingle();
        h.Store.SubtaskCards.Count.Should().Be(0);
    }

    [Fact]
    [Trait("Req", "B-05")]
    public void Archive_from_the_editor_makes_it_read_only_and_restore_brings_it_back()
    {
        using var h = BoardMoveTests.Board(out var board, "Task");
        using var dialogs = new DialogRecorder(h.Workspace.Context);
        var flow = board.OpenAsync(board.Columns[0].Cards.Single());
        var editor = dialogs.Next<TaskEditorViewModel>(h);

        h.Await(editor.ArchiveAsync()).Should().BeTrue();
        h.Settle();
        editor.IsArchived.Should().BeTrue();
        editor.CanEditFields.Should().BeFalse();
        editor.CanSave.Should().BeFalse();
        board.Columns[0].Cards.Should().BeEmpty();

        h.Await(editor.RestoreAsync()).Should().BeTrue();
        h.Settle();
        editor.IsArchived.Should().BeFalse();
        board.Columns[0].Cards.Should().ContainSingle();
        editor.TryClose().Should().BeTrue();
        dialogs.CloseCurrent();
        h.Await(flow);
    }

    [Fact]
    [Trait("Req", "B-16.3")]
    public void A_subtask_card_added_in_the_editor_appears_in_its_list_and_on_the_board()
    {
        using var h = BoardMoveTests.Board(out var board, "Parent");
        using var dialogs = new DialogRecorder(h.Workspace.Context);
        var flow = board.OpenAsync(board.Columns[0].Cards.Single());
        var editor = dialogs.Next<TaskEditorViewModel>(h);

        editor.NewSubtaskCardText = "Follow-up";
        h.Await(editor.AddSubtaskCardAsync()).Should().BeTrue();
        h.Settle();

        editor.SubtaskCards.Select(c => c.Subtitle).Should().Equal("Follow-up");
        editor.IsDirty.Should().BeFalse("cards are created immediately, not drafted");
        board.Columns[0].Cards.Should().Contain(c => c.IsSubtaskCard && c.Title == "Follow-up");
        editor.TryClose().Should().BeTrue();
        dialogs.CloseCurrent();
        h.Await(flow);
    }

    [Fact]
    [Trait("Req", "B-02.7")]
    public void Checklist_edits_are_part_of_the_draft_and_saved_together()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task", "Service", null, "First", "Second"));
        var board = BoardMoveTests.Ready(h);
        using var dialogs = new DialogRecorder(h.Workspace.Context);
        var flow = board.OpenAsync(board.Columns[0].Cards.Single());
        var editor = dialogs.Next<TaskEditorViewModel>(h);

        editor.NewChecklistText = "   ";
        editor.AddChecklistItem().Should().BeFalse();
        editor.ChecklistError.Should().NotBeNull();
        editor.NewChecklistText = "Third";
        editor.AddChecklistItem().Should().BeTrue();
        editor.MoveChecklistItem(editor.ChecklistItems[2], -2);
        editor.RemoveChecklistItem(editor.ChecklistItems.Single(i => i.Text == "Second"));
        editor.ChecklistItems[1].Text = "First, renamed";
        h.Store.Tasks.Lookup(id).Value.ChecklistTotal.Should().Be(2, "nothing is saved before Save");

        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Await(flow);
        h.Settle();
        h.Task(id).ChecklistItems.Select(i => i.Text).Should().Equal("Third", "First, renamed");
    }

    /// <summary>Answers dialog requests in place of the shell's host; each dialog's close request completes it.</summary>
    private sealed class DialogRecorder : IDisposable
    {
        private readonly WorkspaceContext _context;
        private int _taken;

        public DialogRecorder(WorkspaceContext context)
        {
            _context = context;
            context.DialogRequested += OnRequested;
        }

        public List<IDialogViewModel> Shown { get; } = [];

        public ConfirmationResult? Answer { get; set; }

        public T Next<T>(WorkspaceHarness h)
            where T : IDialogViewModel
        {
            h.PumpUntil(() => Shown.Count > _taken);
            return (T)Shown[_taken++];
        }

        public void Dispose() => _context.DialogRequested -= OnRequested;

        /// <summary>The host closes the dialog after the ViewModel allowed it (Esc or Cancel on a clean draft).</summary>
        public void CloseCurrent() => _requests[^1].MarkClosed();

        private readonly List<DialogRequest> _requests = [];

        private void OnRequested(object? sender, DialogRequest request)
        {
            _requests.Add(request);
            Shown.Add(request.ViewModel);
            request.ViewModel.CloseRequested += (_, _) => request.MarkClosed();
            if (request.ViewModel is ConfirmationViewModel confirmation && Answer is { } answer)
            {
                _taken++;
                confirmation.Choose(answer);
            }
        }
    }
}
