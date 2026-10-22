using AwesomeAssertions;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Domain.Tests.Fixtures;

namespace MaxPlanner.Domain.Tests.Policies;

[Trait("Req", "B-06")]
[Trait("Req", "AC-09")]
public sealed class CopyTaskPolicyTests
{
    private static readonly PriorityId High = new(Guid.NewGuid());
    private static readonly ChecklistItemId DoneItem = new(Guid.NewGuid());
    private static readonly ChecklistItemId OpenItem = new(Guid.NewGuid());
    private static readonly FieldId Points = new(Guid.NewGuid());
    private static readonly TaskStatusId[] AllStatuses = [Backlog.Id, InProgress.Id, Completed.Id];

    [Fact]
    public void By_default_a_copy_takes_only_the_effective_title_priority_and_default_status()
    {
        var source = ArchivedLinkedSource();

        var draft = CopyTaskPolicy.Draft(source, Title("CR title"), new CopyOptions(), Backlog.Id, AllStatuses, [Medium, High], Medium, Guid.NewGuid);

        draft.Content.OwnTitle!.Value.Should().Be("CR title");
        draft.Content.Service.Should().BeNull();
        draft.Content.DesignerIds.Should().BeEmpty();
        draft.Content.DeveloperIds.Should().BeEmpty();
        draft.Content.TagIds.Should().BeEmpty();
        draft.Content.Notes.Should().BeNull();
        draft.Content.CustomValues.Should().BeEmpty();
        draft.ChecklistItems.Should().BeEmpty();
        draft.StatusId.Should().Be(Backlog.Id);
        draft.PriorityId.Should().Be(High);
        draft.Start.Should().Be(DateIntent.Unchanged);
        draft.End.Should().Be(DateIntent.Unchanged);
    }

    [Fact]
    public void A_removed_source_priority_falls_back_to_the_configured_default()
    {
        CopyTaskPolicy.Draft(ArchivedLinkedSource(), Title(), new CopyOptions(), Backlog.Id, AllStatuses, [Medium], Medium, Guid.NewGuid)
            .PriorityId.Should().Be(Medium);
    }

    [Fact]
    public void Selected_options_copy_people_tags_notes_dates_custom_values_and_checklist_items_with_new_ids()
    {
        var source = ArchivedLinkedSource();
        var options = new CopyOptions { CopyPeople = true, CopyTags = true, CopyNotes = true, CopyDates = true, CopyCustomValues = true, CopyChecklistItems = true };

        var draft = CopyTaskPolicy.Draft(source, Title(), options, Backlog.Id, AllStatuses, [High], Medium, Guid.NewGuid);

        draft.Content.DesignerIds.Should().Equal(source.DesignerIds);
        draft.Content.DeveloperIds.Should().Equal(source.DeveloperIds);
        draft.Content.TagIds.Should().Equal(source.TagIds);
        draft.Content.Notes.Should().Be(source.Notes);
        draft.Content.CustomValues.Should().ContainKey(Points);
        draft.Start.Should().Be(DateIntent.Set(source.Dates.Start!.Value));
        draft.End.Should().Be(DateIntent.Set(source.Dates.End!.Value));
        draft.ChecklistItems.Select(i => i.Text.Value).Should().Equal("Done item", "Open item");
        draft.ChecklistItems.Select(i => i.Id).Should().NotIntersectWith([DoneItem, OpenItem]);
        draft.ChecklistItems.Should().OnlyContain(i => !i.IsCompleted, "completion is reset by default");
    }

    [Fact]
    public void Preserve_completion_keeps_checked_items_checked()
    {
        var options = new CopyOptions { CopyChecklistItems = true, PreserveChecklistCompletion = true };

        CopyTaskPolicy.Draft(ArchivedLinkedSource(), Title(), options, Backlog.Id, AllStatuses, [High], Medium, Guid.NewGuid)
            .ChecklistItems.Select(i => i.IsCompleted).Should().Equal(true, false);
    }

    [Fact]
    public void The_draft_creates_a_new_active_unlinked_task_at_revision_one()
    {
        var source = ArchivedLinkedSource();
        var draft = CopyTaskPolicy.Draft(source, Title("CR title"), new CopyOptions { CopyChecklistItems = true }, InProgress.Id, AllStatuses, [High], Medium, Guid.NewGuid);

        var copy = TaskItem.Create(new TaskId(Guid.NewGuid()), draft.Content, InProgress, draft.PriorityId, draft.Start, draft.End,
            linkTo: null, rank: 2_048, Today, Now.AddHours(1), draft.ChecklistItems).Value;

        copy.Id.Should().NotBe(source.Id);
        copy.IsArchived.Should().BeFalse();
        copy.IsLinked.Should().BeFalse();
        copy.Revision.Should().Be(1);
        copy.CreatedAtUtc.Should().Be(Now.AddHours(1));
        copy.Dates.Start.Should().Be(Today, "dates were not copied, so ordinary creation rules apply");
        copy.ChecklistItems.Should().HaveCount(2);
        copy.Preserved.PreLinkTitle.Should().BeNull();
    }

    [Fact]
    public void Copy_status_starts_the_copy_in_the_sources_status_with_ordinary_creation_dates()
    {
        var source = ArchivedLinkedSource();
        var draft = CopyTaskPolicy.Draft(source, Title(), new CopyOptions { CopyStatus = true }, Backlog.Id, AllStatuses, [High], Medium, Guid.NewGuid);

        var copy = TaskItem.Create(new TaskId(Guid.NewGuid()), draft.Content, Completed, draft.PriorityId, draft.Start, draft.End,
            linkTo: null, rank: 1, Today.AddDays(3), Now).Value;

        draft.StatusId.Should().Be(Completed.Id);
        copy.Dates.Start.Should().BeNull("dates were not copied");
        copy.Dates.End.Should().Be(Today.AddDays(3), "creating in a completed status stamps today (B-04)");
    }

    [Fact]
    public void Copy_status_with_copy_dates_keeps_the_source_dates()
    {
        var source = ArchivedLinkedSource();
        var draft = CopyTaskPolicy.Draft(source, Title(), new CopyOptions { CopyStatus = true, CopyDates = true }, Backlog.Id, AllStatuses, [High], Medium, Guid.NewGuid);

        var copy = TaskItem.Create(new TaskId(Guid.NewGuid()), draft.Content, Completed, draft.PriorityId, draft.Start, draft.End,
            linkTo: null, rank: 1, Today.AddDays(3), Now).Value;

        copy.Dates.Should().Be(source.Dates);
    }

    [Fact]
    public void Copy_status_falls_back_to_the_default_when_the_source_status_was_removed()
    {
        CopyTaskPolicy.Draft(ArchivedLinkedSource(), Title(), new CopyOptions { CopyStatus = true }, Backlog.Id, [Backlog.Id, InProgress.Id], [High], Medium, Guid.NewGuid)
            .StatusId.Should().Be(Backlog.Id);
    }

    private static TaskItem ArchivedLinkedSource()
    {
        var content = new TaskContent(Title("Own title before link"), Service(), [new PersonId(Guid.NewGuid())], [new PersonId(Guid.NewGuid())],
            [new TagId(Guid.NewGuid())], LongText.CreateOptional("Line 1\nLine 2", "Notes").Value,
            new Dictionary<FieldId, CustomValue> { [Points] = new NumberCustomValue(3) });
        var task = TaskItem.Create(new TaskId(Guid.NewGuid()), content, Completed, High, DateIntent.Set(Today.AddDays(-5)), DateIntent.Unchanged,
            linkTo: null, rank: 1_024, Today, Now).Value;
        task = task.AddChecklistItem(DoneItem, ChecklistText.Create("Done item").Value, Now).Value
            .AddChecklistItem(OpenItem, ChecklistText.Create("Open item").Value, Now).Value
            .SetChecklistItemCompleted(DoneItem, true, Now).Value;
        return task.LinkTo(new ChangeRequestId(Guid.NewGuid()), Now).Value.Archive(Now).Value;
    }
}
