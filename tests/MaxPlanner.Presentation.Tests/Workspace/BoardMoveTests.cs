using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// Phase 3 Board moves through the real use cases and SQLite (B-07, B-16.5, AC-05): drag/drop placement,
/// keyboard moves, the filtered-append rule, and failure rollback. A card moves only after its commit.
/// </summary>
[Trait("Req", "B-07")]
public sealed class BoardMoveTests
{
    [Fact]
    public void Dropping_inside_a_column_reorders_by_rank_and_announces_the_new_position()
    {
        using var h = Board(out var board, "A", "B", "C");
        var announcements = Announcements(h);
        var backlog = board.Columns[0];
        var c = backlog.Cards[2];

        h.Await(board.DropAsync(c, backlog, 0));
        h.Settle();

        Titles(backlog).Should().Equal("C", "A", "B");
        announcements.Should().Contain(a => a.Text == "Moved C to Backlog, position 1 of 3" && !a.IsFailure);
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Saved);
    }

    [Fact]
    [Trait("Req", "AC-05")]
    [Trait("Req", "B-04")]
    public void Dropping_into_a_start_setting_column_moves_the_card_at_the_drop_slot_and_sets_its_start_date()
    {
        TaskId moving = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            moving = await s.TaskAsync("Moving");
        });
        var board = Ready(h);
        var inProgress = board.Columns[2];
        h.External(u => u.CreateTask.ExecuteAsync(new(OperationId.New(), new TaskId(Guid.NewGuid()), Draft("First"), inProgress.StatusId), TestContext.Current.CancellationToken));
        h.External(u => u.CreateTask.ExecuteAsync(new(OperationId.New(), new TaskId(Guid.NewGuid()), Draft("Second"), inProgress.StatusId), TestContext.Current.CancellationToken));
        var card = board.Columns[0].Cards.Single();

        h.Await(board.DropAsync(card, inProgress, 1));
        h.Settle();

        Titles(inProgress).Should().Equal("First", "Moving", "Second");
        board.Columns[0].Cards.Should().BeEmpty();
        h.Task(moving).StartDate.Should().Be(Records.Today, "B-04: a transition to a start-setting status fills StartDate");
    }

    [Fact]
    public void Keyboard_moves_append_to_the_adjacent_status_and_reorder_within_the_column()
    {
        using var h = Board(out var board, "A", "B");
        var a = board.Columns[0].Cards[0];

        h.Await(board.MoveWithinColumnAsync(a, 1));
        h.Settle();
        Titles(board.Columns[0]).Should().Equal("B", "A");

        h.Await(board.MoveToAdjacentColumnAsync(board.Columns[0].Cards[1], 1));
        h.Settle();
        Titles(board.Columns[0]).Should().Equal("B");
        Titles(board.Columns[1]).Should().Equal("A");

        board.MoveToAdjacentColumnAsync(board.Columns[0].Cards[0], -1).IsCompletedSuccessfully.Should().BeTrue("there is no column before the first");
    }

    [Fact]
    [Trait("Req", "B-16.5")]
    public void A_subtask_card_moves_to_the_top_of_another_column_without_touching_its_parent()
    {
        TaskId parent = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            parent = await s.TaskAsync("Parent");
            await s.CardAsync(parent, "Follow-up");
        });
        var board = Ready(h);
        var review = board.Columns[3];
        h.External(u => u.CreateTask.ExecuteAsync(new(OperationId.New(), new TaskId(Guid.NewGuid()), Draft("Already in review"), review.StatusId), TestContext.Current.CancellationToken));
        var card = board.Columns[0].Cards.Single(c => c.IsSubtaskCard);
        var parentRevision = h.Task(parent).Revision;

        h.Await(board.MoveToColumnAsync(card, review, BoardEdge.Top));
        h.Settle();

        Titles(review).Should().Equal("Follow-up", "Already in review");
        h.Task(parent).Revision.Should().Be(parentRevision, "B-16.4: moving a Subtask card never changes its parent");
    }

    [Fact]
    [Trait("Req", "B-07")]
    public void While_searching_a_status_move_appends_to_the_full_column_and_reordering_is_refused()
    {
        using var h = Board(out var board, "Match one", "Hidden", "Match two");
        h.External(u => u.CreateTask.ExecuteAsync(new(OperationId.New(), new TaskId(Guid.NewGuid()), Draft("Hidden blocker"), board.Columns[1].StatusId), TestContext.Current.CancellationToken));
        var announcements = Announcements(h);
        board.SearchText = "match";
        h.Settle();

        board.IsFiltered.Should().BeTrue();
        board.CanReorder.Should().BeFalse();
        board.Columns[0].CountText.Should().Be("2 of 3", "B-07: visible / total active while filtered");
        var first = board.Columns[0].Cards[0];
        board.CanMoveDown(first).Should().BeFalse();

        var calls = h.Coordinator.Calls;
        h.Await(board.DropAsync(first, board.Columns[0], 2)).Should().BeFalse();
        h.Coordinator.Calls.Should().Be(calls, "positional reordering is off while filtered");
        announcements.Should().Contain(a => a.IsFailure && a.Text == "Board_ReorderOff");

        // A drop at the top of another column still appends to the end of the full column, after the hidden card.
        h.Await(board.DropAsync(first, board.Columns[1], 0)).Should().BeTrue();
        board.SearchText = string.Empty;
        h.Settle();
        Titles(board.Columns[1]).Should().Equal("Hidden blocker", "Match one");
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public void A_failed_move_leaves_the_card_in_its_committed_column_and_reports_the_failure()
    {
        using var h = Board(out var board, "Stays");
        var announcements = Announcements(h);
        var card = board.Columns[0].Cards.Single();
        h.Coordinator.FailNext(new StorageFailureError(StorageFailureKind.DiskFull));

        h.Await(board.MoveToAdjacentColumnAsync(card, 1)).Should().BeFalse();
        h.Settle();

        board.Columns[0].Cards.Should().ContainSingle().Which.Should().BeSameAs(card);
        board.Columns[1].Cards.Should().BeEmpty();
        card.IsMoving.Should().BeFalse();
        h.Workspace.SaveStatus.State.Should().Be(SaveState.Failed);
        announcements.Should().Contain(a => a.IsFailure && a.Text.StartsWith("Couldn't move Stays. It stays in Backlog.", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Req", "B-10")]
    public void Search_finds_a_subtask_card_by_its_parent_title()
    {
        using var h = WorkspaceHarness.Create(async s =>
        {
            var parent = await s.TaskAsync("Card limits API");
            await s.CardAsync(parent, "Rounding fix");
            await s.TaskAsync("Unrelated");
        });
        var board = Ready(h);

        board.SearchText = "limits";
        h.Settle();

        board.Columns[0].Cards.Select(c => c.Title).Should().BeEquivalentTo(["Card limits API", "Rounding fix"]);
        board.Summary.Should().Be("Filtered: 2 of 3 cards");
    }

    [Fact]
    [Trait("Req", "T-08.1")]
    public void The_small_breakpoint_column_picker_steps_through_columns()
    {
        using var h = Board(out var board, "A");

        board.SelectedColumn.Should().BeSameAs(board.Columns[0]);
        board.PreviousColumnCommand.CanExecute(null).Should().BeFalse();
        board.NextColumnCommand.Execute(null);
        board.SelectedColumn.Should().BeSameAs(board.Columns[1]);
        board.Columns[0].PickerText.Should().Be("Column: Backlog (1)");
    }

    internal static WorkspaceHarness Board(out BoardPageViewModel board, params string[] backlogTitles)
    {
        var h = WorkspaceHarness.Create(async s =>
        {
            foreach (var title in backlogTitles)
            {
                await s.TaskAsync(title);
            }
        });
        board = Ready(h);
        return h;
    }

    internal static BoardPageViewModel Ready(WorkspaceHarness h)
    {
        Templates(h);
        var board = h.Workspace.Board;
        h.PumpUntil(() => board.State == ViewState.Populated);
        h.Settle();
        return board;
    }

    internal static void Templates(WorkspaceHarness h) => h.Strings
        .With("Board_Moved", "Moved {0} to {1}, position {2} of {3}")
        .With("Board_MoveFailed", "Couldn't move {0}. It stays in {1}.")
        .With("Board_CountFiltered", "{0} of {1}")
        .With("Board_SummaryFiltered", "Filtered: {0} of {1} cards")
        .With("Board_ColumnPickerItem", "Column: {0} ({1})");

    internal static List<Announcement> Announcements(WorkspaceHarness h)
    {
        var list = new List<Announcement>();
        h.Workspace.Context.Announced += (_, a) => list.Add(a);
        return list;
    }

    private static Application.Tasks.TaskContentDraft Draft(string title) =>
        new(title, "Service", null, null, [], null, Domain.Fields.CustomValue.NoValues);

    private static List<string> Titles(BoardColumnViewModel column) => [.. column.Cards.Select(c => c.Title)];
}
