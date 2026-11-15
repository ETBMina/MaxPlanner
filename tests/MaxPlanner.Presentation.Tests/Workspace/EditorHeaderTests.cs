using AwesomeAssertions;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.ChangeRequests;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.SubtaskCards;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// The shared header of the Task, CR and Subtask card editors (design §5.0): the record-kind caption above the title
/// ("Task", "Change request", "Subtask card", or the "New …" kind of an unsaved record), and the info box's Created fact,
/// which is the record's creation day with the year and is empty (the fact hidden) for a record not saved yet.
/// </summary>
public sealed class EditorHeaderTests
{
    [Fact]
    public void An_existing_task_shows_the_task_kind_and_its_creation_day()
    {
        TaskId id = default;
        using var h = WorkspaceHarness.Create(async s => id = await s.TaskAsync("Card limits"));
        using var editor = new TaskEditorViewModel(h.Workspace.Context, id, initialStatus: null);

        editor.DialogTitle.Should().Be("TaskEditor_Title");
        editor.CreatedText.Should().Be(DateText.RecordDate(h.Store.Tasks.Lookup(id).Value.CreatedAtUtc.ToLocalTime()));
    }

    [Fact]
    public void A_new_task_shows_the_new_task_kind_and_no_creation_day()
    {
        using var h = WorkspaceHarness.Create(_ => Task.CompletedTask);
        using var editor = new TaskEditorViewModel(h.Workspace.Context, taskId: null, initialStatus: null);

        editor.DialogTitle.Should().Be("TaskEditor_TitleNew");
        editor.CreatedText.Should().BeEmpty();
    }

    [Fact]
    public void An_existing_cr_shows_the_cr_kind_and_its_creation_day()
    {
        ChangeRequestId cr = default;
        using var h = WorkspaceHarness.Create(async s => cr = await s.CrAsync("CR-1", "Card limits"));
        using var editor = new CrEditorViewModel(h.Workspace.Context, cr);

        editor.DialogTitle.Should().Be("CrEditor_Title");
        editor.CreatedText.Should().Be(DateText.RecordDate(h.Store.ChangeRequests.Lookup(cr).Value.CreatedAtUtc.ToLocalTime()));
    }

    [Fact]
    public void A_new_cr_shows_the_new_cr_kind_and_no_creation_day()
    {
        using var h = WorkspaceHarness.Create(_ => Task.CompletedTask);
        using var editor = new CrEditorViewModel(h.Workspace.Context, changeRequestId: null);

        editor.DialogTitle.Should().Be("CrEditor_TitleNew");
        editor.CreatedText.Should().BeEmpty();
    }

    [Fact]
    public void An_existing_subtask_card_shows_the_card_kind_and_its_creation_day()
    {
        SubtaskCardId card = default;
        using var h = WorkspaceHarness.Create(async s => card = await s.CardAsync(await s.TaskAsync("Parent"), "Design review"));
        using var editor = SubtaskCardEditorViewModel.ForCard(h.Workspace.Context, card);

        editor.DialogTitle.Should().Be("SubtaskEditor_Title");
        editor.CreatedText.Should().Be(DateText.RecordDate(h.Store.SubtaskCards.Lookup(card).Value.CreatedAtUtc.ToLocalTime()));
    }

    [Fact]
    public void A_new_subtask_card_shows_the_new_card_kind_and_no_creation_day()
    {
        TaskId parent = default;
        using var h = WorkspaceHarness.Create(async s => parent = await s.TaskAsync("Parent"));
        using var editor = SubtaskCardEditorViewModel.ForNewCard(h.Workspace.Context, parent);

        editor.DialogTitle.Should().Be("SubtaskEditor_TitleNew");
        editor.CreatedText.Should().BeEmpty();
    }
}
