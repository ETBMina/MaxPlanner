using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Domain.Tests.Fixtures;

namespace MaxPlanner.Domain.Tests.Tasks;

[Trait("Req", "B-02.8")]
public sealed class TaskEditTests
{
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    [Fact]
    [Trait("Req", "B-04")]
    public void A_whole_draft_is_one_revision_with_transition_dates_and_the_new_checklist()
    {
        var task = UnlinkedTask(Backlog);
        var item = new ChecklistItem(new ChecklistItemId(Guid.NewGuid()), ChecklistText.Create("Draft").Value, true, 1_024);
        var priority = new PriorityId(Guid.NewGuid());

        var edited = task.Edit(Edit(Content(Title("Renamed")), priority, items: [item]), Backlog, InProgress, 9_000, null, Today, Later).Value;

        edited.Revision.Should().Be(task.Revision + 1, "B-02.8: one save is one logical change");
        edited.OwnTitle!.Value.Should().Be("Renamed");
        edited.PriorityId.Should().Be(priority);
        edited.StatusId.Should().Be(InProgress.Id);
        edited.Dates.Start.Should().Be(Today, "B-04: a transition to a start-setting status fills StartDate");
        edited.Rank.Should().Be(9_000);
        edited.ChecklistItems.Should().Equal(item);
        edited.UpdatedAtUtc.Should().Be(Later);
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void Editing_without_a_status_change_fills_no_dates()
    {
        var task = UnlinkedTask(InProgress);
        var cleared = task.Edit(Edit(Content(), Medium, start: DateIntent.Cleared), InProgress, InProgress, task.Rank, null, Today, Later).Value;

        cleared.Dates.Start.Should().BeNull("an explicit clear without a transition stays cleared");
    }

    [Fact]
    [Trait("Req", "B-02.4")]
    [Trait("Req", "AC-28")]
    public void Linking_in_the_same_save_moves_the_draft_title_to_preserved_metadata()
    {
        var task = UnlinkedTask(Backlog);
        var cr = new ChangeRequestId(Guid.NewGuid());

        var linked = task.Edit(Edit(Content(Title("Own title"), Service()), Medium, items: []), Backlog, Backlog, task.Rank, cr, Today, Later).Value;

        linked.ChangeRequestId.Should().Be(cr);
        linked.OwnTitle.Should().BeNull();
        linked.Preserved.PreLinkTitle.Should().Be("Own title");
        linked.Revision.Should().Be(task.Revision + 1);
    }

    [Fact]
    [Trait("Req", "D10")]
    public void Linking_requires_a_service()
    {
        var task = UnlinkedTask(Backlog);
        var result = task.Edit(Edit(Content()), Backlog, Backlog, task.Rank, new ChangeRequestId(Guid.NewGuid()), Today, Later);

        result.Error!.Code.Should().Be(DomainErrorCodes.ServiceRequiredWhenLinked);
    }

    [Fact]
    [Trait("Req", "AC-29")]
    public void An_already_linked_task_cannot_be_linked_again()
    {
        var task = UnlinkedTask(Backlog, Service()).LinkTo(new ChangeRequestId(Guid.NewGuid()), Now).Value;
        var result = task.Edit(Edit(Content(service: Service(), unlinkedDefaults: false)), Backlog, Backlog, task.Rank, new ChangeRequestId(Guid.NewGuid()), Today, Later);

        result.Error!.Code.Should().Be(DomainErrorCodes.TaskAlreadyLinked);
    }

    [Fact]
    [Trait("Req", "B-05")]
    public void An_archived_task_is_read_only()
    {
        var task = UnlinkedTask(Backlog).Archive(Now).Value;
        task.Edit(Edit(Content()), Backlog, Backlog, task.Rank, null, Today, Later).Error!.Code.Should().Be(DomainErrorCodes.ArchivedReadOnly);
    }

    [Fact]
    public void Duplicate_checklist_ids_are_refused()
    {
        var task = UnlinkedTask(Backlog);
        var id = new ChecklistItemId(Guid.NewGuid());
        var items = new[] { new ChecklistItem(id, ChecklistText.Create("A").Value, false, 1), new ChecklistItem(id, ChecklistText.Create("B").Value, false, 2) };

        task.Edit(Edit(Content(), items: items), Backlog, Backlog, task.Rank, null, Today, Later).Error!.Code.Should().Be(DomainErrorCodes.DuplicateKey);
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void An_inverted_range_from_automation_is_a_validation_error()
    {
        var task = UnlinkedTask(Backlog);
        var result = task.Edit(Edit(Content(), end: DateIntent.Set(Today.AddDays(-3))), Backlog, InProgress, task.Rank, null, Today, Later);

        result.Error!.Code.Should().Be(DomainErrorCodes.DateRangeInverted);
    }

    private static TaskEdit Edit(TaskContent content, PriorityId? priority = null, DateIntent start = default, DateIntent end = default, IReadOnlyList<ChecklistItem>? items = null) =>
        new(content, priority ?? Medium, start, end, items ?? []);
}
