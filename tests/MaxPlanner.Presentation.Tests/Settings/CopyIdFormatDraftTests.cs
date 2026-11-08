using AwesomeAssertions;
using MaxPlanner.Application.Settings;
using MaxPlanner.Presentation.Settings;

namespace MaxPlanner.Presentation.Tests.Settings;

/// <summary>Design §4.6 and ADR 0010: the Copy ID format box keeps unsaved edits and sends one save at a time.</summary>
[Trait("Adr", "0010")]
public sealed class CopyIdFormatDraftTests
{
    private const string Default = CopyIdFormatter.Default;

    [Fact]
    public void A_committed_update_replaces_text_without_an_unsaved_edit()
    {
        var draft = new CopyIdFormatDraft();
        var text = draft.Sync(string.Empty, Default);

        draft.Sync(text, "{Title}").Should().Be("{Title}");
    }

    [Fact]
    public void A_committed_update_keeps_text_the_user_is_typing()
    {
        var draft = new CopyIdFormatDraft();
        draft.Sync(string.Empty, Default);

        draft.Sync("{Squad} -", Default).Should().Be("{Squad} -", "another default changed while the user typed");
        draft.Sync("{Squad} -", "{Title}").Should().Be("{Squad} -");
    }

    [Fact]
    public void Committing_an_unchanged_or_blank_format_saves_nothing()
    {
        var draft = new CopyIdFormatDraft();
        draft.Sync(Default, Default);

        draft.Commit(Default, Default).Should().BeNull();
        draft.Commit("  ", Default).Should().BeNull();
        draft.IsSaving.Should().BeFalse();
    }

    [Fact]
    public void A_value_committed_during_a_save_is_sent_after_the_committed_update()
    {
        var draft = new CopyIdFormatDraft();
        draft.Sync(Default, Default);

        draft.Commit("A", Default).Should().Be("A");
        draft.Commit("A", Default).Should().BeNull("Enter then LostFocus sends one save");
        draft.Commit("B", Default).Should().BeNull("a save is in flight");

        draft.SaveEnded(succeeded: true, refreshed: false);
        draft.TakePending(Default).Should().BeNull("the stamp is not refreshed yet");

        draft.Sync("B", "A").Should().Be("B");
        draft.TakePending("A").Should().Be("B");
        draft.IsSaving.Should().BeTrue();
    }

    [Fact]
    public void A_pending_value_equal_to_the_saved_one_is_dropped()
    {
        var draft = new CopyIdFormatDraft();
        draft.Sync(Default, Default);
        draft.Commit("A", Default);
        draft.Commit("A", Default);

        draft.SaveEnded(succeeded: true, refreshed: true);

        draft.TakePending("A").Should().BeNull();
        draft.IsSaving.Should().BeFalse();
    }

    [Fact]
    public void After_a_failed_save_the_pending_value_is_tried_at_once()
    {
        var draft = new CopyIdFormatDraft();
        draft.Sync(Default, Default);
        draft.Commit("A", Default);
        draft.Commit("B", Default);

        draft.SaveEnded(succeeded: false, refreshed: false);

        draft.TakePending(Default).Should().Be("B");
    }
}
