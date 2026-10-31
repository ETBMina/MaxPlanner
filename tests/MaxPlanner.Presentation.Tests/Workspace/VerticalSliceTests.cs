using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Tests.Testing;
using ViewState = MaxPlanner.Presentation.Workspace.ViewState;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// T-12 step 3: a Grid edit is validated, committed to SQLite, and reaches the Board and Checklists from the
/// shared committed change feed, incrementally (no reload, same item ViewModels); failures and conflicts keep
/// the draft and change nothing else (T-07, AC-06, AC-07).
/// </summary>
[Trait("Req", "T-07")]
[Trait("Req", "T-12")]
public sealed class VerticalSliceTests
{
    [Fact]
    [Trait("Req", "B-07")]
    [Trait("Req", "AC-06")]
    public void A_grid_title_edit_updates_the_board_card_and_the_checklist_group_incrementally()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Onboarding copy", "Mobile Banking", null, "Draft", "Review"));
        var (grid, board, checklists) = OpenPages(h);
        var row = Row(grid, task.Value);
        var card = BoardCard(board, task.Value);
        var group = checklists.Groups.Single();
        var sequence = h.Store.Sequence;

        grid.BeginEdit(row, GridColumns.Title).Should().BeTrue();
        row.DraftText = "Onboarding copy v2";
        var saved = h.Await(grid.CommitAsync(row));

        saved.Should().BeTrue();
        h.Settle();
        h.Store.Sequence.Should().Be(sequence + 1, "one Grid edit is one committed change set");
        row.IsEditing.Should().BeFalse();
        row.TitleText.Should().Be("Onboarding copy v2");
        (row.StartDateText, row.EndDateText).Should().Be((string.Empty, string.Empty), "a task without dates shows empty date cells");
        BoardCard(board, task.Value).Should().BeSameAs(card, "the card is updated in place, not rebuilt");
        card.Title.Should().Be("Onboarding copy v2");
        checklists.Groups.Single().Should().BeSameAs(group);
        group.Title.Should().Be("Onboarding copy v2");
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Saved);
    }

    [Fact]
    [Trait("Req", "B-07")]
    [Trait("Req", "AC-05")]
    public void A_grid_status_edit_moves_the_card_to_the_end_of_the_destination_column_and_applies_the_date_rule()
    {
        TaskId moving = default;
        TaskId staying = default;
        TaskId inProgressAlready = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            moving = await s.TaskAsync("Moving task");
            staying = await s.TaskAsync("Staying task");
            inProgressAlready = await s.TaskAsync("Started earlier");
        });
        var (grid, board, _) = OpenPages(h);
        var inProgress = board.Columns.Single(c => c.Name == "In Progress");
        h.External(u => u.MoveTask.ExecuteAsync(
            new MoveTaskCommand(OperationId.New(), inProgressAlready, 1, inProgress.StatusId, BoardPlacement.AppendToEnd), default));
        var backlog = board.Columns.Single(c => c.Name == "Backlog");
        var stayingCard = BoardCard(board, staying.Value);
        var row = Row(grid, moving.Value);

        grid.BeginEdit(row, GridColumns.Status).Should().BeTrue();
        row.DraftStatus = grid.StatusOptions.Single(o => o.Name == "In Progress");
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();

        backlog.Cards.Select(c => c.Key).Should().Equal(staying.Value);
        backlog.Cards.Single().Should().BeSameAs(stayingCard, "cards that did not change keep their ViewModel");
        inProgress.Cards.Select(c => c.Key).Should().Equal(inProgressAlready.Value, moving.Value);
        inProgress.CountText.Should().Be("2");
        row.StatusText.Should().Be("In Progress");
        row.StartDateText.Should().NotBeEmpty("B-04: moving to a start-setting status sets the start date");
    }

    [Fact]
    [Trait("Req", "B-16.2")]
    public void A_subtask_card_subtitle_edit_updates_its_board_card()
    {
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            var parent = await s.TaskAsync("Parent task");
            card = await s.CardAsync(parent, "Copy fix");
        });
        var (grid, board, _) = OpenPages(h);
        var row = Row(grid, card.Value);
        row.CanEdit(GridColumns.Title).Should().BeFalse("a Subtask card shows its parent's title read-only");
        row.CanEdit(GridColumns.Service).Should().BeFalse();

        grid.BeginEdit(row, GridColumns.Subtitle).Should().BeTrue();
        row.DraftText = "Copy fix, second pass";
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();

        BoardCard(board, card.Value).Title.Should().Be("Copy fix, second pass");
    }

    [Fact]
    [Trait("Req", "B-02.3")]
    public void A_linked_task_title_is_locked_and_names_its_cr()
    {
        TaskId linked = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            var cr = await s.CrAsync("CR-1042", "Instant card freeze");
            linked = await s.TaskAsync(null, "Cards API", cr);
        });
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.Rows.Count == 1);
        var row = grid.Rows.Single();

        row.IsTitleLocked.Should().BeTrue();
        row.TitleText.Should().Be("Instant card freeze");
        row.CanEdit(GridColumns.Title).Should().BeFalse();
        grid.BeginEdit(row, GridColumns.Title).Should().BeFalse();
        row.CanEdit(GridColumns.Service).Should().BeTrue();
    }

    [Fact]
    [Trait("Req", "AC-07")]
    [Trait("Req", "T-06.7")]
    public void A_failed_commit_keeps_the_editor_and_draft_and_changes_no_page()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Committed title", "Mobile Banking", null, "Item"));
        var (grid, board, checklists) = OpenPages(h);
        var row = Row(grid, task.Value);
        var sequence = h.Store.Sequence;
        grid.BeginEdit(row, GridColumns.Title);
        row.DraftText = "Draft that must survive";

        h.Coordinator.FailNext(new StorageFailureError(StorageFailureKind.DiskFull));
        var saved = h.Await(grid.CommitAsync(row));
        h.Settle();

        saved.Should().BeFalse();
        row.IsEditingTitle.Should().BeTrue("the editor stays open on failure");
        row.DraftText.Should().Be("Draft that must survive");
        row.ErrorMessage.Should().Be("Not saved. The drive is full.");
        row.TitleText.Should().Be("Committed title");
        BoardCard(board, task.Value).Title.Should().Be("Committed title");
        checklists.Groups.Single().Title.Should().Be("Committed title");
        h.Store.Sequence.Should().Be(sequence, "nothing was committed or published");
        var status = h.Workspace.SaveStatus;
        status.State.Should().Be(SaveState.Failed);
        status.IsFailureNoticeOpen.Should().BeTrue("a storage failure opens the App InfoBar");
        status.FailureMessage.Should().Be("Not saved. The drive is full.");
        status.FailureDetails.Should().StartWith("Operation ");

        // The retained draft is saved once storage works again.
        h.Await(grid.CommitAsync(row)).Should().BeTrue();
        h.Settle();
        row.TitleText.Should().Be("Draft that must survive");
        status.State.Should().Be(SaveState.Saved);
        status.IsFailureNoticeOpen.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "B-02.2")]
    public void A_blank_title_is_refused_with_an_inline_error_and_no_save_failure()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Committed title"));
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.Rows.Count == 1);
        var row = grid.Rows.Single();
        var sequence = h.Store.Sequence;

        grid.BeginEdit(row, GridColumns.Title);
        row.DraftText = "   ";
        h.Await(grid.CommitAsync(row)).Should().BeFalse();

        row.IsEditingTitle.Should().BeTrue();
        row.ErrorMessage.Should().Be("Field_Title is required.");
        h.Settle();
        h.Store.Sequence.Should().Be(sequence, "the ownership rule rolled the transaction back");
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Idle, "nothing was attempted against the data");
    }

    [Fact]
    [Trait("Req", "AC-07")]
    [Trait("Req", "B-02.9")]
    public void A_stale_revision_never_overwrites_the_newer_title_and_use_mine_commits_the_kept_draft()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Original title"));
        var grid = h.Workspace.Grid;
        var board = h.Workspace.Board;
        h.PumpUntil(() => grid.Rows.Count == 1 && board.State == ViewState.Populated);
        var row = grid.Rows.Single();
        grid.BeginEdit(row, GridColumns.Title);
        row.DraftText = "Grid draft";

        // Another editor renames the task after this cell started editing.
        h.External(u => u.UpdateTaskContent.ExecuteAsync(new UpdateTaskContentCommand(OperationId.New(), task, 1,
            new TaskContentDraft("Renamed elsewhere", "Mobile Banking", null, null, [], null, CustomValue.NoValues)), default));
        h.Await(grid.CommitAsync(row)).Should().BeFalse();
        h.Settle();

        row.HasConflict.Should().BeTrue();
        row.ErrorMessage.Should().Be("This Record_Task changed elsewhere.");
        row.DraftText.Should().Be("Grid draft", "the draft is never replaced silently");
        row.ConflictSavedText.Should().Be("Renamed elsewhere");
        row.TitleText.Should().Be("Renamed elsewhere");
        h.Task(task).EffectiveTitle.Should().Be("Renamed elsewhere");
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Failed);
        h.Workspace.SaveStatus.IsFailureNoticeOpen.Should().BeFalse("a conflict is explained at the cell, not in the App InfoBar");

        h.Await(grid.UseMineAsync(row)).Should().BeTrue();
        h.Settle();
        row.IsEditing.Should().BeFalse();
        h.Task(task).EffectiveTitle.Should().Be("Grid draft");
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public void Reload_after_a_conflict_discards_the_draft_and_shows_the_saved_value()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Original title"));
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.Rows.Count == 1);
        var row = grid.Rows.Single();
        grid.BeginEdit(row, GridColumns.Service);
        row.DraftText = "Grid service";
        h.External(u => u.UpdateTaskContent.ExecuteAsync(new UpdateTaskContentCommand(OperationId.New(), task, 1,
            new TaskContentDraft("Original title", "Saved service", null, null, [], null, CustomValue.NoValues)), default));
        h.Await(grid.CommitAsync(row)).Should().BeFalse();

        grid.Reload(row);

        row.IsEditing.Should().BeFalse();
        row.HasConflict.Should().BeFalse();
        row.ServiceText.Should().Be("Saved service");
        h.Task(task).Service.Should().Be("Saved service");
    }

    [Fact]
    [Trait("Req", "ADR-0004")]
    public void The_editor_stays_open_and_saving_while_the_command_runs()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Original title"));
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.Rows.Count == 1);
        var row = grid.Rows.Single();
        grid.BeginEdit(row, GridColumns.Title);
        row.DraftText = "Held title";
        var hold = h.Coordinator.HoldNext();

        var commit = grid.CommitAsync(row);
        h.Settle();

        commit.IsCompleted.Should().BeFalse();
        row.IsSaving.Should().BeTrue();
        row.IsEditingTitle.Should().BeTrue();
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Saving);
        grid.CancelEdit(row);
        row.IsEditingTitle.Should().BeTrue("Esc is ignored while the outcome is unknown");

        hold.SetResult();
        h.Await(commit).Should().BeTrue();
        row.TitleText.Should().Be("Held title");
    }

    [Fact]
    [Trait("Req", "B-02.1")]
    public void New_task_creates_a_task_in_the_default_status_and_requests_focus_on_its_title_editor()
    {
        using var h = WorkspaceHarness.Create();
        var grid = h.Workspace.Grid;
        h.PumpUntil(() => grid.State == ViewState.Empty);
        GridFocusRequest? request = null;
        grid.FocusRequested += (_, r) => request = r;

        grid.NewTaskCommand.Execute(null);
        h.PumpUntil(() => request is not null);

        request!.Column.Should().Be(GridColumns.Title);
        request.BeginEdit.Should().BeTrue();
        request.Row.TitleText.Should().Be("Grid_NewTaskTitle");
        request.Row.StatusText.Should().Be("Backlog");
        request.Row.IsEditingTitle.Should().BeTrue();
        grid.State.Should().Be(ViewState.Populated);
    }

    private static (GridPageViewModel Grid, BoardPageViewModel Board, Checklists.ChecklistsPageViewModel Checklists) OpenPages(WorkspaceHarness h)
    {
        var grid = h.Workspace.Grid;
        var board = h.Workspace.Board;
        var checklists = h.Workspace.Checklists;
        h.PumpUntil(() => grid.State != ViewState.Loading
            && board.State == ViewState.Populated
            && checklists.State != ViewState.Loading);
        h.Settle();
        return (grid, board, checklists);
    }

    private static GridRowViewModel Row(GridPageViewModel grid, Guid id) => grid.Rows.Single(r => r.Key == id);

    private static BoardCardViewModel BoardCard(BoardPageViewModel board, Guid id) => board.Columns.SelectMany(c => c.Cards).Single(c => c.Key == id);
}
