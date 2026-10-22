using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Tests.Fakes;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.ChangeRequests;

public sealed class ChangeRequestTests
{
    private readonly TestWorkspace _workspace = new();

    [Fact]
    [Trait("Req", "B-03.1")]
    public async Task A_new_cr_uses_the_default_status_and_priority_and_keeps_its_task_number_as_text()
    {
        var cr = _workspace.Cr(await _workspace.AddCrAsync());

        cr.StatusId.Should().Be(CrBacklog.Id);
        cr.PriorityId.Should().Be(Medium.Id);
        cr.OwnerId.Should().Be(Alice.Id);
        cr.TaskNumber!.Value.Should().Be("0042");
    }

    [Fact]
    [Trait("Req", "B-03.2")]
    public async Task A_duplicate_number_after_trim_and_case_normalization_identifies_the_existing_cr()
    {
        var existing = await _workspace.AddCrAsync("CR-1042");

        var result = await Create(CrDraft("  cr-1042 ", "Another"));

        Failed<ChangeRequestId, DuplicateCrNumberError>(result).Should().Be(new DuplicateCrNumberError("CR-1042", existing));
        _workspace.Data.ChangeRequests.Should().ContainSingle();
    }

    [Fact]
    [Trait("Req", "B-02.9")]
    [Trait("Req", "AC-31")]
    public async Task Every_cr_field_error_is_reported_together_and_an_owner_is_required()
    {
        var draft = CrDraft(" ", " ") with { OwnerId = null, PlannedStart = Today };

        var result = await Create(draft);

        Failed<ChangeRequestId, ValidationFailedError>(result).Errors.Should().BeEquivalentTo([
            new DomainError(DomainErrorCodes.Required, "Number"),
            new DomainError(DomainErrorCodes.Required, "Title"),
            new DomainError(DomainErrorCodes.Required, "PlannedEnd"),
            new DomainError(DomainErrorCodes.Required, "Owner"),
        ]);
    }

    [Fact]
    public async Task An_unknown_owner_or_squad_is_rejected()
    {
        var result = await Create(CrDraft() with { OwnerId = new PersonId(Guid.NewGuid()), SquadId = new SquadId(Guid.NewGuid()) });

        Failed<ChangeRequestId, ValidationFailedError>(result).Errors.Should().BeEquivalentTo([
            new DomainError(DomainErrorCodes.UnknownReference, "Owner"),
            new DomainError(DomainErrorCodes.UnknownReference, "Squad"),
        ]);
    }

    [Fact]
    [Trait("Req", "B-03.6")]
    [Trait("Req", "AC-28")]
    public async Task Linking_an_unlinked_task_is_permanent_and_moves_its_title_to_preserved_metadata()
    {
        var cr = await _workspace.AddCrAsync();
        var task = await _workspace.AddTaskAsync("My own title", service: "Cards API");

        var outcome = Succeeded(await Link(task, 1, cr, 1));

        var linked = _workspace.Task(task);
        outcome.Value.Should().Be(2);
        linked.ChangeRequestId.Should().Be(cr);
        linked.OwnTitle.Should().BeNull();
        linked.Preserved.PreLinkTitle.Should().Be("My own title");
        _workspace.Data.Links[task].ChangeRequestId.Should().Be(cr);
        _workspace.Coordinator.Published[^1].AddedLinks.Should().ContainSingle().Which.TaskId.Should().Be(task);
    }

    [Fact]
    [Trait("Req", "B-03.7")]
    [Trait("Req", "AC-29")]
    public async Task A_linked_task_cannot_be_relinked_to_another_cr()
    {
        var first = await _workspace.AddCrAsync("CR-1");
        var second = await _workspace.AddCrAsync("CR-2");
        var task = await _workspace.AddLinkedTaskAsync(first);

        var result = await Link(task, 1, second, 1);

        Failed<long, ValidationFailedError>(result).Errors.Should().Equal(new DomainError(DomainErrorCodes.TaskAlreadyLinked, "ChangeRequest"));
        _workspace.Data.Links[task].ChangeRequestId.Should().Be(first);
    }

    [Fact]
    [Trait("Req", "B-03.6")]
    public async Task An_archived_unlinked_task_can_still_be_linked()
    {
        var cr = await _workspace.AddCrAsync();
        var task = await _workspace.AddTaskAsync(service: "Cards API");
        _workspace.Arrange(t => t.Archive(_workspace.Time.GetUtcNow()).Value, task);

        Succeeded(await Link(task, 2, cr, 1));

        _workspace.Task(task).IsArchived.Should().BeTrue();
        _workspace.Task(task).ChangeRequestId.Should().Be(cr);
    }

    [Fact]
    [Trait("Req", "T-13")]
    public async Task Linking_against_a_stale_cr_preview_is_a_conflict()
    {
        var cr = await _workspace.AddCrAsync();
        var task = await _workspace.AddTaskAsync(service: "Cards API");

        Failed<long, ConcurrencyConflictError>(await Link(task, 1, cr, 7)).Entity.Should().Be(EntityKind.ChangeRequest);
        _workspace.Data.Links.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "B-05")]
    [Trait("Req", "AC-30")]
    public async Task Deleting_a_cr_removes_its_links_tasks_checklist_items_and_subtask_cards_in_one_commit()
    {
        var cr = await _workspace.AddCrAsync("CR-1");
        var keptCr = await _workspace.AddCrAsync("CR-2");
        var active = await _workspace.AddLinkedTaskAsync(cr);
        var archived = await _workspace.AddLinkedTaskAsync(cr, "Statements");
        var keptTask = await _workspace.AddLinkedTaskAsync(keptCr);
        var item = new ChecklistItemId(Guid.NewGuid());
        Succeeded(await new AddChecklistItem(_workspace.Coordinator).ExecuteAsync(
            new AddChecklistItemCommand(OperationId.New(), active, 1, item, "Haptics"), CancellationToken.None));
        var card = Succeeded(await new CreateSubtaskCard(_workspace.Coordinator).ExecuteAsync(
            new CreateSubtaskCardCommand(OperationId.New(), active, new SubtaskCardId(Guid.NewGuid()), "Follow-up"), CancellationToken.None)).Value;
        _workspace.Arrange(t => t.Archive(_workspace.Time.GetUtcNow()).Value, archived);
        var sequenceBefore = _workspace.Data.CommitSequence;

        var outcome = Succeeded(await Delete(cr, [active, archived]));

        outcome.Value.Should().Be(new CascadeDeletionSummary(TaskCount: 2, ChecklistItemCount: 1, SubtaskCardCount: 1));
        outcome.CommitSequence.Should().Be(sequenceBefore + 1);
        _workspace.Data.ChangeRequests.Keys.Should().Equal(keptCr);
        _workspace.Data.Tasks.Keys.Should().Equal(keptTask);
        _workspace.Data.Links.Keys.Should().Equal(keptTask);
        _workspace.Data.SubtaskCards.Should().BeEmpty();
        _workspace.Data.Tombstones.Should().BeEquivalentTo([
            (EntityKind.ChangeRequest, cr.Value), (EntityKind.Task, active.Value), (EntityKind.Task, archived.Value),
            (EntityKind.ChecklistItem, item.Value), (EntityKind.SubtaskCard, card.Value),
        ]);
        var changes = _workspace.Coordinator.Published[^1];
        changes.DeletedChangeRequests.Should().Equal(cr);
        changes.DeletedTasks.Should().BeEquivalentTo([active, archived]);
        changes.DeletedSubtaskCards.Should().Equal(card);
        changes.RemovedLinks.Select(l => l.TaskId).Should().BeEquivalentTo([active, archived]);
    }

    [Fact]
    [Trait("Req", "AC-30")]
    public async Task A_task_linked_after_the_confirmation_refuses_the_deletion()
    {
        var cr = await _workspace.AddCrAsync();
        var confirmed = await _workspace.AddLinkedTaskAsync(cr);
        await _workspace.AddLinkedTaskAsync(cr, "Late arrival");

        var result = await Delete(cr, [confirmed]);

        Failed<CascadeDeletionSummary, ValidationFailedError>(result).Errors.Should().Equal(new DomainError(DomainErrorCodes.CascadeScopeChanged));
        _workspace.Data.Tasks.Should().HaveCount(2);
        _workspace.Data.ChangeRequests.Should().ContainKey(cr);
    }

    [Fact]
    [Trait("Req", "AC-30")]
    public async Task A_failure_in_the_middle_of_a_cascade_leaves_everything_intact()
    {
        var cr = await _workspace.AddCrAsync();
        var first = await _workspace.AddLinkedTaskAsync(cr);
        var second = await _workspace.AddLinkedTaskAsync(cr, "Statements");
        var published = _workspace.Coordinator.Published.Count;
        var deletes = 0;
        _workspace.Coordinator.BeforeWrite = write =>
        {
            if (write == "tasks.delete" && ++deletes == 2)
            {
                throw new FakeStorageException("disk full");
            }
        };

        var result = await Delete(cr, [first, second]);

        Failed<CascadeDeletionSummary, StorageFailureError>(result);
        _workspace.Data.Tasks.Keys.Should().BeEquivalentTo([first, second]);
        _workspace.Data.Links.Should().HaveCount(2);
        _workspace.Data.ChangeRequests.Should().ContainKey(cr);
        _workspace.Data.Tombstones.Should().BeEmpty();
        _workspace.Coordinator.Published.Should().HaveCount(published);
    }

    [Fact]
    [Trait("Req", "B-05")]
    public async Task A_stale_editor_of_a_cascade_deleted_task_learns_it_was_deleted()
    {
        var cr = await _workspace.AddCrAsync();
        var task = await _workspace.AddLinkedTaskAsync(cr);
        Succeeded(await Delete(cr, [task]));

        var result = await new UpdateTaskContent(_workspace.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), task, 1, Draft(null, "Cards API")), CancellationToken.None);

        Failed<long, RecordNotFoundError>(result).Should().Be(new RecordNotFoundError(EntityKind.Task, task.Value, WasDeleted: true));
    }

    private Task<AppResult<CommandOutcome<ChangeRequestId>>> Create(ChangeRequestDraft draft) =>
        new CreateChangeRequest(_workspace.Coordinator).ExecuteAsync(
            new CreateChangeRequestCommand(OperationId.New(), new ChangeRequestId(Guid.NewGuid()), draft), CancellationToken.None);

    private Task<AppResult<CommandOutcome<long>>> Link(TaskId task, long taskRevision, ChangeRequestId cr, long crRevision) =>
        new LinkTaskToChangeRequest(_workspace.Coordinator).ExecuteAsync(
            new LinkTaskToChangeRequestCommand(OperationId.New(), task, taskRevision, cr, crRevision), CancellationToken.None);

    private Task<AppResult<CommandOutcome<CascadeDeletionSummary>>> Delete(ChangeRequestId cr, TaskId[] confirmed) =>
        new DeleteChangeRequest(_workspace.Coordinator).ExecuteAsync(
            new DeleteChangeRequestCommand(OperationId.New(), cr, _workspace.Cr(cr).Revision, confirmed), CancellationToken.None);
}
