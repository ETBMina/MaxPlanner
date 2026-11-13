using AwesomeAssertions;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Grid;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// Design §4.3: the decisions behind the Grid's code-built menus and flyout editors — which header menu commands are
/// enabled, which row menu items appear and in what order, which flyout editor a column opens, and the double-click
/// guard of single-tap editing.
/// </summary>
[Trait("Req", "B-07")]
public sealed class GridMenuAndFlyoutRulesTests
{
    [Theory]
    [InlineData(0, 3, false, true, true)]
    [InlineData(1, 3, true, true, true)]
    [InlineData(2, 3, true, false, true)]
    [InlineData(0, 1, false, false, false)]
    public void The_header_menu_moves_within_the_displayed_columns_and_never_hides_the_last_one(
        int index, int count, bool canMoveLeft, bool canMoveRight, bool canHide) =>
        GridMenus.HeaderMenuStateFor(index, count).Should().Be((canMoveLeft, canMoveRight, canHide));

    [Fact]
    public void An_unlinked_active_task_offers_link_add_subtask_card_and_archive()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Onboarding copy"));

        GridMenus.RowMenuItemsFor(WorkItemRecord.From(h.Task(task))).Should().Equal(
            GridRowMenuItem.Open, GridRowMenuItem.Copy, GridRowMenuItem.CopyId, GridRowMenuItem.LinkToCr,
            GridRowMenuItem.AddSubtaskCard, GridRowMenuItem.Archive, GridRowMenuItem.Delete);
    }

    [Fact]
    public void A_linked_task_offers_open_cr_instead_of_link()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            var cr = await s.CrAsync("CR-1", "Card freeze");
            task = await s.TaskAsync(null, cr: cr);
        });

        GridMenus.RowMenuItemsFor(WorkItemRecord.From(h.Task(task))).Should().Equal(
            GridRowMenuItem.Open, GridRowMenuItem.OpenCr, GridRowMenuItem.Copy, GridRowMenuItem.CopyId,
            GridRowMenuItem.AddSubtaskCard, GridRowMenuItem.Archive, GridRowMenuItem.Delete);
    }

    [Fact]
    [Trait("Req", "B-05")]
    public void An_archived_unlinked_task_offers_link_and_restore()
    {
        TaskId task = default;
        using var h = WorkspaceHarness.Create(async s => task = await s.TaskAsync("Onboarding copy"));
        h.Await(h.Workspace.Context.Tasks.ArchiveAsync(h.Task(task))).Should().BeTrue();
        h.Settle();

        GridMenus.RowMenuItemsFor(WorkItemRecord.From(h.Task(task))).Should().Equal(
            GridRowMenuItem.Open, GridRowMenuItem.Copy, GridRowMenuItem.CopyId, GridRowMenuItem.LinkToCr,
            GridRowMenuItem.Restore, GridRowMenuItem.Delete);
    }

    [Fact]
    public void A_subtask_card_offers_open_its_parents_cr_copy_id_and_delete()
    {
        SubtaskCardId linkedCard = default;
        SubtaskCardId unlinkedCard = default;
        using var h = WorkspaceHarness.Create(async s =>
        {
            var cr = await s.CrAsync("CR-1", "Card freeze");
            linkedCard = await s.CardAsync(await s.TaskAsync(null, cr: cr), "Copy fix");
            unlinkedCard = await s.CardAsync(await s.TaskAsync("Unlinked parent"), "Copy fix");
        });

        GridMenus.RowMenuItemsFor(WorkItemRecord.From(h.Store.SubtaskCards.Lookup(linkedCard).Value)).Should().Equal(
            GridRowMenuItem.Open, GridRowMenuItem.OpenCr, GridRowMenuItem.CopyId, GridRowMenuItem.Delete);
        GridMenus.RowMenuItemsFor(WorkItemRecord.From(h.Store.SubtaskCards.Lookup(unlinkedCard).Value)).Should().Equal(
            GridRowMenuItem.Open, GridRowMenuItem.CopyId, GridRowMenuItem.Delete);
    }

    [Theory]
    [InlineData(GridEditorKind.People, true, false, GridFlyoutEditorKind.TokenPicker)]
    [InlineData(GridEditorKind.Tags, true, false, GridFlyoutEditorKind.TokenPicker)]
    [InlineData(GridEditorKind.People, false, false, GridFlyoutEditorKind.Notes)]
    [InlineData(GridEditorKind.Custom, false, true, GridFlyoutEditorKind.CustomField)]
    [InlineData(GridEditorKind.Custom, false, false, GridFlyoutEditorKind.Notes)]
    [InlineData(GridEditorKind.Custom, true, false, GridFlyoutEditorKind.Notes)]
    [InlineData(GridEditorKind.Notes, false, false, GridFlyoutEditorKind.Notes)]
    public void A_flyout_column_opens_its_picker_or_custom_editor_when_the_row_has_that_draft_and_text_otherwise(
        GridEditorKind kind, bool hasDraftTokens, bool hasDraftCustom, GridFlyoutEditorKind expected) =>
        GridEditorFlyout.EditorFor(kind, hasDraftTokens, hasDraftCustom).Should().Be(expected);

    [Theory]
    [InlineData(10_000, 10_000, true)]
    [InlineData(10_000, 10_499, true)]
    [InlineData(10_000, 10_500, false)]
    [InlineData(10_000, 60_000, false)]
    public void A_close_within_the_double_click_time_of_opening_is_guarded(long startedAt, long now, bool expected) =>
        GridInput.IsWithinDoubleClickWindow(startedAt, now).Should().Be(expected);
}
