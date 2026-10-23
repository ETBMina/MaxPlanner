using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Tasks;

/// <summary>The task editor's save (UpdateTask, CreateTask with drafts) and its More-menu commands.</summary>
public sealed class TaskEditorUseCaseTests
{
    private readonly TestWorkspace _workspace = new();

    [Fact]
    [Trait("Req", "B-02.8")]
    [Trait("Req", "B-04")]
    [Trait("Req", "B-07")]
    public async Task Saving_a_draft_with_a_status_change_is_one_revision_appended_to_the_new_column_with_its_dates()
    {
        var id = await _workspace.AddTaskAsync();
        var inProgress = await _workspace.AddTaskAsync("Already there", InProgress);

        var outcome = Succeeded(await Save(id, 1, Draft("Renamed", "Cards API"), InProgress.Id, High.Id));

        var task = _workspace.Task(id);
        outcome.Value.Should().Be(2, "B-02.8: the whole draft is one change");
        task.StatusId.Should().Be(InProgress.Id);
        task.PriorityId.Should().Be(High.Id);
        task.Dates.Start.Should().Be(Today, "B-04: the status transition fills StartDate");
        task.Rank.Should().BeGreaterThan(_workspace.Task(inProgress).Rank, "B-07: a status change appends to the end of the column");
        _workspace.Coordinator.Published[^1].OperationKind.Should().Be(UpdateTask.OperationKind);
    }

    [Fact]
    [Trait("Req", "B-02.7")]
    [Trait("Req", "B-05")]
    public async Task Checklist_edits_keep_ids_follow_the_draft_order_and_tombstone_removed_items()
    {
        var id = await _workspace.AddTaskAsync();
        var keep = new ChecklistItemId(Guid.NewGuid());
        var drop = new ChecklistItemId(Guid.NewGuid());
        Succeeded(await Save(id, 1, Draft(), items: [new(keep, "Keep", false), new(drop, "Drop", false)]));
        var added = new ChecklistItemId(Guid.NewGuid());

        Succeeded(await Save(id, 2, Draft(), items: [new(added, "New first", false), new(keep, "Kept, renamed", true)]));

        var items = _workspace.Task(id).ChecklistItems;
        items.Select(i => (i.Id, i.Text.Value, i.IsCompleted)).Should().Equal((added, "New first", false), (keep, "Kept, renamed", true));
        items[0].Rank.Should().BeLessThan(items[1].Rank);
        _workspace.Data.Tombstones.Should().Contain((EntityKind.ChecklistItem, drop.Value));

        var revived = await Save(id, 3, Draft(), items: [new(drop, "Back again", false)]);
        Failed<long, RecordNotFoundError>(revived).WasDeleted.Should().BeTrue("B-05: a deleted item cannot reappear from a stale draft");
    }

    [Fact]
    [Trait("Req", "B-02.7")]
    public async Task Blank_checklist_text_is_a_validation_error_not_a_deletion()
    {
        var id = await _workspace.AddTaskAsync();
        var result = await Save(id, 1, Draft(), items: [new(new ChecklistItemId(Guid.NewGuid()), "  ", false)]);

        Failed<long, ValidationFailedError>(result).Errors.Should().ContainSingle().Which.Field.Should().Be("ChecklistItems");
        _workspace.Coordinator.TransactionsStarted.Should().Be(1, "only the arrange step ran a transaction");
    }

    [Fact]
    [Trait("Req", "B-02.6")]
    public async Task New_people_and_tags_are_created_by_the_save_and_existing_names_are_reused_case_insensitively()
    {
        var id = await _workspace.AddTaskAsync();
        var carol = new NewPerson(new PersonId(Guid.NewGuid()), "  Carol ");
        var aliceAgain = new NewPerson(new PersonId(Guid.NewGuid()), "alice");
        var urgentTag = new NewTag(new TagId(Guid.NewGuid()), "Hotfix");
        var hotfixTwice = new NewTag(new TagId(Guid.NewGuid()), "HOTFIX");
        var draft = Draft() with
        {
            DesignerIds = [carol.Id, aliceAgain.Id],
            DeveloperIds = [carol.Id],
            TagIds = [urgentTag.Id, hotfixTwice.Id],
        };

        Succeeded(await Save(id, 1, draft, lookups: new NewLookups([carol, aliceAgain], [urgentTag, hotfixTwice])));

        var task = _workspace.Task(id);
        var workflow = _workspace.Data.Workflow;
        workflow.People.Select(p => p.Name.Display).Should().BeEquivalentTo(["Alice", "Bob", "Carol"]);
        workflow.Tags.Select(t => t.Name.Display).Should().BeEquivalentTo(["Mobile", "Hotfix"]);
        task.DesignerIds.Should().Equal(carol.Id, Alice.Id);
        task.DeveloperIds.Should().Equal(carol.Id);
        task.TagIds.Should().Equal(urgentTag.Id);
        _workspace.Coordinator.Published[^1].Workflow.Should().NotBeNull("the new names are published with the save");
    }

    [Fact]
    [Trait("Req", "B-02.6")]
    public async Task A_failed_save_creates_no_people_or_tags()
    {
        var id = await _workspace.AddTaskAsync();
        var carol = new NewPerson(new PersonId(Guid.NewGuid()), "Carol");

        var result = await Save(id, 7, Draft() with { DesignerIds = [carol.Id] }, lookups: new NewLookups([carol], []));

        Failed<long, ConcurrencyConflictError>(result);
        _workspace.Data.Workflow.People.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Req", "B-02.4")]
    [Trait("Req", "AC-28")]
    public async Task A_link_confirmed_in_the_editor_is_created_with_the_save_and_keeps_the_own_title_hidden()
    {
        var cr = await _workspace.AddCrAsync();
        var id = await _workspace.AddTaskAsync("My own title");

        Succeeded(await Save(id, 1, Draft("My own title", "Cards API"), linkTo: new CrLinkTarget(cr, 1)));

        var task = _workspace.Task(id);
        task.ChangeRequestId.Should().Be(cr);
        task.OwnTitle.Should().BeNull();
        task.Preserved.PreLinkTitle.Should().Be("My own title");
        task.Revision.Should().Be(2);
        _workspace.Data.Links.Should().ContainKey(id);
        _workspace.Data.AssertIntegrity();
    }

    [Fact]
    [Trait("Req", "T-13")]
    public async Task A_link_against_a_changed_cr_is_a_conflict_and_nothing_is_saved()
    {
        var cr = await _workspace.AddCrAsync();
        var id = await _workspace.AddTaskAsync();

        var result = await Save(id, 1, Draft("Card freeze", "Cards API"), linkTo: new CrLinkTarget(cr, 9));

        Failed<long, ConcurrencyConflictError>(result).Entity.Should().Be(EntityKind.ChangeRequest);
        _workspace.Task(id).Revision.Should().Be(1);
        _workspace.Data.Links.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "AC-29")]
    public async Task A_linked_task_cannot_be_linked_again_through_the_editor()
    {
        var first = await _workspace.AddCrAsync();
        var second = await _workspace.AddCrAsync("CR-2000");
        var id = await _workspace.AddLinkedTaskAsync(first);

        var result = await Save(id, 1, Draft(null, "Mobile Banking"), linkTo: new CrLinkTarget(second, 1));

        Failed<long, ValidationFailedError>(result).Errors.Single().Code.Should().Be(DomainErrorCodes.TaskAlreadyLinked);
        _workspace.Task(id).ChangeRequestId.Should().Be(first);
    }

    [Fact]
    [Trait("Req", "B-05")]
    public async Task An_archived_task_cannot_be_edited()
    {
        var id = await _workspace.AddTaskAsync();
        Succeeded(await new ArchiveTask(_workspace.Coordinator).ExecuteAsync(new ArchiveTaskCommand(OperationId.New(), id, 1), TestContext.Current.CancellationToken));

        var result = await Save(id, 2, Draft("Edited"));

        Failed<long, ValidationFailedError>(result).Errors.Single().Code.Should().Be(DomainErrorCodes.ArchivedReadOnly);
    }

    [Fact]
    [Trait("Req", "B-02.8")]
    public async Task Creating_from_the_editor_saves_checklist_items_and_new_tags_together()
    {
        var id = new TaskId(Guid.NewGuid());
        var tag = new NewTag(new TagId(Guid.NewGuid()), "Release");
        var item = new ChecklistItemId(Guid.NewGuid());

        Succeeded(await new CreateTask(_workspace.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), id, Draft() with { TagIds = [tag.Id] }, Review.Id,
                ChecklistItems: [new(item, "First step", false)], NewLookups: new NewLookups([], [tag])),
            TestContext.Current.CancellationToken));

        var task = _workspace.Task(id);
        task.StatusId.Should().Be(Review.Id);
        task.ChecklistItems.Single().Id.Should().Be(item);
        task.TagIds.Should().Equal(tag.Id);
    }

    [Fact]
    [Trait("Req", "B-05")]
    public async Task Archive_and_restore_put_the_task_back_at_the_end_of_its_column()
    {
        var id = await _workspace.AddTaskAsync("First");
        Succeeded(await new ArchiveTask(_workspace.Coordinator).ExecuteAsync(new ArchiveTaskCommand(OperationId.New(), id, 1), TestContext.Current.CancellationToken));
        var later = await _workspace.AddTaskAsync("Added while archived");
        _workspace.Task(id).IsArchived.Should().BeTrue();

        Succeeded(await new RestoreTask(_workspace.Coordinator).ExecuteAsync(new RestoreTaskCommand(OperationId.New(), id, 2), TestContext.Current.CancellationToken));

        var restored = _workspace.Task(id);
        restored.IsArchived.Should().BeFalse();
        restored.Rank.Should().BeGreaterThan(_workspace.Task(later).Rank, "B-05: restore inserts at the end of the column");
    }

    [Fact]
    [Trait("Req", "B-05")]
    [Trait("Req", "T-06.3")]
    public async Task Deleting_a_task_removes_its_cards_items_and_link_atomically_and_keeps_its_cr()
    {
        var cr = await _workspace.AddCrAsync();
        var id = await _workspace.AddLinkedTaskAsync(cr);
        var item = new ChecklistItemId(Guid.NewGuid());
        Succeeded(await Save(id, 1, Draft(null, "Mobile Banking"), items: [new(item, "Step", false)]));
        var card = new SubtaskCardId(Guid.NewGuid());
        Succeeded(await new CreateSubtaskCard(_workspace.Coordinator).ExecuteAsync(new CreateSubtaskCardCommand(OperationId.New(), id, card, "Follow-up"), TestContext.Current.CancellationToken));

        var summary = Succeeded(await new DeleteTask(_workspace.Coordinator).ExecuteAsync(new DeleteTaskCommand(OperationId.New(), id, 2), TestContext.Current.CancellationToken)).Value;

        summary.Should().Be(new TaskDeletionSummary(1, 1));
        _workspace.Data.Tasks.Should().NotContainKey(id);
        _workspace.Data.SubtaskCards.Should().BeEmpty();
        _workspace.Data.Links.Should().BeEmpty();
        _workspace.Data.ChangeRequests.Should().ContainKey(cr);
        _workspace.Data.Tombstones.Should().Contain([(EntityKind.Task, id.Value), (EntityKind.SubtaskCard, card.Value), (EntityKind.ChecklistItem, item.Value)]);
        var published = _workspace.Coordinator.Published[^1];
        published.DeletedTasks.Should().Equal(id);
        published.DeletedSubtaskCards.Should().Equal(card);
        published.RemovedLinks.Should().ContainSingle();
    }

    [Fact]
    [Trait("Req", "AC-07")]
    public async Task Deleting_from_a_stale_confirmation_is_a_conflict()
    {
        var id = await _workspace.AddTaskAsync();
        Succeeded(await Save(id, 1, Draft("Changed meanwhile")));

        Failed<TaskDeletionSummary, ConcurrencyConflictError>(await new DeleteTask(_workspace.Coordinator).ExecuteAsync(new DeleteTaskCommand(OperationId.New(), id, 1), TestContext.Current.CancellationToken));
        _workspace.Data.Tasks.Should().ContainKey(id);
    }

    [Fact]
    [Trait("Req", "B-16.7")]
    public async Task A_subtask_card_is_deleted_alone_and_the_parent_is_unchanged()
    {
        var id = await _workspace.AddTaskAsync();
        var card = new SubtaskCardId(Guid.NewGuid());
        Succeeded(await new CreateSubtaskCard(_workspace.Coordinator).ExecuteAsync(new CreateSubtaskCardCommand(OperationId.New(), id, card, "Follow-up"), TestContext.Current.CancellationToken));

        Succeeded(await new DeleteSubtaskCard(_workspace.Coordinator).ExecuteAsync(new DeleteSubtaskCardCommand(OperationId.New(), card, 1), TestContext.Current.CancellationToken));

        _workspace.Data.SubtaskCards.Should().BeEmpty();
        _workspace.Task(id).Revision.Should().Be(1);
        _workspace.Data.Tombstones.Should().Contain((EntityKind.SubtaskCard, card.Value));
    }

    [Fact]
    [Trait("Req", "B-16.6")]
    public async Task A_card_of_an_archived_parent_cannot_be_deleted()
    {
        var id = await _workspace.AddTaskAsync();
        var card = new SubtaskCardId(Guid.NewGuid());
        Succeeded(await new CreateSubtaskCard(_workspace.Coordinator).ExecuteAsync(new CreateSubtaskCardCommand(OperationId.New(), id, card, "Follow-up"), TestContext.Current.CancellationToken));
        Succeeded(await new ArchiveTask(_workspace.Coordinator).ExecuteAsync(new ArchiveTaskCommand(OperationId.New(), id, 1), TestContext.Current.CancellationToken));

        var result = await new DeleteSubtaskCard(_workspace.Coordinator).ExecuteAsync(new DeleteSubtaskCardCommand(OperationId.New(), card, 1), TestContext.Current.CancellationToken);

        Failed<SubtaskCardId, ValidationFailedError>(result).Errors.Single().Code.Should().Be(DomainErrorCodes.ArchivedReadOnly);
    }

    private Task<AppResult<CommandOutcome<long>>> Save(
        TaskId id,
        long revision,
        TaskContentDraft draft,
        TaskStatusId? status = null,
        PriorityId? priority = null,
        IReadOnlyList<ChecklistItemDraft>? items = null,
        NewLookups? lookups = null,
        CrLinkTarget? linkTo = null)
    {
        var current = _workspace.Data.Tasks[id];
        return new UpdateTask(_workspace.Coordinator).ExecuteAsync(
            new UpdateTaskCommand(OperationId.New(), id, revision, draft, status ?? current.StatusId, priority ?? current.PriorityId,
                DateIntent.Unchanged, DateIntent.Unchanged,
                items ?? [.. current.ChecklistItems.Select(i => new ChecklistItemDraft(i.Id, i.Text.Value, i.IsCompleted))],
                lookups, linkTo),
            CancellationToken.None);
    }
}
