using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Domain.Tests.Fixtures;

namespace MaxPlanner.Domain.Tests.Tasks;

public sealed class TaskItemTests
{
    private static readonly ChangeRequestId Cr = new(Guid.NewGuid());

    [Fact]
    [Trait("Req", "B-02.2")]
    public void An_unlinked_task_needs_its_own_title()
    {
        var result = TaskItem.Create(new TaskId(Guid.NewGuid()), Content(ownTitle: null, unlinkedDefaults: false), Backlog, Medium,
            DateIntent.Unchanged, DateIntent.Unchanged, null, 1, Today, Now);

        result.Error!.Code.Should().Be(DomainErrorCodes.Required);
        result.Error.Field.Should().Be("Title");
    }

    [Fact]
    [Trait("Req", "B-02.2")]
    [Trait("Req", "AC-28")]
    public void A_task_created_linked_takes_no_own_title_and_requires_a_service()
    {
        var noService = TaskItem.Create(new TaskId(Guid.NewGuid()), Content(ownTitle: null, unlinkedDefaults: false), Backlog, Medium,
            DateIntent.Unchanged, DateIntent.Unchanged, Cr, 1, Today, Now);
        var withOwnTitle = TaskItem.Create(new TaskId(Guid.NewGuid()), Content(ownTitle: Title(), service: Service()), Backlog, Medium,
            DateIntent.Unchanged, DateIntent.Unchanged, Cr, 1, Today, Now);

        noService.Error!.Code.Should().Be(DomainErrorCodes.ServiceRequiredWhenLinked);
        withOwnTitle.Error!.Code.Should().Be(DomainErrorCodes.TitleNotAllowedWhenLinked);
    }

    [Fact]
    [Trait("Req", "AC-28")]
    [Trait("Req", "AC-29")]
    public void Linking_is_permanent_and_keeps_the_previous_title_only_as_preserved_metadata()
    {
        var task = UnlinkedTask(service: Service());

        var linked = task.LinkTo(Cr, Now).Value;
        var relink = linked.LinkTo(new ChangeRequestId(Guid.NewGuid()), Now);

        linked.ChangeRequestId.Should().Be(Cr);
        linked.OwnTitle.Should().BeNull();
        linked.Preserved.PreLinkTitle.Should().Be("Card freeze");
        linked.Revision.Should().Be(task.Revision + 1);
        relink.Error!.Code.Should().Be(DomainErrorCodes.TaskAlreadyLinked);
    }

    [Fact]
    [Trait("Req", "B-02.2")]
    public void Linking_requires_a_service_first()
    {
        UnlinkedTask().LinkTo(Cr, Now).Error!.Code.Should().Be(DomainErrorCodes.ServiceRequiredWhenLinked);
    }

    [Fact]
    [Trait("Req", "B-05")]
    public void Archived_tasks_are_read_only_but_can_still_be_linked_and_restored()
    {
        var archived = UnlinkedTask(service: Service()).Archive(Now).Value;

        archived.UpdateContent(Content(), Now).Error!.Code.Should().Be(DomainErrorCodes.ArchivedReadOnly);
        archived.LinkTo(Cr, Now).IsSuccess.Should().BeTrue();
        var restored = archived.Restore(rankAtTopOfColumn: 99_000, Now).Value;
        restored.IsArchived.Should().BeFalse();
        restored.Rank.Should().Be(99_000);
    }

    [Fact]
    [Trait("Req", "B-04")]
    [Trait("Req", "AC-05")]
    public void Moving_a_task_applies_the_transition_dates_and_the_new_rank_in_one_change()
    {
        var task = UnlinkedTask(Backlog);

        var moved = task.MoveTo(Backlog, InProgress, rank: 5_000, DateIntent.Unchanged, DateIntent.Unchanged, Today, Now).Value;

        moved.StatusId.Should().Be(InProgress.Id);
        moved.Rank.Should().Be(5_000);
        moved.Dates.Start.Should().Be(Today);
    }

    [Fact]
    public void A_move_based_on_a_stale_status_is_rejected()
    {
        UnlinkedTask(Backlog).MoveTo(Review, Completed, 1, DateIntent.Unchanged, DateIntent.Unchanged, Today, Now)
            .Error!.Code.Should().Be(DomainErrorCodes.StatusMismatch);
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void Toggling_every_checklist_item_never_changes_the_task_status_or_dates()
    {
        var itemId = new ChecklistItemId(Guid.NewGuid());
        var task = UnlinkedTask(InProgress).AddChecklistItem(itemId, ChecklistText.Create("Haptics").Value, Now).Value;

        var toggled = task.SetChecklistItemCompleted(itemId, true, Now).Value;

        toggled.ChecklistItems.Single().IsCompleted.Should().BeTrue();
        toggled.StatusId.Should().Be(task.StatusId);
        toggled.Dates.Should().Be(task.Dates);
    }

    [Fact]
    [Trait("Req", "B-16")]
    public void Removing_a_checklist_item_returns_it_for_pop_out()
    {
        var itemId = new ChecklistItemId(Guid.NewGuid());
        var task = UnlinkedTask().AddChecklistItem(itemId, ChecklistText.Create("Offline copy").Value, Now).Value;

        var (after, removed) = task.RemoveChecklistItem(itemId, Now).Value;

        after.ChecklistItems.Should().BeEmpty();
        removed.Text.Value.Should().Be("Offline copy");
    }

    [Fact]
    [Trait("Req", "B-02.10")]
    public void A_task_holds_at_most_the_checklist_item_limit()
    {
        var task = UnlinkedTask();
        for (var i = 0; i < DomainLimits.ChecklistItemsPerTask; i++)
        {
            task = task.AddChecklistItem(new ChecklistItemId(Guid.NewGuid()), ChecklistText.Create($"Item {i}").Value, Now).Value;
        }

        task.AddChecklistItem(new ChecklistItemId(Guid.NewGuid()), ChecklistText.Create("One too many").Value, Now)
            .Error!.Code.Should().Be(DomainErrorCodes.TooManyItems);
    }

    [Fact]
    public void Every_change_produces_a_new_instance_and_leaves_the_original_untouched()
    {
        var task = UnlinkedTask();

        var changed = task.SetPriority(new PriorityId(Guid.NewGuid()), Now.AddMinutes(1)).Value;

        task.PriorityId.Should().Be(Medium);
        task.Revision.Should().Be(1);
        changed.Revision.Should().Be(2);
        changed.UpdatedAtUtc.Should().Be(Now.AddMinutes(1));
    }
}
