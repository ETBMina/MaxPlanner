using AwesomeAssertions;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// Design §4.2 and §4.6: Settings › Appearance › Show tags on Board cards (a UI preference) and the card's Task chip,
/// which runs Copy ID like the card menu.
/// </summary>
[Trait("Req", "B-07")]
public sealed class BoardCardSettingsTests
{
    [Fact]
    public void Show_tags_on_board_cards_is_on_by_default()
    {
        using var h = WorkspaceHarness.Create();

        h.Workspace.Context.ShowBoardCardTags.Should().BeTrue();
        UiPreferences.Default.ShowBoardCardTags.Should().BeNull("an unset preference means the default, on");
    }

    [Fact]
    public void Turning_the_setting_off_saves_it_and_tells_every_open_card()
    {
        using var h = BoardMoveTests.Board(out var board, "A", "B");
        var notified = new List<BoardCardViewModel>();
        foreach (var card in board.Buckets.SelectMany(b => b.Cards))
        {
            card.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BoardCardViewModel.HasTagStrip))
                {
                    notified.Add(card);
                }
            };
        }

        h.Workspace.Context.ShowBoardCardTags = false;

        h.Preferences.Current.ShowBoardCardTags.Should().BeFalse();
        notified.Should().HaveCount(2);
        board.Buckets.SelectMany(b => b.Cards).Should().OnlyContain(c => !c.HasTagStrip);
    }

    [Fact]
    public void Restoring_the_default_ui_preferences_turns_the_setting_back_on()
    {
        using var h = WorkspaceHarness.Create(preferences: UiPreferences.Default with { ShowBoardCardTags = false });
        h.Workspace.Context.ShowBoardCardTags.Should().BeFalse();

        h.Preferences.Update(_ => UiPreferences.Default);

        h.Workspace.Context.ShowBoardCardTags.Should().BeTrue();
    }

    [Fact]
    public void A_disposed_board_stops_listening_to_the_setting()
    {
        using var h = BoardMoveTests.Board(out _, "A");
        var context = h.Workspace.Context;
        var before = ContextListeners(context);
        var board = new BoardPageViewModel(context, _ => { });
        h.PumpUntil(() => board.State == ViewState.Populated);
        h.Settle();
        var card = board.Buckets.SelectMany(b => b.Cards).Single();
        var notified = 0;
        card.PropertyChanged += (_, e) => notified += e.PropertyName == nameof(BoardCardViewModel.HasTagStrip) ? 1 : 0;
        ContextListeners(context).Should().Be(before + 1);

        board.Dispose();
        context.ShowBoardCardTags = false;

        ContextListeners(context).Should().Be(before, "Dispose removes the board's WorkspaceContext.PropertyChanged handler");
        notified.Should().Be(0);
    }

    [Fact]
    public void The_task_chip_copies_the_cards_id_in_the_copy_id_format()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            var cr = await s.CrAsync("CR-1067", "Card limits API");
            task = await s.TaskAsync(null, cr: cr);
        });
        BoardMoveTests.Templates(h);
        var copied = new List<string>();
        using var board = new BoardPageViewModel(h.Workspace.Context, copied.Add);
        h.PumpUntil(() => board.State == ViewState.Populated);
        h.Settle();
        var card = board.Buckets.SelectMany(b => b.Cards).Single(c => c.TaskId == task);

        card.HasTaskNumber.Should().BeTrue();
        board.CopyId(card);

        copied.Should().Equal("CR-1067 - 0042");
    }

    // ObservableObject's field-like PropertyChanged event: its invocation list counts the subscribers.
    private static int ContextListeners(WorkspaceContext context) =>
        (typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject)
            .GetField(nameof(WorkspaceContext.PropertyChanged), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(context) as Delegate)?.GetInvocationList().Length ?? 0;
}
