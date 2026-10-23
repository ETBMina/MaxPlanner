using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

/// <summary>
/// The Phase 4 task editor commands on a real SQLite file (T-11.2): a whole draft with new people and tags,
/// checklist changes and a permanent link survives a reopen; deletion removes the dependent rows and
/// tombstones every ID; copy and restore use the real column ranks.
/// </summary>
[Trait("Req", "B-02")]
public sealed class TaskEditorPersistenceTests
{
    [Fact]
    [Trait("Req", "B-02.6")]
    [Trait("Req", "B-02.8")]
    [Trait("Req", "AC-28")]
    public async Task A_whole_draft_with_new_lookups_checklist_and_link_is_durable_after_reopen()
    {
        await using var db = await CreateAsync();
        var owner = db.AddPerson("Owner");
        db.AddTag("Mobile");
        var cr = await db.AddCrAsync(owner);
        var inProgress = await db.TaskStatusAsync("In Progress");
        var task = await db.AddTaskAsync("Own title");
        var designer = new NewPerson(new PersonId(Guid.NewGuid()), "Dana");
        var sameTag = new NewTag(new TagId(Guid.NewGuid()), "mobile");
        var item = new ChecklistItemId(Guid.NewGuid());
        var snapshot = await db.SnapshotAsync();
        var priority = snapshot.Workflow.Priorities[0].Id;

        var revision = Succeeded(await new UpdateTask(db.Coordinator).ExecuteAsync(
            new UpdateTaskCommand(OperationId.New(), task, 1,
                Draft("Own title", "Cards API") with { DesignerIds = [designer.Id], TagIds = [sameTag.Id] },
                inProgress.Id, priority, DateIntent.Unchanged, DateIntent.Unchanged,
                [new ChecklistItemDraft(item, "Write tests", false)],
                new NewLookups([designer], [sameTag]),
                new CrLinkTarget(cr, 1)),
            Ct)).Value;

        revision.Should().Be(2);
        await db.ReopenAsync();
        var reopened = await db.SnapshotAsync();
        var saved = reopened.Tasks.Single();
        saved.ChangeRequestId.Should().Be(cr);
        saved.OwnTitle.Should().BeNull();
        saved.Preserved.PreLinkTitle.Should().Be("Own title");
        saved.StatusId.Should().Be(inProgress.Id);
        saved.Dates.Start.Should().Be(DateOnly.FromDateTime(db.Clock.UtcNow.UtcDateTime));
        saved.DesignerIds.Should().Equal(designer.Id);
        saved.TagIds.Should().ContainSingle().Which.Should().NotBe(sameTag.Id, "'mobile' reuses the existing Mobile tag");
        saved.ChecklistItems.Single().Id.Should().Be(item);
        reopened.Workflow.People.Select(p => p.Name.Display).Should().BeEquivalentTo(["Owner", "Dana"]);
        reopened.Workflow.Tags.Should().ContainSingle();
        reopened.Links.Should().ContainSingle(l => l.TaskId == task && l.ChangeRequestId == cr);
    }

    [Fact]
    [Trait("Req", "B-05")]
    [Trait("Req", "T-06.3")]
    public async Task Deleting_a_task_removes_cards_items_and_link_and_tombstones_every_id()
    {
        await using var db = await CreateAsync();
        var cr = await db.AddCrAsync(db.AddPerson("Owner"));
        var task = new TaskId(Guid.NewGuid());
        var item = new ChecklistItemId(Guid.NewGuid());
        Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), task, Draft(null, "Cards API"), LinkTo: new CrLinkTarget(cr, 1),
                ChecklistItems: [new ChecklistItemDraft(item, "Step", false)]), Ct));
        var card = new SubtaskCardId(Guid.NewGuid());
        Succeeded(await new CreateSubtaskCard(db.Coordinator).ExecuteAsync(new CreateSubtaskCardCommand(OperationId.New(), task, card, "Follow-up"), Ct));

        var summary = Succeeded(await new DeleteTask(db.Coordinator).ExecuteAsync(new DeleteTaskCommand(OperationId.New(), task, 1), Ct)).Value;

        summary.Should().Be(new TaskDeletionSummary(1, 1));
        await db.ReopenAsync();
        var snapshot = await db.SnapshotAsync();
        snapshot.Tasks.Should().BeEmpty();
        snapshot.SubtaskCards.Should().BeEmpty();
        snapshot.Links.Should().BeEmpty();
        snapshot.ChangeRequests.Should().ContainSingle("the CR stays (B-05)");
        db.Scalar("SELECT count(*) FROM checklist_items;").Should().Be(0);
        db.Scalar("SELECT count(*) FROM tombstones;").Should().Be(3);

        var recreated = await new CreateTask(db.Coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), task, Draft()), Ct);
        Failed<TaskId, RecordNotFoundError>(recreated).WasDeleted.Should().BeTrue("B-05: a deleted ID cannot come back");
    }

    [Fact]
    [Trait("Req", "B-05")]
    [Trait("Req", "B-06")]
    public async Task Restore_and_copy_append_to_the_end_of_the_real_column()
    {
        await using var db = await CreateAsync();
        var first = await db.AddTaskAsync("First");
        Succeeded(await new ArchiveTask(db.Coordinator).ExecuteAsync(new ArchiveTaskCommand(OperationId.New(), first, 1), Ct));
        var second = await db.AddTaskAsync("Second");
        Succeeded(await new RestoreTask(db.Coordinator).ExecuteAsync(new RestoreTaskCommand(OperationId.New(), first, 2), Ct));
        var copy = new TaskId(Guid.NewGuid());

        Succeeded(await new CopyTask(db.Coordinator).ExecuteAsync(
            new CopyTaskCommand(OperationId.New(), first, copy, "Copy of first", null, new CopyOptions()), Ct));

        var tasks = (await db.SnapshotAsync()).Tasks.ToDictionary(t => t.Id);
        tasks[first].IsArchived.Should().BeFalse();
        tasks[first].Rank.Should().BeGreaterThan(tasks[second].Rank);
        tasks[copy].Rank.Should().BeGreaterThan(tasks[first].Rank);
        tasks[copy].Revision.Should().Be(1);
    }
}
