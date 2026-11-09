using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Board;

/// <summary>Design §4.2 Board card anatomy (owner redesign "H1 · Pill strip"): tag strip, ID chips, and checklist row.</summary>
[Trait("Req", "B-07")]
public sealed class BoardCardViewModelTests
{
    private static readonly LookupRef<TagId> Integration = new(new TagId(Guid.NewGuid()), "Integration");
    private static readonly LookupRef<TagId> Zain = new(new TagId(Guid.NewGuid()), "Zain");
    private static readonly LookupRef<TaskStatusId> Status = new(new TaskStatusId(Guid.NewGuid()), "To do");

    private static readonly FakeStrings Strings = new FakeStrings()
        .With("Board_TagSeparator", ", ")
        .With("Board_CardNameWithTags", "{0}, Tags: {1}")
        .With("Board_TaskCardName", "{0}, {1}, {2}, {3} priority, {4}, {5}")
        .With("Board_SubtaskCardName", "Subtask card {0}, subtask of {1}, {2}")
        .With("Board_ChecklistCount", "{0}/{1}")
        .With("Board_ChecklistProgress", "Checklist {0}/{1}")
        .With("Board_NoChecklist", "No checklist")
        .With("Board_CopyIdChipName", "Copy ID for Task {0}")
        .With("TaskEditor_OpenCrName", "Open {0}")
        .With("Board_CrReferenceWithTask", "{0} · Task {1}");

    [Fact]
    public void A_task_with_tags_shows_the_strip_while_the_setting_is_on()
    {
        var card = ViewModel(Task(tags: [Integration, Zain]));

        card.HasTagStrip.Should().BeTrue();
        card.Tags.Select(t => (t.Id, t.Name)).Should().Equal((Integration.Id.Value, "Integration"), (Zain.Id.Value, "Zain"));
        card.TagsText.Should().Be("Integration, Zain");
        card.AccessibleName.Should().EndWith(", Tags: Integration, Zain");
    }

    [Fact]
    public void A_task_without_tags_has_no_strip_and_no_tags_in_its_name()
    {
        var card = ViewModel(Task());

        card.HasTagStrip.Should().BeFalse("design §4.2: no tags leaves no empty band");
        card.AccessibleName.Should().NotContain("Tags:");
    }

    [Fact]
    public void Turning_the_setting_off_hides_the_strip_and_notifies_without_a_model_update()
    {
        var show = true;
        var card = ViewModel(Task(tags: [Integration]), () => show);
        var changed = Changes(card);

        show = false;
        card.OnShowTagsChanged();

        card.HasTagStrip.Should().BeFalse();
        card.AccessibleName.Should().NotContain("Tags:");
        changed.Should().Contain([nameof(BoardCardViewModel.HasTagStrip), nameof(BoardCardViewModel.AccessibleName)]);
    }

    [Fact]
    public void An_unlinked_task_has_neither_chip()
    {
        var card = ViewModel(Task(cr: Unlinked));

        card.HasCrReference.Should().BeFalse();
        card.HasTaskNumber.Should().BeFalse();
        card.CopyIdName.Should().BeEmpty();
    }

    [Fact]
    public void A_linked_task_without_a_task_number_has_only_the_cr_chip()
    {
        var card = ViewModel(Task(cr: Linked("CR-1067", null)));

        card.HasCrReference.Should().BeTrue();
        card.CrNumber.Should().Be("CR-1067");
        card.OpenCrName.Should().Be("Open CR-1067");
        card.HasTaskNumber.Should().BeFalse();
    }

    [Fact]
    public void A_linked_task_with_a_task_number_has_both_chips()
    {
        var card = ViewModel(Task(cr: Linked("CR-1067", "INT-7625")));

        card.HasCrReference.Should().BeTrue();
        card.HasTaskNumber.Should().BeTrue();
        card.TaskNumber.Should().Be("INT-7625");
        card.CopyIdName.Should().Be("Copy ID for Task INT-7625");
    }

    [Fact]
    public void A_task_without_checklist_items_shows_no_checklist_and_no_bar()
    {
        var card = ViewModel(Task());

        card.HasChecklist.Should().BeFalse();
        card.IsChecklistComplete.Should().BeFalse("B-07: an empty list never looks complete");
        card.IsChecklistIncomplete.Should().BeFalse();
        card.ChecklistText.Should().Be("No checklist");
        card.ChecklistCountText.Should().BeEmpty();
    }

    [Fact]
    public void A_partial_checklist_shows_its_count_and_progress()
    {
        var card = ViewModel(Task(done: 1, total: 3));

        card.HasChecklist.Should().BeTrue();
        card.IsChecklistIncomplete.Should().BeTrue();
        card.IsChecklistComplete.Should().BeFalse();
        card.ChecklistCountText.Should().Be("1/3");
        card.ChecklistText.Should().Be("Checklist 1/3");
        card.ChecklistPercent.Should().BeApproximately(33.33, 0.01);
    }

    [Fact]
    public void A_complete_checklist_is_marked_complete()
    {
        var card = ViewModel(Task(done: 3, total: 3));

        card.IsChecklistComplete.Should().BeTrue();
        card.IsChecklistIncomplete.Should().BeFalse();
        card.ChecklistCountText.Should().Be("3/3");
        card.ChecklistPercent.Should().Be(100);
    }

    [Fact]
    public void A_subtask_card_shows_its_parents_tags_and_inherited_chips()
    {
        var card = ViewModel(Card(parentTags: [Zain], cr: Linked("CR-1067", "INT-7625")));

        card.HasTagStrip.Should().BeTrue();
        card.TagsText.Should().Be("Zain");
        card.HasCrReference.Should().BeTrue();
        card.HasTaskNumber.Should().BeTrue();
        card.AccessibleName.Should().Be("Subtask card Rollout, subtask of Card freeze, To do, Tags: Zain");
    }

    [Fact]
    public void A_parent_tag_change_replaces_the_subtask_cards_tags_and_notifies()
    {
        var original = Card(parentTags: [Zain]);
        var card = ViewModel(original);
        var changed = Changes(card);

        card.Update(WorkItemRecord.From(original with { ParentTags = [Integration, Zain], Revision = 2 }));

        card.TagsText.Should().Be("Integration, Zain");
        changed.Should().Contain([nameof(BoardCardViewModel.Tags), nameof(BoardCardViewModel.HasTagStrip), nameof(BoardCardViewModel.TagsText)]);
    }

    [Fact]
    public void Removing_the_parents_last_tag_removes_the_strip()
    {
        var original = Card(parentTags: [Zain]);
        var card = ViewModel(original);

        card.Update(WorkItemRecord.From(original with { ParentTags = [], Revision = 2 }));

        card.HasTagStrip.Should().BeFalse();
    }

    [Fact]
    public void An_update_that_keeps_the_tags_keeps_the_same_list()
    {
        var original = Task(tags: [Integration]);
        var card = ViewModel(original);
        var before = card.Tags;
        var changed = Changes(card);

        card.Update(WorkItemRecord.From(original with { EffectiveTitle = "Renamed", Revision = 2 }));

        card.Tags.Should().BeSameAs(before, "the strip is rebuilt only when its tags change");
        changed.Should().NotContain(nameof(BoardCardViewModel.Tags));
    }

    private static readonly InheritedCrValues Unlinked = new(null, null, null, null, "General", null, null);

    private static InheritedCrValues Linked(string number, string? taskNumber) =>
        new(new ChangeRequestId(Guid.NewGuid()), number, taskNumber, null, "General", null, null);

    private static BoardCardViewModel ViewModel(TaskReadModel task, Func<bool>? showTags = null) =>
        new(WorkItemRecord.From(task), Strings, _ => PriorityLevel.Mid, showTags);

    private static BoardCardViewModel ViewModel(SubtaskCardReadModel card) =>
        new(WorkItemRecord.From(card), Strings, _ => PriorityLevel.Mid, () => true);

    private static List<string?> Changes(BoardCardViewModel card)
    {
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        return changed;
    }

    private static TaskReadModel Task(LookupRef<TagId>[]? tags = null, InheritedCrValues? cr = null, int done = 0, int total = 0) =>
        new(
            new TaskId(Guid.NewGuid()), BoardCardKind.Task, "Card freeze", false, "Payments", Status, 0, false,
            new LookupRef<PriorityId>(new PriorityId(Guid.NewGuid()), "High"), 0, cr ?? Unlinked, null, null,
            tags ?? [], null, null, null, false, 1000, done, total, [], 0, new Dictionary<FieldId, CustomValue>(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

    private static SubtaskCardReadModel Card(LookupRef<TagId>[] parentTags, InheritedCrValues? cr = null) =>
        new(
            new SubtaskCardId(Guid.NewGuid()), BoardCardKind.SubtaskCard, new TaskId(Guid.NewGuid()), "Rollout", "Card freeze", null, parentTags,
            false, Status, 0, false, cr ?? Unlinked, null, null, null, 1000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
}
