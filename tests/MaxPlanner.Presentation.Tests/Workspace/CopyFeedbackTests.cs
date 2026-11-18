using AwesomeAssertions;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// A copy button's feedback: a copy that reached the clipboard is announced as "Copied {text}" and lets the button show
/// its "Copied" hint; a copy the clipboard refused, or with nothing to copy, gives no feedback.
/// </summary>
public sealed class CopyFeedbackTests
{
    [Theory]
    [InlineData("RP-116", true, true)]
    [InlineData("RP-116", false, false)]
    [InlineData("", true, false)]
    [InlineData(null, true, false)]
    public void Feedback_follows_only_a_copy_that_reached_the_clipboard(string? text, bool reachedClipboard, bool shown) =>
        CopyFeedbackRules.ShowsFeedback(text, reachedClipboard).Should().Be(shown);

    [Theory]
    [InlineData(false, "CR-1 - 0042", true)]
    [InlineData(true, "CR-1 - 0042", false)]
    [InlineData(false, "", false)]
    [InlineData(false, null, false)]
    public void Copy_id_is_offered_only_for_a_saved_record_with_id_text(bool isNew, string? idText, bool offered) =>
        CopyFeedbackRules.CanCopyId(isNew, idText).Should().Be(offered);

    [Fact]
    public void The_hint_closes_by_itself_after_one_and_a_half_seconds() =>
        CopyFeedbackRules.HintDuration.Should().Be(TimeSpan.FromSeconds(1.5));

    [Fact]
    public void The_board_task_chip_announces_the_copied_id()
    {
        using var h = BoardWithLinkedTask(out var task);
        h.Strings.With("Copy_Announcement", "Copied {0}");
        var announced = Record(h.Workspace.Context);
        using var board = new BoardPageViewModel(h.Workspace.Context, _ => true);
        var card = Card(h, board, task);

        board.CopyId(card).Should().BeTrue();

        announced.Should().Contain(new Announcement("Copied CR-1067 - 0042", false));
    }

    [Fact]
    public void A_copy_the_clipboard_refused_is_neither_hinted_nor_announced()
    {
        using var h = BoardWithLinkedTask(out var task);
        var announced = Record(h.Workspace.Context);
        using var board = new BoardPageViewModel(h.Workspace.Context, _ => false);
        var card = Card(h, board, task);

        board.CopyId(card).Should().BeFalse();

        announced.Should().BeEmpty();
    }

    [Fact]
    public void The_task_editor_announces_a_copied_number_and_ignores_a_failed_copy()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Card limits"));
        h.Strings.With("Copy_Announcement", "Copied {0}");
        var announced = Record(h.Workspace.Context);
        using var editor = new TaskEditorViewModel(h.Workspace.Context, id, initialStatus: null);

        editor.ReportCopied("RP-116", reachedClipboard: false).Should().BeFalse();
        editor.ReportCopied(string.Empty, reachedClipboard: true).Should().BeFalse();
        announced.Should().BeEmpty();

        editor.ReportCopied("RP-116", reachedClipboard: true).Should().BeTrue();
        announced.Should().Equal(new Announcement("Copied RP-116", false));
    }

    private static WorkspaceHarness BoardWithLinkedTask(out TaskId task)
    {
        TaskId created = default;
        var h = WorkspaceHarness.Create(async s =>
        {
            var cr = await s.CrAsync("CR-1067", "Card limits API");
            created = await s.TaskAsync(null, cr: cr);
        });
        BoardMoveTests.Templates(h);
        task = created;
        return h;
    }

    private static BoardCardViewModel Card(WorkspaceHarness h, BoardPageViewModel board, TaskId task)
    {
        h.PumpUntil(() => board.State == ViewState.Populated);
        h.Settle();
        return board.Buckets.SelectMany(b => b.Cards).Single(c => c.TaskId == task);
    }

    private static List<Announcement> Record(WorkspaceContext context)
    {
        var announced = new List<Announcement>();
        context.Announced += (_, a) => announced.Add(a);
        return announced;
    }
}
