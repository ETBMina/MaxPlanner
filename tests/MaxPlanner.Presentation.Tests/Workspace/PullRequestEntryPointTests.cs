using AwesomeAssertions;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.PullRequests;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.SubtaskCards;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>Design §5.11 entry points: the menus, the editors' follow-ups, and the dialog from saved values.</summary>
[Trait("Req", "B-18")]
public sealed class PullRequestEntryPointTests
{
    private static readonly UiPreferences On = new()
    {
        AzureDevOps = new AzureDevOpsPreferences
        {
            Enabled = true,
            CollectionUrl = "https://server/value",
            Project = "ace_bank",
            Repository = "ace_repo",
        },
    };

    [Fact]
    public void The_Board_card_menu_has_Create_pull_request_above_Delete_only_while_the_integration_is_on()
    {
        BoardCardMenuRules.ItemsFor(isTask: true, hasCrReference: true, isPullRequestAvailable: true).Should().Equal(
            BoardCardMenuItem.Open, BoardCardMenuItem.OpenCr, BoardCardMenuItem.MoveTo, BoardCardMenuItem.MoveUp, BoardCardMenuItem.MoveDown,
            BoardCardMenuItem.Separator, BoardCardMenuItem.Copy, BoardCardMenuItem.CopyId, BoardCardMenuItem.AddSubtaskCard, BoardCardMenuItem.Archive,
            BoardCardMenuItem.CreatePullRequest, BoardCardMenuItem.Separator, BoardCardMenuItem.Delete);

        BoardCardMenuRules.ItemsFor(isTask: false, hasCrReference: false, isPullRequestAvailable: true).Should().Equal(
            BoardCardMenuItem.Open, BoardCardMenuItem.OpenParent, BoardCardMenuItem.MoveTo, BoardCardMenuItem.MoveUp, BoardCardMenuItem.MoveDown,
            BoardCardMenuItem.Separator, BoardCardMenuItem.CopyId, BoardCardMenuItem.CreatePullRequest, BoardCardMenuItem.Separator, BoardCardMenuItem.Delete);

        BoardCardMenuRules.ItemsFor(isTask: true, hasCrReference: false, isPullRequestAvailable: false).Should().NotContain(BoardCardMenuItem.CreatePullRequest);
        BoardCardMenuRules.ItemsFor(isTask: false, hasCrReference: true, isPullRequestAvailable: false).Should().NotContain(BoardCardMenuItem.CreatePullRequest);
    }

    [Fact]
    public void The_Grid_row_menu_has_Create_pull_request_before_Delete_only_while_the_integration_is_on()
    {
        TaskId task = default;
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            task = await s.TaskAsync("Task");
            card = await s.CardAsync(task, "Card");
        });
        var taskRow = WorkItemRecord.From(h.Task(task));
        var cardRow = WorkItemRecord.From(h.Store.SubtaskCards.Lookup(card).Value);

        GridMenus.RowMenuItemsFor(taskRow, isPullRequestAvailable: true).Should().EndWith([GridRowMenuItem.Archive, GridRowMenuItem.CreatePullRequest, GridRowMenuItem.Delete]);
        GridMenus.RowMenuItemsFor(cardRow, isPullRequestAvailable: true).Should().Equal(GridRowMenuItem.Open, GridRowMenuItem.CopyId, GridRowMenuItem.CreatePullRequest, GridRowMenuItem.Delete);
        GridMenus.RowMenuItemsFor(taskRow, isPullRequestAvailable: false).Should().NotContain(GridRowMenuItem.CreatePullRequest);
    }

    [Fact]
    public void The_editors_offer_the_item_only_while_the_integration_is_on()
    {
        TaskId task = default;
        using var off = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Task"));
        using (var editor = new TaskEditorViewModel(off.Workspace.Context, task, null))
        {
            editor.IsPullRequestAvailable.Should().BeFalse("the integration is off by default");
        }

        using var on = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Task"), On);
        using var onEditor = new TaskEditorViewModel(on.Workspace.Context, task, null);
        onEditor.IsPullRequestAvailable.Should().BeTrue();
    }

    [Fact]
    public void The_editor_follow_ups_close_for_Create_pull_request_except_for_a_new_record()
    {
        TaskId task = default;
        SubtaskCardId card = default;
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(
            async s =>
            {
                task = await s.TaskAsync("Task");
                card = await s.CardAsync(task, "Card");
                cr = await s.CrAsync("CR-1", "CR");
            },
            On);
        var context = h.Workspace.Context;

        using var taskEditor = new TaskEditorViewModel(context, task, null);
        var closed = 0;
        taskEditor.CloseRequested += (_, _) => closed++;
        taskEditor.RequestCreatePullRequestCommand.Execute(null);
        taskEditor.FollowUp.Should().Be(EditorFollowUp.CreatePullRequest);
        closed.Should().Be(1);

        using var newTask = new TaskEditorViewModel(context, null, null);
        newTask.RequestCreatePullRequestCommand.Execute(null);
        newTask.FollowUp.Should().Be(EditorFollowUp.None);

        using var cardEditor = SubtaskCardEditorViewModel.ForCard(context, card);
        cardEditor.RequestCreatePullRequestCommand.Execute(null);
        cardEditor.FollowUp.Should().Be(SubtaskEditorFollowUp.CreatePullRequest);

        using var newCard = SubtaskCardEditorViewModel.ForNewCard(context, task);
        newCard.RequestCreatePullRequestCommand.Execute(null);
        newCard.FollowUp.Should().Be(SubtaskEditorFollowUp.None);

        using var crEditor = new CrEditorViewModel(context, cr);
        crEditor.RequestCreatePullRequestCommand.Execute(null);
        crEditor.FollowUp.Should().Be(CrEditorFollowUp.CreatePullRequest);

        using var newCr = new CrEditorViewModel(context, null);
        newCr.RequestCreatePullRequestCommand.Execute(null);
        newCr.FollowUp.Should().Be(CrEditorFollowUp.None);
    }

    [Fact]
    public void A_dirty_editor_asks_first_and_the_dialog_uses_the_saved_values()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Saved title"), On);
        using var editor = new TaskEditorViewModel(h.Workspace.Context, task, null);
        editor.Title = "Unsaved title";

        editor.RequestCreatePullRequestCommand.Execute(null);

        editor.IsDirtyClosePromptOpen.Should().BeTrue("the dirty-close confirmation comes first (design §5.8)");
    }

    [Fact]
    public void The_dialog_opens_with_the_saved_CR_numbers_and_Open_Settings_asks_for_Settings()
    {
        ChangeRequestId cr = default;
        TaskId task = default;
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(
            async s =>
            {
                cr = await s.CrAsync("CR-7", "Card limits");
                task = await s.TaskAsync(null, cr: cr);
                card = await s.CardAsync(task, "Fix copy");
            },
            On,
            AzureDevOpsTesting.Services(new FakeAzureDevOpsClient(), new FakeTokenStore(null)));
        var context = h.Workspace.Context;
        var shown = new List<IDialogViewModel>();
        context.DialogRequested += (_, request) =>
        {
            shown.Add(request.ViewModel);
            if (request.ViewModel is CreatePullRequestViewModel dialog)
            {
                // No token: the dialog explains it and offers Open Settings.
                dialog.OpenSettingsCommand.Execute(null);
            }

            request.MarkClosed();
        };
        var settingsRequests = 0;
        context.AzureDevOpsSettingsRequested += (_, _) => settingsRequests++;

        h.Await(context.PullRequests.CreateForSubtaskCardAsync(card));
        h.Await(context.PullRequests.CreateForChangeRequestAsync(cr));

        var fromCard = (CreatePullRequestViewModel)shown[0];
        fromCard.SourceBranch.Should().Be("0042", "a Subtask card uses its parent's CR task number");
        fromCard.Title.Should().Be("PR CR-7 0042 into uat");
        fromCard.AccessibleName.Should().Be("PullRequest_DialogName");
        fromCard.IsSetupIncomplete.Should().BeTrue();
        ((CreatePullRequestViewModel)shown[1]).SourceBranch.Should().Be("0042");
        settingsRequests.Should().Be(2);
    }
}
