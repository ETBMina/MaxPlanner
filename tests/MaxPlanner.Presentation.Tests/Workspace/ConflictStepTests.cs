using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Presentation.SubtaskCards;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// The conflict steps of the task, CR, and Subtask card editors (design §5.10, B-02.9) through the real use cases and
/// SQLite: review then apply keeps the chosen values, reload discards the draft after the confirmation, and Back from
/// either step returns to the form with the draft and the conflict kept.
/// </summary>
[Trait("Req", "B-02.9")]
public sealed class ConflictStepTests
{
    [Fact]
    public void The_task_editor_reviews_then_applies_reloads_and_steps_back()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Original", "Mobile Banking"));
        using var mine = new TaskEditorViewModel(h.Workspace.Context, id, initialStatus: null);
        mine.Title = "Mine";
        h.External(u => u.Tasks.UpdateTaskContent.ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), id, 1, new TaskContentDraft("Theirs", "Cards API", null, null, [], null, CustomValue.NoValues)),
            TestContext.Current.CancellationToken));
        h.Await(mine.SaveAsync()).Should().BeFalse();
        mine.IsConflict.Should().BeTrue();

        mine.ReviewConflict();
        mine.IsConflictReviewStep.Should().BeTrue();
        mine.ConflictRows.Should().NotBeEmpty();
        mine.StepBack();
        (mine.IsFormStep, mine.IsConflict, mine.Title).Should().Be((true, true, "Mine"), "Back from the review keeps the draft and the conflict");
        mine.BeginReload();
        mine.IsReloadConfirmStep.Should().BeTrue();
        mine.StepBack();
        (mine.IsFormStep, mine.IsConflict, mine.Title).Should().Be((true, true, "Mine"), "Back from the reload confirmation keeps the draft");

        mine.ReviewConflict();
        mine.ConflictRows.Select(r => r.Key).Should().BeEquivalentTo(["Title", "Service"]);
        mine.ConflictRows.Single(r => r.Key == "Service").UseMine = false;
        mine.ApplyConflictReview();
        (mine.IsConflict, mine.IsFormStep, mine.Title, mine.Service).Should().Be((false, true, "Mine", "Cards API"));
        mine.ConflictRows.Should().BeEmpty();
        mine.SaveError.Should().BeNull();
        mine.IsDirty.Should().BeTrue("the kept value still differs from the saved revision");
    }

    [Fact]
    public void The_task_editor_reload_confirm_discards_the_draft()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Original"));
        using var mine = new TaskEditorViewModel(h.Workspace.Context, id, initialStatus: null);
        mine.Title = "Mine";
        h.External(u => u.Tasks.UpdateTaskContent.ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), id, 1, new TaskContentDraft("Theirs", "Mobile Banking", null, null, [], null, CustomValue.NoValues)),
            TestContext.Current.CancellationToken));
        h.Await(mine.SaveAsync()).Should().BeFalse();

        mine.ReviewConflict();
        mine.BeginReload();
        mine.IsReloadConfirmStep.Should().BeTrue();
        mine.ConfirmReload();

        (mine.Title, mine.IsConflict, mine.IsDirty, mine.IsFormStep).Should().Be(("Theirs", false, false, true));
        mine.SaveError.Should().BeNull();
    }

    [Fact]
    public void The_cr_editor_reviews_then_applies_reloads_and_steps_back()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var mine = new CrEditorViewModel(h.Workspace.Context, cr);
        mine.Title = "Mine";
        mine.EmailSubject = "My subject";
        using (var other = new CrEditorViewModel(h.Workspace.Context, cr))
        {
            other.Title = "Theirs";
            other.EmailSubject = "Their subject";
            h.Await(other.SaveAsync()).Should().BeTrue();
            h.Settle();
        }

        h.Await(mine.SaveAsync()).Should().BeFalse();
        mine.IsConflict.Should().BeTrue();

        mine.ReviewConflict();
        mine.IsConflictReviewStep.Should().BeTrue();
        mine.ConflictRows.Should().NotBeEmpty();
        mine.StepBack();
        (mine.IsFormStep, mine.IsConflict, mine.Title).Should().Be((true, true, "Mine"), "Back from the review keeps the draft and the conflict");
        mine.BeginReload();
        mine.IsReloadConfirmStep.Should().BeTrue();
        mine.StepBack();
        (mine.IsFormStep, mine.IsConflict, mine.Title).Should().Be((true, true, "Mine"), "Back from the reload confirmation keeps the draft");

        mine.ReviewConflict();
        mine.ConflictRows.Select(r => r.Key).Should().BeEquivalentTo(["Title", "EmailSubject"]);
        mine.ConflictRows.Single(r => r.Key == "EmailSubject").UseMine = false;
        mine.ApplyConflictReview();
        (mine.IsConflict, mine.IsFormStep, mine.Title, mine.EmailSubject).Should().Be((false, true, "Mine", "Their subject"));
        mine.ConflictRows.Should().BeEmpty();
        mine.SaveError.Should().BeNull();

        h.Await(mine.SaveAsync()).Should().BeTrue();
        h.Settle();
        var saved = h.Store.ChangeRequests.Lookup(cr).Value;
        (saved.Title, saved.EmailSubject).Should().Be(("Mine", "Their subject"));
    }

    [Fact]
    public void The_subtask_card_editor_reviews_reloads_and_steps_back()
    {
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s => card = await s.CardAsync(await s.TaskAsync("Parent"), "Card"));
        using var mine = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card);
        mine.Subtitle = "Mine";
        using (var other = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card))
        {
            other.Subtitle = "Theirs";
            h.Await(other.SaveAsync()).Should().BeTrue();
            h.Settle();
        }

        h.Await(mine.SaveAsync()).Should().BeFalse();
        mine.IsConflict.Should().BeTrue();

        mine.ReviewConflict();
        mine.IsConflictReviewStep.Should().BeTrue();
        mine.ConflictRows.Should().NotBeEmpty();
        mine.StepBack();
        (mine.IsFormStep, mine.IsConflict, mine.Subtitle).Should().Be((true, true, "Mine"), "Back from the review keeps the draft and the conflict");
        mine.BeginReload();
        mine.IsReloadConfirmStep.Should().BeTrue();
        mine.StepBack();
        (mine.IsFormStep, mine.IsConflict, mine.Subtitle).Should().Be((true, true, "Mine"), "Back from the reload confirmation keeps the draft");

        mine.BeginReload();
        mine.IsReloadConfirmStep.Should().BeTrue();
        mine.ConfirmReload();
        (mine.Subtitle, mine.IsConflict, mine.IsDirty, mine.IsFormStep).Should().Be(("Theirs", false, false, true));
        mine.SaveError.Should().BeNull();
    }

    [Fact]
    public void Review_does_nothing_without_a_conflict()
    {
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s => card = await s.CardAsync(await s.TaskAsync("Parent"), "Card"));
        using var editor = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card);
        editor.Subtitle = "Changed";

        editor.ReviewConflict();

        editor.IsFormStep.Should().BeTrue();
        editor.ConflictRows.Should().BeEmpty();
    }
}
