using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Presentation.Reviews;
using MaxPlanner.Presentation.SubtaskCards;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// The CR, Subtask card, and review editors through the real use cases and SQLite: each saves, asks before
/// closing a dirty draft, and keeps a stale draft as a conflict (B-02.8–9, design §5.8, §5.10). The task editor's
/// own versions of these are in <see cref="TaskEditorTests"/>.
/// </summary>
[Trait("Req", "B-02.9")]
public sealed class EditorParityTests
{
    [Fact]
    public void The_cr_editor_saves_the_draft_announces_it_and_reports_the_saved_cr()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        var context = h.Workspace.Context;
        var announced = Record(context);
        ChangeRequestId? reported = null;
        context.ChangeRequestSaved += (_, id) => reported = id;
        using var editor = new CrEditorViewModel(context, cr);
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;

        editor.Title = "Renamed";
        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Settle();

        h.Store.ChangeRequests.Lookup(cr).Value.Title.Should().Be("Renamed");
        closed.Should().Be(1);
        reported.Should().Be(cr);
        announced.Should().Contain(new Announcement("SaveStatus_Saved", false));
    }

    [Fact]
    [Trait("Req", "B-02.8")]
    public void Closing_a_dirty_cr_draft_asks_first_and_discarding_saves_nothing()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);
        editor.TryClose().Should().BeTrue("a clean draft closes at once");

        using var dirty = new CrEditorViewModel(h.Workspace.Context, cr);
        dirty.Title = "Changed";
        dirty.TryClose().Should().BeFalse();
        dirty.IsDirtyClosePromptOpen.Should().BeTrue();
        dirty.ContinueEditing();
        dirty.IsDirtyClosePromptOpen.Should().BeFalse();
        dirty.DiscardChanges();
        h.Settle();

        dirty.TryClose().Should().BeTrue();
        h.Store.ChangeRequests.Lookup(cr).Value.Title.Should().Be("Original");
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public void A_stale_cr_save_is_a_conflict_that_can_be_reviewed_and_reloaded()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var mine = new CrEditorViewModel(h.Workspace.Context, cr);
        mine.Title = "Mine";
        using (var other = new CrEditorViewModel(h.Workspace.Context, cr))
        {
            other.Title = "Theirs";
            SaveElsewhere(h, other.SaveAsync());
        }

        mine.Title.Should().Be("Mine", "a dirty draft is never replaced silently");

        h.Await(mine.SaveAsync()).Should().BeFalse();
        mine.IsConflict.Should().BeTrue();
        mine.SaveError.Should().Be("Editor_ConflictMessage");

        mine.ReviewConflict();
        mine.IsConflictReviewStep.Should().BeTrue();
        mine.ConflictRows.Select(r => (r.Key, r.Mine, r.Saved)).Should().Equal(("Title", "Mine", "Theirs"));
        mine.BeginReload();
        mine.IsReloadConfirmStep.Should().BeTrue();
        mine.ConfirmReload();

        mine.Title.Should().Be("Theirs");
        (mine.IsConflict, mine.IsDirty, mine.IsFormStep).Should().Be((false, false, true));
        mine.SaveError.Should().BeNull();
    }

    [Fact]
    [Trait("Req", "T-05")]
    public void A_cr_save_with_an_unknown_outcome_is_retried_under_the_same_operation()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);
        editor.Title = "Renamed";
        var before = h.Coordinator.OperationIds.Count;
        h.Coordinator.FailNext(new CommitOutcomeUnknownError(OperationId.New()));

        h.Await(editor.SaveAsync()).Should().BeFalse();
        editor.SaveError.Should().Be("Error_CommitOutcomeUnknown");
        h.Await(editor.SaveAsync()).Should().BeTrue();

        var sent = h.Coordinator.OperationIds.Skip(before).ToList();
        sent.Should().HaveCount(2);
        sent[1].Should().Be(sent[0], "T-05: the retry of an unknown outcome reuses its operation ID");
    }

    [Fact]
    public void The_subtask_card_editor_saves_the_draft_and_announces_it()
    {
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s => card = await s.CardAsync(await s.TaskAsync("Parent"), "Card"));
        var announced = Record(h.Workspace.Context);
        using var editor = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card);
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;

        editor.Subtitle = "Renamed";
        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Settle();

        h.Store.SubtaskCards.Lookup(card).Value.Subtitle.Should().Be("Renamed");
        closed.Should().Be(1);
        announced.Should().Contain(new Announcement("SaveStatus_Saved", false));
    }

    [Fact]
    [Trait("Req", "B-02.8")]
    public void Closing_a_dirty_subtask_card_draft_asks_first_and_discarding_saves_nothing()
    {
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s => card = await s.CardAsync(await s.TaskAsync("Parent"), "Card"));
        using var editor = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card);

        editor.Notes = "Changed";
        editor.TryClose().Should().BeFalse();
        editor.IsDirtyClosePromptOpen.Should().BeTrue();
        editor.RequestFollowUp(SubtaskEditorFollowUp.OpenParent);
        editor.FollowUp.Should().Be(SubtaskEditorFollowUp.OpenParent);
        editor.ContinueEditing();
        editor.FollowUp.Should().Be(SubtaskEditorFollowUp.None, "Continue editing drops the follow-up");
        editor.DiscardChanges();
        h.Settle();

        h.Store.SubtaskCards.Lookup(card).Value.Notes.Should().BeNullOrEmpty();
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public void A_stale_subtask_card_save_is_a_conflict_whose_review_keeps_the_chosen_values()
    {
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s => card = await s.CardAsync(await s.TaskAsync("Parent"), "Card"));
        using var mine = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card);
        mine.Subtitle = "Mine";
        using (var other = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card))
        {
            other.Subtitle = "Theirs";
            SaveElsewhere(h, other.SaveAsync());
        }

        mine.Subtitle.Should().Be("Mine");

        h.Await(mine.SaveAsync()).Should().BeFalse();
        mine.IsConflict.Should().BeTrue();
        mine.SaveError.Should().Be("Editor_ConflictMessage");

        mine.ReviewConflict();
        mine.ConflictRows.Select(r => (r.Key, r.Mine, r.Saved)).Should().Equal(("Subtitle", "Mine", "Theirs"));
        mine.ApplyConflictReview();
        (mine.IsConflict, mine.Subtitle).Should().Be((false, "Mine"));
        mine.SaveError.Should().BeNull();

        h.Await(mine.SaveAsync()).Should().BeTrue();
        h.Settle();
        h.Store.SubtaskCards.Lookup(card).Value.Subtitle.Should().Be("Mine");
    }

    [Fact]
    public void The_review_editor_saves_the_draft_and_announces_it()
    {
        var review = new ReviewItemId(Guid.NewGuid());
        using var h = WorkspaceHarness.Create(s => SeedReviewAsync(s, review));
        var announced = Record(h.Workspace.Context);
        using var editor = new ReviewEditorViewModel(h.Workspace.Context, review);
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;

        editor.Title = "Renamed";
        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Settle();

        h.Store.ReviewItems.Lookup(review).Value.Content.Title.Value.Should().Be("Renamed");
        closed.Should().Be(1);
        announced.Should().Contain(new Announcement("Reviews_Saved", false));
    }

    [Fact]
    [Trait("Req", "B-02.8")]
    public void Closing_a_dirty_review_draft_asks_first_and_discarding_saves_nothing()
    {
        var review = new ReviewItemId(Guid.NewGuid());
        using var h = WorkspaceHarness.Create(s => SeedReviewAsync(s, review));
        using var editor = new ReviewEditorViewModel(h.Workspace.Context, review);

        editor.Comments = "Changed";
        editor.TryCloseForWindow().Should().BeFalse();
        editor.IsDirtyClosePromptOpen.Should().BeTrue();
        editor.IsWindowClosePending.Should().BeTrue();
        editor.ContinueEditing();
        editor.IsWindowClosePending.Should().BeFalse();
        editor.DiscardChanges();
        h.Settle();

        h.Store.ReviewItems.Lookup(review).Value.Content.Comments.Should().BeNull();
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public void A_stale_review_save_is_a_conflict_that_reload_resolves()
    {
        var review = new ReviewItemId(Guid.NewGuid());
        using var h = WorkspaceHarness.Create(s => SeedReviewAsync(s, review));
        var announced = Record(h.Workspace.Context);
        using var mine = new ReviewEditorViewModel(h.Workspace.Context, review);
        mine.Title = "Mine";
        using (var other = new ReviewEditorViewModel(h.Workspace.Context, review))
        {
            other.Title = "Theirs";
            SaveElsewhere(h, other.SaveAsync());
        }

        mine.Title.Should().Be("Mine");

        h.Await(mine.SaveAsync()).Should().BeFalse();
        mine.IsConflict.Should().BeTrue();
        mine.SaveError.Should().Be("This Record_Review changed elsewhere.");
        announced.Should().Contain(new Announcement("This Record_Review changed elsewhere.", true));

        mine.Reload();
        (mine.IsConflict, mine.IsDirty, mine.Title).Should().Be((false, false, "Theirs"));
        mine.SaveError.Should().BeNull();
    }

    private static List<Announcement> Record(WorkspaceContext context)
    {
        var announced = new List<Announcement>();
        context.Announced += (_, a) => announced.Add(a);
        return announced;
    }

    /// <summary>Another editor saves first, and its commit reaches the open editors.</summary>
    private static void SaveElsewhere(WorkspaceHarness h, Task<bool> save)
    {
        h.Await(save).Should().BeTrue();
        h.Settle();
    }

    private static async Task SeedReviewAsync(WorkspaceHarness.Seeder s, ReviewItemId id) =>
        (await new CreateReview(s.Coordinator).ExecuteAsync(
            new CreateReviewCommand(OperationId.New(), id, new ReviewDraft("Review", null, null, [], null, null), null, false), default))
            .IsSuccess.Should().BeTrue();
}
