using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Settings;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Common;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// What each "run one write command" site shows (C-05): its save-status result, what it announces, what it shows
/// inline, whether it waits until the commit is displayed, and how it retries. The sites run through one command
/// runner, and these tests pin that each kept its own visible behaviour, including where it differs from its siblings.
/// </summary>
[Collection(nameof(UiTasksConfiguration))]
[Trait("Req", "T-07")]
public sealed class CommandSiteTests
{
    private static readonly StorageFailureError DiskFull = new(StorageFailureKind.DiskFull);
    private const string DiskFullText = "Not saved. The drive is full.";

    // ----- Task and CR page commands: announce the blocked text and the failure text, Report the failure -----

    [Fact]
    public void A_task_archive_completes_the_indicator_and_announces_the_archived_task()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task"));
        var announced = Announcements(h);

        h.Await(h.Workspace.Context.Tasks.ArchiveAsync(h.Task(id))).Should().BeTrue();

        h.Workspace.SaveStatus.State.Should().Be(SaveState.Saved);
        announced.Should().Equal(new Announcement("Task_Archived", false));
    }

    [Fact]
    public void A_failed_task_archive_reports_and_announces_the_failure_text()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task"));
        var announced = Announcements(h);
        h.Coordinator.FailNext(DiskFull);

        h.Await(h.Workspace.Context.Tasks.ArchiveAsync(h.Task(id))).Should().BeFalse();

        h.Workspace.SaveStatus.State.Should().Be(SaveState.Failed);
        h.Workspace.SaveStatus.FailureMessage.Should().Be(DiskFullText);
        announced.Should().Equal(new Announcement(DiskFullText, true));
    }

    [Fact]
    public void A_refused_task_archive_is_rejected_so_the_indicator_keeps_its_last_result()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task"));
        var announced = Announcements(h);
        h.Coordinator.FailNext(new ValidationFailedError(new DomainError(DomainErrorCodes.Required, "Title")));

        h.Await(h.Workspace.Context.Tasks.ArchiveAsync(h.Task(id))).Should().BeFalse();

        h.Workspace.SaveStatus.State.Should().Be(SaveState.Idle, "Report rejects a validation refusal instead of failing");
        announced.Should().Equal(new Announcement("Field_Title is required.", true));
    }

    [Fact]
    public void A_task_command_while_editing_is_blocked_announces_why_and_sends_nothing()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task"));
        var announced = Announcements(h);
        var calls = h.Coordinator.Calls;
        h.Workspace.Context.IsEditingBlocked = true;

        h.Await(h.Workspace.Context.Tasks.ArchiveAsync(h.Task(id))).Should().BeFalse();

        h.Coordinator.Calls.Should().Be(calls);
        announced.Should().Equal(new Announcement("Error_EditingBlocked", true));
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Idle);
    }

    [Fact]
    [Trait("Req", "B-16.3")]
    public void A_pop_out_names_the_card_status_in_the_save_status_once_displayed_and_announces_it()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task", checklist: "Item"));
        h.Strings.With("PopOut_Moved", "Moved to a Subtask card in {0}").With("PopOut_Announced", "Popped out to {0}");
        var announced = Announcements(h);
        var task = h.Task(id);

        var card = h.Await(h.Workspace.Context.Tasks.PopOutAsync(task, task.ChecklistItems.Single().Id));

        card.Should().NotBeNull();
        h.Store.SubtaskCards.Lookup(card!.Value).HasValue.Should().BeTrue("the result is read once the commit is displayed");
        var status = h.Workspace.SaveStatus;
        status.State.Should().Be(SaveState.Saved);
        status.Text.Should().Be("Moved to a Subtask card in Backlog");
        status.ResultActionText.Should().Be("PopOut_OpenCard");
        announced.Should().Equal(new Announcement("Popped out to Backlog", false));
    }

    [Fact]
    [Trait("Req", "B-16.3")]
    public void A_failed_pop_out_reports_and_announces_the_failure_text()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task", checklist: "Item"));
        var announced = Announcements(h);
        var task = h.Task(id);
        h.Coordinator.FailNext(DiskFull);

        h.Await(h.Workspace.Context.Tasks.PopOutAsync(task, task.ChecklistItems.Single().Id)).Should().BeNull();

        h.Workspace.SaveStatus.State.Should().Be(SaveState.Failed);
        announced.Should().Equal(new Announcement(DiskFullText, true));
    }

    [Fact]
    [Trait("Req", "B-08.5")]
    public void A_failed_cr_schedule_reports_and_announces_the_failure_text()
    {
        ChangeRequestId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.CrAsync("CR-1", "First"));
        var announced = Announcements(h);
        h.Coordinator.FailNext(DiskFull);

        h.Await(h.Workspace.Context.ChangeRequests.ScheduleAsync(h.Store.ChangeRequests.Lookup(id).Value, new DateOnly(2026, 11, 20))).Should().BeFalse();

        h.Workspace.SaveStatus.State.Should().Be(SaveState.Failed);
        announced.Should().Equal(new Announcement(DiskFullText, true));
    }

    // ----- Board: Fail (not Report) and a composed announcement -----

    [Fact]
    [Trait("Req", "B-07")]
    public void A_refused_board_move_fails_the_indicator_where_other_sites_reject_it()
    {
        using var h = BoardMoveTests.Board(out var board, "Stays");
        var announced = Announcements(h);
        var card = board.Buckets[0].Cards.Single();
        h.Coordinator.FailNext(new ValidationFailedError(new DomainError(DomainErrorCodes.Required, "Title")));

        h.Await(board.MoveToAdjacentBucketAsync(card, 1)).Should().BeFalse();

        h.Workspace.SaveStatus.State.Should().Be(SaveState.Failed, "the Board ends every failed move with Fail");
        card.IsMoving.Should().BeFalse();
        announced.Should().Equal(new Announcement("Couldn't move Stays. It stays in Backlog. Field_Title is required.", true));
    }

    [Fact]
    [Trait("Req", "B-07")]
    public void A_board_move_while_editing_is_blocked_announces_why_and_sends_nothing()
    {
        using var h = BoardMoveTests.Board(out var board, "Stays");
        var announced = Announcements(h);
        var card = board.Buckets[0].Cards.Single();
        var calls = h.Coordinator.Calls;
        h.Workspace.Context.IsEditingBlocked = true;

        h.Await(board.MoveToAdjacentBucketAsync(card, 1)).Should().BeFalse();

        h.Coordinator.Calls.Should().Be(calls);
        card.IsMoving.Should().BeFalse();
        announced.Should().Equal(new Announcement("Error_EditingBlocked", true));
    }

    [Fact]
    [Trait("Req", "B-07")]
    public void A_board_move_is_announced_with_its_new_position_once_displayed()
    {
        using var h = BoardMoveTests.Board(out var board, "Moves");
        var announced = Announcements(h);
        var card = board.Buckets[0].Cards.Single();

        h.Await(board.MoveToAdjacentBucketAsync(card, 1)).Should().BeTrue();

        board.Buckets[1].Cards.Should().ContainSingle("the move returns once the commit is displayed");
        card.IsMoving.Should().BeFalse();
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Saved);
        announced.Should().Equal(new Announcement("Moved Moves to Blocked, position 1 of 1", false));
    }

    // ----- Grid: shown at the cell, never announced -----

    [Fact]
    [Trait("Req", "AC-07")]
    public void A_failed_grid_commit_is_shown_at_the_cell_and_not_announced()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Committed"));
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.Rows.Count == 1);
        var announced = Announcements(h);
        var row = grid.Rows.Single();
        grid.BeginEdit(row, GridColumns.Title);
        row.DraftText = "Draft";
        h.Coordinator.FailNext(DiskFull);

        h.Await(grid.CommitAsync(row)).Should().BeFalse();

        row.ErrorMessage.Should().Be(DiskFullText);
        row.IsSaving.Should().BeFalse();
        announced.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "T-05")]
    public void A_grid_commit_after_an_unknown_outcome_reuses_its_operation_for_the_same_draft()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Committed"));
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.Rows.Count == 1);
        var row = grid.Rows.Single();
        grid.BeginEdit(row, GridColumns.Title);
        row.DraftText = "Draft";
        h.Coordinator.FailNext(new CommitOutcomeUnknownError(OperationId.New()));
        h.Await(grid.CommitAsync(row)).Should().BeFalse();

        h.Await(grid.CommitAsync(row)).Should().BeTrue();

        h.Coordinator.OperationIds[^1].Should().Be(h.Coordinator.OperationIds[^2]);
    }

    // ----- Checklists: inline text, announce failures only, wait until displayed -----

    [Fact]
    [Trait("Req", "B-09")]
    public void A_checklist_toggle_while_editing_is_blocked_shows_why_inline_without_an_announcement()
    {
        using var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Task", "Service", null, "One"));
        var checklists = h.Workspace.Checklists;
        h.PumpUntil(() => checklists.Groups.Count == 1);
        var group = checklists.Groups.Single();
        var announced = Announcements(h);
        var calls = h.Coordinator.Calls;
        h.Workspace.Context.IsEditingBlocked = true;

        h.Await(group.ToggleAsync(group.OpenItems.Single(), true));

        group.ToggleError.Should().Be("Error_EditingBlocked");
        group.OpenItems.Single().IsPending.Should().BeFalse();
        h.Coordinator.Calls.Should().Be(calls);
        announced.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "B-09")]
    public void A_failed_checklist_toggle_announces_the_failure_text_once()
    {
        using var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Task", "Service", null, "One"));
        var checklists = h.Workspace.Checklists;
        h.PumpUntil(() => checklists.Groups.Count == 1);
        var group = checklists.Groups.Single();
        var announced = Announcements(h);
        h.Coordinator.FailNext(DiskFull);

        h.Await(group.ToggleAsync(group.OpenItems.Single(), true));

        announced.Should().Equal(new Announcement(DiskFullText, true));
    }

    [Fact]
    [Trait("Req", "B-09")]
    public void A_general_to_do_is_listed_when_adding_it_returns()
    {
        using var h = WorkspaceHarness.Create();
        var general = h.Workspace.Checklists.General;
        general.NewItemText = "Call the bank";

        h.Await(general.AddItemCommand.ExecuteAsync(null));

        general.OpenItems.Select(i => i.Text).Should().Equal("Call the bank");
        general.NewItemText.Should().BeEmpty();
        general.IsAdding.Should().BeFalse();
    }

    // ----- Settings: inline text, announce failures only, wait until displayed -----

    [Fact]
    [Trait("Req", "B-04")]
    public void A_settings_add_is_listed_when_it_returns_and_clears_the_box()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var section = settings.Tags;
        section.AddText = "Urgent";

        h.Await(settings.AddAsync(section));

        section.Rows.Select(r => r.Name).Should().Contain("Urgent", "Settings waits until the commit is displayed");
        (section.AddText, section.AddError, section.IsAdding).Should().Be((string.Empty, null, false));
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Saved);
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void A_failed_settings_add_shows_the_failure_inline_and_announces_it()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var section = settings.Tags;
        var announced = Announcements(h);
        section.AddText = "Urgent";
        h.Coordinator.FailNext(DiskFull);

        h.Await(settings.AddAsync(section));

        (section.AddText, section.AddError, section.IsAdding).Should().Be(("Urgent", DiskFullText, false));
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Failed);
        announced.Should().Equal(new Announcement(DiskFullText, true));
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void A_settings_add_while_editing_is_blocked_shows_why_inline_without_an_announcement()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var section = settings.Tags;
        var announced = Announcements(h);
        section.AddText = "Urgent";
        h.Workspace.Context.IsEditingBlocked = true;

        h.Await(settings.AddAsync(section));

        (section.AddError, section.IsAdding).Should().Be(("Error_EditingBlocked", false));
        announced.Should().BeEmpty();
    }

    // ----- Dialog and editor variants -----

    [Fact]
    [Trait("Req", "B-05")]
    public void A_failed_cr_delete_shows_the_failure_on_the_step_and_announces_it()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);
        var announced = Announcements(h);
        editor.BeginDeleteCommand.Execute(null);
        h.Coordinator.FailNext(DiskFull);

        h.Await(editor.ConfirmDeleteAsync()).Should().BeFalse();

        (editor.DeleteError, editor.IsSaving).Should().Be((DiskFullText, false));
        announced.Should().Equal(new Announcement(DiskFullText, true));
    }

    [Fact]
    [Trait("Req", "B-05")]
    public void A_cr_delete_that_throws_shows_the_unexpected_text_on_the_step_and_ends_saving()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);
        var announced = Announcements(h);
        editor.BeginDeleteCommand.Execute(null);
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(editor.ConfirmDeleteCommand.ExecuteAsync(null));

        (editor.DeleteError, editor.IsSaving).Should().Be(("Error_Unexpected", false));
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
    }

    [Fact]
    [Trait("Req", "B-16.3")]
    public void A_failed_subtask_card_add_in_the_task_editor_is_shown_below_the_box_and_not_announced()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task"));
        using var editor = new TaskEditorViewModel(h.Workspace.Context, id, null);
        var announced = Announcements(h);
        editor.NewSubtaskCardText = "Card";
        h.Coordinator.FailNext(DiskFull);

        h.Await(editor.AddSubtaskCardAsync()).Should().BeFalse();

        (editor.SubtaskCardError, editor.NewSubtaskCardText).Should().Be((DiskFullText, "Card"));
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Failed);
        announced.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "B-16.3")]
    public void A_subtask_card_add_in_the_task_editor_that_throws_shows_the_unexpected_text()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task"));
        using var editor = new TaskEditorViewModel(h.Workspace.Context, id, null);
        var announced = Announcements(h);
        editor.NewSubtaskCardText = "Card";
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(editor.AddSubtaskCardCommand.ExecuteAsync(null));

        (editor.SubtaskCardError, editor.NewSubtaskCardText).Should().Be(("Error_Unexpected", "Card"));
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
    }

    [Fact]
    [Trait("Req", "T-05")]
    public void A_copy_after_an_unknown_outcome_or_a_throw_is_retried_under_the_same_operation()
    {
        using var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Source"));
        var copy = new CopyTaskViewModel(h.Store, h.Workspace.Context.Commands, h.Workspace.Context, h.Strings, h.UseCases.Tasks, h.Store.Tasks.Items.Single());
        var announced = Announcements(h);

        h.Coordinator.FailNext(new CommitOutcomeUnknownError(OperationId.New()));
        h.Await(copy.CreateAsync()).Should().BeFalse();
        copy.Error.Should().Be("Error_CommitOutcomeUnknown");
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));
        h.Await(copy.CreateAsync()).Should().BeFalse();
        copy.Error.Should().Be("Error_Unexpected");
        copy.IsSaving.Should().BeFalse();
        h.Await(copy.CreateAsync()).Should().BeTrue();

        var operations = h.Coordinator.OperationIds.TakeLast(3).ToList();
        operations.Distinct().Should().ContainSingle("both the unknown outcome and the throw leave the outcome unknown");
        announced.Should().Equal(new Announcement("Error_CommitOutcomeUnknown", true), new Announcement("Error_Unexpected", true));
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void A_lookup_removal_that_throws_only_announces_and_retries_as_a_new_operation()
    {
        using var h = WorkspaceHarness.Create();
        var context = h.Workspace.Context;
        var workflow = h.Await(h.Dataset.Snapshots.LoadAsync(CancellationToken.None)).Value.Workflow;
        var unused = workflow.TaskStatuses.First(s => s.Id != workflow.Defaults.DefaultTaskStatusId);
        var vm = new LookupRemovalViewModel(context.Commands, context, context.Strings, SettingsUseCases.Create(h.Coordinator, h.Dataset.Snapshots), workflow, LookupKind.TaskStatus, unused.Id.Value, unused.Name.Display);
        h.Await(vm.LoadAsync());
        var announced = Announcements(h);

        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));
        h.Await(vm.RemoveAsync()).Should().BeFalse();

        announced.Should().Equal(new Announcement("Error_Unexpected", true));
        context.SaveStatus.IsFailed.Should().BeFalse("the removal dialog shows the failure itself and leaves the indicator as it was");
        context.SaveStatus.IsFailureNoticeOpen.Should().BeFalse();

        h.Await(vm.RemoveAsync()).Should().BeTrue();
        h.Coordinator.OperationIds[^1].Should().NotBe(h.Coordinator.OperationIds[^2], "the removal does not reuse the operation of a throw");
    }

    [Fact]
    [Trait("Req", "B-16.3")]
    public void A_subtask_card_add_in_the_task_editor_is_still_sent_while_editing_is_blocked()
    {
        // Pins a known T-07 defect awaiting the user's decision, not intended behaviour: this command should be refused
        // while editing is blocked, as every other write command is.
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task"));
        using var editor = new TaskEditorViewModel(h.Workspace.Context, id, null);
        editor.NewSubtaskCardText = "Card";
        var calls = h.Coordinator.Calls;
        h.Workspace.Context.IsEditingBlocked = true;

        h.Await(editor.AddSubtaskCardAsync()).Should().BeTrue("this command has never checked the blocked state (kept as it was)");

        h.Coordinator.Calls.Should().Be(calls + 1);
    }

    // ----- The approved fix: a thrown command no longer leaves "Saving…" or a busy flag behind, and is reported once -----

    [Fact]
    [Trait("Req", "ADR-0029")]
    public void A_task_archive_that_throws_ends_saving_and_is_reported_once()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Task"));
        var announced = Announcements(h);
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(h.Workspace.Context.Tasks.ArchiveAsync(h.Task(id))).Should().BeFalse();

        (h.Workspace.SaveStatus.IsSaving, h.Workspace.SaveStatus.IsFailed).Should().Be((false, true));
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
    }

    [Fact]
    [Trait("Req", "ADR-0029")]
    public void A_pop_out_that_throws_ends_saving_and_the_group_shows_the_unexpected_text()
    {
        using var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Task", "Service", null, "One"));
        var checklists = h.Workspace.Checklists;
        h.PumpUntil(() => checklists.Groups.Count == 1);
        var group = checklists.Groups.Single();
        var item = group.OpenItems.Single();
        var announced = Announcements(h);
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(item.PopOutCommand.ExecuteAsync(null));

        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
        item.IsPending.Should().BeFalse();
        group.ToggleError.Should().Be("Error_Unexpected");
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
    }

    [Fact]
    [Trait("Req", "ADR-0029")]
    public void A_board_move_that_throws_ends_moving_and_saving_and_is_reported_once()
    {
        using var h = BoardMoveTests.Board(out var board, "Stays");
        var announced = Announcements(h);
        var card = board.Buckets[0].Cards.Single();
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(board.MoveToAdjacentBucketAsync(card, 1)).Should().BeFalse();

        card.IsMoving.Should().BeFalse();
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
    }

    [Fact]
    [Trait("Req", "ADR-0029")]
    [Trait("Req", "T-05")]
    public void A_grid_commit_that_throws_keeps_the_editor_and_draft_and_retries_under_the_same_operation()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Committed"));
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.Rows.Count == 1);
        var announced = Announcements(h);
        var row = grid.Rows.Single();
        grid.BeginEdit(row, GridColumns.Title);
        row.DraftText = "Draft";
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(grid.CommitAsync(row)).Should().BeFalse();

        (row.IsSaving, row.IsEditingTitle, row.DraftText, row.ErrorMessage).Should().Be((false, true, "Draft", "Error_Unexpected"));
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
        announced.Should().Equal(new Announcement("Error_Unexpected", true));

        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Coordinator.OperationIds[^1].Should().Be(h.Coordinator.OperationIds[^2], "a throw leaves the outcome unknown (T-05)");
        h.Task(id).EffectiveTitle.Should().Be("Draft");
    }

    [Fact]
    [Trait("Req", "ADR-0029")]
    public void A_settings_add_that_throws_ends_adding_and_shows_the_unexpected_text_inline()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var section = settings.Tags;
        var announced = Announcements(h);
        section.AddText = "Urgent";
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(settings.AddAsync(section));

        (section.AddText, section.AddError, section.IsAdding).Should().Be(("Urgent", "Error_Unexpected", false));
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
    }

    [Fact]
    [Trait("Req", "ADR-0029")]
    public void A_checklist_add_that_throws_ends_adding_and_the_next_command_on_the_card_still_runs()
    {
        using var h = WorkspaceHarness.Create();
        var general = h.Workspace.Checklists.General;
        var announced = Announcements(h);
        general.NewItemText = "Call the bank";
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(general.AddItemCommand.ExecuteAsync(null));
        general.NewItemError.Should().Be("Error_Unexpected");
        general.IsAdding.Should().BeFalse();

        h.Await(general.AddItemCommand.ExecuteAsync(null));
        general.NewItemError.Should().BeNull();
        general.OpenItems.Select(i => i.Text).Should().Equal("Call the bank");
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
    }

    [Fact]
    public void A_failed_queued_action_does_not_stop_the_next_one_and_the_queue_stays_in_order()
    {
        using var h = WorkspaceHarness.Create();
        var queue = new Checklists.ChecklistCommandQueue(h.Workspace.Context.Commands, "Record_Task");
        var steps = new List<string>();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = queue.Enqueue(async () =>
        {
            await hold.Task;
            steps.Add("first");
            throw new InvalidOperationException("defect");
        });
        var second = queue.Enqueue(() =>
        {
            steps.Add("second");
            return Task.CompletedTask;
        });
        h.Settle();
        steps.Should().BeEmpty("the second action waits for the first");

        hold.SetResult();
        h.PumpUntil(() => second.IsCompleted);

        first.IsFaulted.Should().BeTrue("the failing action's own caller still sees its exception");
        second.IsCompletedSuccessfully.Should().BeTrue();
        steps.Should().Equal("first", "second");
    }

    // ----- Throws at the remaining sites, including a display wait that fails after the commit -----

    [Fact]
    [Trait("Req", "T-05")]
    public void An_editor_save_whose_display_wait_throws_stays_open_and_the_retry_is_recognised_as_committed()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);
        var closed = 0;
        editor.CloseRequested += (_, _) => closed++;
        var announced = Announcements(h);
        editor.Title = "Renamed";

        WithFailingDisplayWait(h, () => editor.SaveAsync()).Should().BeFalse();

        (editor.IsSaving, editor.SaveError, closed).Should().Be((false, "Error_Unexpected", 0));
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
        h.Settle();
        h.Store.ChangeRequests.Lookup(cr).Value.Title.Should().Be("Renamed", "the command committed before the wait failed");

        h.Await(editor.SaveAsync()).Should().BeTrue();
        h.Coordinator.OperationIds[^1].Should().Be(h.Coordinator.OperationIds[^2]);
        closed.Should().Be(1);
    }

    [Fact]
    [Trait("Req", "B-05")]
    public void A_cr_delete_whose_display_wait_throws_shows_the_unexpected_text_and_ends_saving()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Original"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);
        var announced = Announcements(h);
        editor.BeginDeleteCommand.Execute(null);

        WithFailingDisplayWait(h, () => editor.ConfirmDeleteAsync()).Should().BeFalse();

        (editor.DeleteError, editor.IsSaving).Should().Be(("Error_Unexpected", false));
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void A_lookup_removal_whose_display_wait_throws_is_reported_and_the_retry_reuses_the_operation()
    {
        using var h = WorkspaceHarness.Create();
        var context = h.Workspace.Context;
        var workflow = h.Await(h.Dataset.Snapshots.LoadAsync(CancellationToken.None)).Value.Workflow;
        var unused = workflow.TaskStatuses.First(s => s.Id != workflow.Defaults.DefaultTaskStatusId);
        var vm = new LookupRemovalViewModel(context.Commands, context, context.Strings, SettingsUseCases.Create(h.Coordinator, h.Dataset.Snapshots), workflow, LookupKind.TaskStatus, unused.Id.Value, unused.Name.Display);
        h.Await(vm.LoadAsync());
        var announced = Announcements(h);

        WithFailingDisplayWait(h, () => vm.RemoveAsync()).Should().BeFalse();

        (vm.IsSaving, vm.Error).Should().Be((false, "Error_Unexpected"));
        context.SaveStatus.IsFailed.Should().BeTrue("a failure after the commit is reported");
        announced.Should().Equal(new Announcement("Error_Unexpected", true));

        h.Await(vm.RemoveAsync()).Should().BeTrue();
        h.Coordinator.OperationIds[^1].Should().Be(h.Coordinator.OperationIds[^2]);
    }

    [Fact]
    [Trait("Req", "B-11")]
    public void A_field_save_whose_display_wait_throws_keeps_the_dialog_and_the_retry_reuses_the_operation()
    {
        using var h = WorkspaceHarness.Create();
        var workflow = h.Await(h.Dataset.Snapshots.LoadAsync(CancellationToken.None)).Value.Workflow;
        var vm = FieldDefinitionEditorViewModel.ForNew(h.Workspace.Context.Commands, h.Workspace.Context, h.Strings, SettingsUseCases.Create(h.Coordinator, h.Dataset.Snapshots), workflow, Domain.Fields.FieldTarget.Task);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.Label = "Cost centre";

        WithFailingDisplayWait(h, () => vm.SaveAsync()).Should().BeFalse();

        (vm.IsSaving, vm.SaveError, closed).Should().Be((false, "Error_Unexpected", false));
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();

        h.Await(vm.SaveAsync()).Should().BeTrue();
        h.Coordinator.OperationIds[^1].Should().Be(h.Coordinator.OperationIds[^2]);
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void A_settings_rename_that_throws_ends_the_rows_busy_state_and_keeps_the_rename_open()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var row = settings.TaskStatuses.Rows[0];
        row.IsRenaming = true;
        row.RenameText = "Renamed";
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(settings.CommitRenameAsync(row)).Should().BeFalse();

        (row.IsBusy, row.IsRenaming, row.RenameError).Should().Be((false, true, "Error_Unexpected"));
        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void A_settings_row_command_that_throws_ends_the_rows_busy_state_and_shows_the_page_notice()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var row = settings.TaskStatuses.Rows[0];
        var announced = Announcements(h);
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(settings.MoveAsync(row, 1));

        row.IsBusy.Should().BeFalse();
        settings.ErrorMessage.Should().Be("Error_Unexpected", "the page notice shows the text as well as the app notice");
        h.Workspace.SaveStatus.IsFailureNoticeOpen.Should().BeTrue();
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
    }

    [Fact]
    [Trait("Req", "B-08")]
    public void Calendar_cr_commands_that_throw_end_saving_and_are_each_reported_once()
    {
        ChangeRequestId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.CrAsync("CR-1", "First"));
        var commands = h.Workspace.Context.ChangeRequests;
        var announced = Announcements(h);
        var day = new DateOnly(2026, 11, 20);

        h.Coordinator.ThrowNext(new InvalidOperationException("schedule"));
        h.Await(commands.ScheduleAsync(h.Store.ChangeRequests.Lookup(id).Value, day)).Should().BeFalse();
        h.Coordinator.ThrowNext(new InvalidOperationException("resize"));
        h.Await(commands.ResizeAsync(h.Store.ChangeRequests.Lookup(id).Value, day, day)).Should().BeFalse();
        h.Coordinator.ThrowNext(new InvalidOperationException("backlog"));
        h.Await(commands.MoveToBacklogAsync(h.Store.ChangeRequests.Lookup(id).Value)).Should().BeFalse();

        h.Workspace.SaveStatus.IsSaving.Should().BeFalse();
        announced.Should().HaveCount(3).And.AllSatisfy(a => a.Should().Be(new Announcement("Error_Unexpected", true)));
    }

    [Fact]
    [Trait("Req", "B-17")]
    public void A_review_toggle_that_throws_ends_pending_restores_the_checkbox_and_shows_the_text()
    {
        using var h = WorkspaceHarness.Create(async s =>
            (await new Application.Reviews.CreateReview(s.Coordinator).ExecuteAsync(
                new Application.Reviews.CreateReviewCommand(OperationId.New(), new ReviewItemId(Guid.NewGuid()), new Application.Reviews.ReviewDraft("Review", null, null, [], null, null), null, false), default))
                .IsSuccess.Should().BeTrue());
        var reviews = h.Workspace.Checklists.Reviews;
        h.PumpUntil(() => reviews.Open.Count == 1);
        var row = reviews.Open.Single();
        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        var announced = Announcements(h);
        h.Coordinator.ThrowNext(new InvalidOperationException("defect"));

        h.Await(row.ToggleAsync(true));

        (row.IsPending, row.IsDone, reviews.ActionError).Should().Be((false, false, "Error_Unexpected"));
        notified.Should().Contain(nameof(row.IsDone), "the checkbox goes back to the committed value");
        announced.Should().Equal(new Announcement("Error_Unexpected", true));
    }

    /// <summary>Runs <paramref name="start"/> so its command commits and the wait until it is displayed then throws.</summary>
    private static T WithFailingDisplayWait<T>(WorkspaceHarness h, Func<Task<T>> start)
    {
        var hold = h.Coordinator.HoldNext();
        var calls = h.Coordinator.Calls;
        var task = start();
        h.PumpUntil(() => h.Coordinator.Calls > calls);
        h.Settle();
        h.StoreScheduler.ThrowOnNextScheduleFromThisThread();
        hold.SetResult();
        return h.Await(task);
    }

    private static SettingsPageViewModel Settings(WorkspaceHarness h)
    {
        var settings = new SettingsPageViewModel(h.Workspace.Context, new SettingsServices(SettingsUseCases.Create(h.Coordinator, h.Dataset.Snapshots), null, null, null));
        h.PumpUntil(() => settings.TaskStatuses.Rows.Count > 0);
        h.Settle();
        return settings;
    }

    private static List<Announcement> Announcements(WorkspaceHarness h)
    {
        var list = new List<Announcement>();
        h.Workspace.Context.Announced += (_, a) => list.Add(a);
        return list;
    }
}
