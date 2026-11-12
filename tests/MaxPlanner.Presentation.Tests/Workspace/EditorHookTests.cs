using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Presentation.Reviews;
using MaxPlanner.Presentation.SubtaskCards;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// Where the editors differ from the shared editor state machine, through the real use cases and SQLite: what
/// counts as a change to save, the failures only one editor knows, the CR's save-and-create step, the "created"
/// announcements, and the review editor's own validation display.
/// </summary>
[Trait("Req", "B-02.8")]
public sealed class EditorHookTests
{
    [Fact]
    [Trait("Req", "B-11")]
    public void A_clean_cr_still_saves_when_it_offers_to_make_its_owner_the_default()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var declined = new CrEditorViewModel(h.Workspace.Context, cr) { MakeDefaultOwner = false };
        var before = h.Coordinator.OperationIds.Count;
        h.Await(declined.SaveAsync()).Should().BeTrue();
        h.Coordinator.OperationIds.Count.Should().Be(before, "a clean CR without the offer just closes");

        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);
        editor.IsDirty.Should().BeFalse();
        editor.ShowMakeDefaultOwner.Should().BeTrue();
        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Settle();

        h.Coordinator.OperationIds.Count.Should().Be(before + 1, "B-11: making the owner the default is a change to save");
        h.Store.CurrentWorkflow!.Defaults.DefaultCrOwnerId.Should().Be(h.Store.ChangeRequests.Lookup(cr).Value.Owner.Id);
    }

    [Fact]
    [Trait("Req", "B-02.4")]
    public void A_clean_task_with_a_pending_link_still_saves()
    {
        TaskId task = default;
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            task = await s.TaskAsync("Own title", "Cards API");
            cr = await s.CrAsync("CR-1042", "Instant card freeze");
        });
        using var editor = new TaskEditorViewModel(h.Workspace.Context, task, initialStatus: null);
        LinkTo(editor, "1042");
        editor.IsDirty.Should().BeFalse("the link is not part of the draft values");
        var before = h.Coordinator.OperationIds.Count;

        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Settle();

        h.Coordinator.OperationIds.Count.Should().Be(before + 1);
        h.Task(task).Cr.ChangeRequestId.Should().Be(cr);
    }

    [Fact]
    [Trait("Req", "B-03.2")]
    public void A_duplicate_cr_number_is_shown_on_the_number_with_a_way_to_open_the_other_cr()
    {
        ChangeRequestId first = default;
        ChangeRequestId second = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            first = await s.CrAsync("CR-1", "First");
            second = await s.CrAsync("CR-2", "Second");
        });
        var announced = Record(h.Workspace.Context);
        using var editor = new CrEditorViewModel(h.Workspace.Context, second) { MakeDefaultOwner = false };
        string? focused = null;
        editor.FocusRequested += (_, key) => focused = key;

        editor.Number = "CR-1";
        h.Await(editor.SaveAsync()).Should().BeFalse();

        editor.NumberError.Should().Be("CrEditor_DuplicateNumber");
        editor.DuplicateId.Should().Be(first);
        editor.HasDuplicate.Should().BeTrue();
        editor.ValidationSummary.Should().Be("TaskEditor_FixField");
        focused.Should().Be("Number");
        editor.IsConflict.Should().BeFalse();
        editor.SaveError.Should().BeNull();
        announced.Should().ContainSingle(a => a.IsFailure).Which.Text.Should().Be("CrEditor_DuplicateNumber");
    }

    [Fact]
    [Trait("Req", "T-13")]
    public void A_pending_link_whose_cr_changed_before_save_is_refreshed_for_review_instead_of_a_conflict()
    {
        TaskId task = default;
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            task = await s.TaskAsync("Own title", "Cards API");
            cr = await s.CrAsync("CR-1042", "Instant card freeze");
        });
        using var editor = new TaskEditorViewModel(h.Workspace.Context, task, initialStatus: null);
        LinkTo(editor, "1042");
        using (var other = new CrEditorViewModel(h.Workspace.Context, cr) { MakeDefaultOwner = false })
        {
            other.Title = "Card freeze, renamed";
            h.Await(other.SaveAsync()).Should().BeTrue();
            h.Settle();
        }

        editor.PendingLink!.Title.Should().Be("Instant card freeze", "the preview is the CR the user confirmed");
        h.Await(editor.SaveAsync()).Should().BeFalse();

        editor.CrChangedMessage.Should().Be("TaskEditor_CrChangedMessage");
        editor.HasCrChangedMessage.Should().BeTrue();
        editor.PendingLink!.Title.Should().Be("Card freeze, renamed");
        editor.IsConflict.Should().BeFalse();
        editor.SaveError.Should().BeNull();

        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Settle();
        h.Task(task).Cr.ChangeRequestId.Should().Be(cr);
    }

    [Fact]
    [Trait("Req", "B-03.5")]
    public void Save_and_create_saves_the_new_cr_then_closes_once_for_the_task_editor()
    {
        using var h = WorkspaceHarness.Create();
        var context = h.Workspace.Context;
        var announced = Record(context);
        ChangeRequestId? reported = null;
        context.ChangeRequestSaved += (_, id) => reported = id;
        using var editor = new CrEditorViewModel(context, changeRequestId: null);
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;
        editor.Number = "CR-9";
        editor.Title = "New CR";
        editor.Owner.Query = "Dana";
        editor.Owner.SubmitQuery().Should().BeTrue();

        editor.BeginCreateTask();
        editor.IsSaveAndCreateStep.Should().BeTrue("a new CR is saved first");
        closed.Should().Be(0);
        h.Await(editor.SaveAndCreateTaskAsync()).Should().BeTrue();

        closed.Should().Be(1);
        editor.FollowUp.Should().Be(CrEditorFollowUp.CreateTask);
        editor.CreatedId.Should().NotBeNull();
        reported.Should().Be(editor.CreatedId);
        announced.Should().Contain(new Announcement("SaveStatus_Saved", false));
        editor.TryClose().Should().BeTrue();
    }

    [Fact]
    public void A_new_subtask_card_announces_it_was_created_with_its_subtitle()
    {
        TaskId parent = default;
        using var h = WorkspaceHarness.Create(async s => parent = await s.TaskAsync("Parent"));
        h.Strings.With("SubtaskCard_Created", "Created {0}");
        var announced = Record(h.Workspace.Context);
        using var editor = SubtaskCardEditorViewModel.ForNewCard(h.Workspace.Context, parent);

        editor.Subtitle = "  Fresh card ";
        h.Await(editor.SaveAsync()).Should().BeTrue();

        announced.Should().Contain(new Announcement("Created Fresh card", false));
        editor.CreatedCardId.Should().NotBeNull();
    }

    [Fact]
    public void A_new_review_announces_it_was_created_with_its_title()
    {
        using var h = WorkspaceHarness.Create();
        h.Strings.With("Reviews_Created", "Created {0}");
        var announced = Record(h.Workspace.Context);
        using var editor = new ReviewEditorViewModel(h.Workspace.Context, reviewId: null);

        editor.Title = "  New review ";
        h.Await(editor.SaveAsync()).Should().BeTrue();

        announced.Should().Contain(new Announcement("Created New review", false));
    }

    [Fact]
    public void The_review_editor_announces_its_summary_keeps_the_first_error_and_focuses_title_without_a_field()
    {
        var review = new ReviewItemId(Guid.NewGuid());
        using var h = WorkspaceHarness.Create(async s =>
            (await new CreateReview(s.Coordinator).ExecuteAsync(
                new CreateReviewCommand(OperationId.New(), review, new ReviewDraft("Review", null, null, [], null, null), null, false), default))
                .IsSuccess.Should().BeTrue());
        var announced = Record(h.Workspace.Context);
        using var editor = new ReviewEditorViewModel(h.Workspace.Context, review);
        var focused = new List<string>();
        editor.FocusRequested += (_, key) => focused.Add(key);

        editor.Comments = "Changed";
        h.Coordinator.FailNext(new ValidationFailedError(
            [new DomainError(DomainErrorCodes.Required, "Comments"), new DomainError(DomainErrorCodes.TooLong, "Comments", 10), new DomainError(DomainErrorCodes.Required)]));
        h.Await(editor.SaveAsync()).Should().BeFalse();

        editor.CommentsError.Should().Be("Field_Comments is required.", "the first error of a field wins");
        editor.SaveError.Should().Be("Field_Value is required.");
        editor.ValidationSummary.Should().Be("TaskEditor_FixFields", "two distinct fields, counting the one without a name");
        focused.Should().Equal("Comments");
        announced.Where(a => a.IsFailure).Should().Equal(new Announcement("TaskEditor_FixFields", true));

        h.Coordinator.FailNext(new ValidationFailedError([new DomainError(DomainErrorCodes.Required)]));
        h.Await(editor.SaveAsync()).Should().BeFalse();

        editor.CommentsError.Should().BeNull();
        editor.ValidationSummary.Should().Be("TaskEditor_FixField");
        focused.Should().Equal("Comments", "Title");
    }

    private static void LinkTo(TaskEditorViewModel editor, string query)
    {
        editor.BeginLink();
        editor.CrPicker.Query = query;
        editor.CrPicker.Selected = editor.CrPicker.Results.Single();
        editor.LinkNext();
        editor.ConfirmLink().Should().BeTrue();
    }

    private static List<Announcement> Record(WorkspaceContext context)
    {
        var announced = new List<Announcement>();
        context.Announced += (_, a) => announced.Add(a);
        return announced;
    }
}
