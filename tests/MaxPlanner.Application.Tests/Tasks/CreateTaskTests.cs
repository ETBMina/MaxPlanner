using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Tasks;

[Trait("Req", "B-02")]
public sealed class CreateTaskTests
{
    private readonly TestWorkspace _workspace = new();

    [Fact]
    [Trait("Req", "B-07")]
    public async Task A_new_task_uses_the_workflow_defaults_and_appends_after_every_card_in_its_column()
    {
        var first = await _workspace.AddTaskAsync("First");
        Succeeded(await new CreateSubtaskCard(_workspace.Coordinator).ExecuteAsync(
            new CreateSubtaskCardCommand(OperationId.New(), first, new SubtaskCardId(Guid.NewGuid()), "Card in backlog"), CancellationToken.None));

        var second = _workspace.Task(await _workspace.AddTaskAsync("Second"));

        second.StatusId.Should().Be(Backlog.Id);
        second.PriorityId.Should().Be(Medium.Id);
        second.Revision.Should().Be(1);
        second.Rank.Should().Be(3 * RankPolicy.Step, "the Subtask card shares the column and holds rank 2048");
        second.Dates.Should().Be(ActualDates.None);
    }

    [Fact]
    [Trait("Req", "B-04")]
    public async Task Creating_in_a_start_setting_status_stamps_today()
    {
        _workspace.Task(await _workspace.AddTaskAsync(status: InProgress)).Dates.Start.Should().Be(Today);
    }

    [Fact]
    [Trait("Req", "B-02.9")]
    public async Task Every_draft_text_error_is_reported_together_before_any_transaction()
    {
        var draft = Draft(title: " ", service: new string('s', 251), notes: new string('n', 100_001));

        var result = await new CreateTask(_workspace.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), draft), CancellationToken.None);

        Failed<TaskId, ValidationFailedError>(result).Errors.Should().BeEquivalentTo([
            new DomainError(DomainErrorCodes.TooLong, "Service", 250),
            new DomainError(DomainErrorCodes.TooLong, "Notes", 100_000),
            new DomainError(DomainErrorCodes.Required, "Title"),
        ]);
        _workspace.Coordinator.TransactionsStarted.Should().Be(0);
    }

    [Fact]
    [Trait("Req", "B-11")]
    public async Task Reference_and_custom_field_errors_found_in_the_transaction_are_reported_together()
    {
        var ticket = FieldDefinition.Create(new FieldId(Guid.NewGuid()), FieldKey.Create("ticket").Value, FieldTarget.Task,
            LookupName.Create("Ticket").Value, FieldKind.Text, isRequired: true, null, 0, []).Value;
        _workspace.Data.Workflow = SeedWorkflow with { Fields = [ticket] };
        var draft = Draft() with { DesignerIds = [new PersonId(Guid.NewGuid())], TagIds = [new TagId(Guid.NewGuid())] };

        var result = await new CreateTask(_workspace.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), draft), CancellationToken.None);

        Failed<TaskId, ValidationFailedError>(result).Errors.Should().BeEquivalentTo([
            new DomainError(DomainErrorCodes.UnknownReference, "Designers"),
            new DomainError(DomainErrorCodes.UnknownReference, "Tags"),
            new DomainError(DomainErrorCodes.Required, "Custom:ticket"),
        ]);
        _workspace.Data.Tasks.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "B-03.5")]
    [Trait("Req", "AC-04")]
    public async Task Creating_from_a_cr_creates_the_task_and_its_permanent_link_in_one_commit()
    {
        var cr = await _workspace.AddCrAsync();

        var task = _workspace.Task(await _workspace.AddLinkedTaskAsync(cr));

        task.ChangeRequestId.Should().Be(cr);
        task.OwnTitle.Should().BeNull("the title is inherited from the CR");
        _workspace.Data.Links[task.Id].ChangeRequestId.Should().Be(cr);
        var changes = _workspace.Coordinator.Published[^1];
        changes.UpsertedTasks.Should().ContainSingle().Which.Id.Should().Be(task.Id);
        changes.AddedLinks.Should().ContainSingle().Which.ChangeRequestId.Should().Be(cr);
    }

    [Fact]
    [Trait("Req", "T-13")]
    public async Task Creating_from_a_stale_cr_preview_is_a_conflict_and_writes_nothing()
    {
        var cr = await _workspace.AddCrAsync();

        var result = await new CreateTask(_workspace.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft(null, "Cards API"), LinkTo: new CrLinkTarget(cr, ExpectedRevision: 0)),
            CancellationToken.None);

        Failed<TaskId, ConcurrencyConflictError>(result).Should().Be(new ConcurrencyConflictError(EntityKind.ChangeRequest, cr.Value, 0, 1));
        _workspace.Data.Tasks.Should().BeEmpty();
        _workspace.Data.Links.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "B-02.2")]
    public async Task A_linked_task_takes_no_own_title_and_needs_a_service()
    {
        var cr = await _workspace.AddCrAsync();

        var result = await new CreateTask(_workspace.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft("Own title", service: null), LinkTo: new CrLinkTarget(cr, 1)),
            CancellationToken.None);

        Failed<TaskId, ValidationFailedError>(result).Errors.Select(e => e.Code).Should().Equal(DomainErrorCodes.TitleNotAllowedWhenLinked);
    }

    [Fact]
    [Trait("Req", "B-02.5")]
    public async Task Linking_to_a_deleted_cr_reports_that_it_was_deleted()
    {
        var cr = new ChangeRequestId(Guid.NewGuid());
        _workspace.Data.Tombstones.Add((EntityKind.ChangeRequest, cr.Value));

        var result = await new CreateTask(_workspace.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft(null, "Cards API"), LinkTo: new CrLinkTarget(cr, 1)),
            CancellationToken.None);

        Failed<TaskId, RecordNotFoundError>(result).WasDeleted.Should().BeTrue();
    }

    [Fact]
    [Trait("Req", "B-05")]
    public async Task A_deleted_task_id_cannot_be_reused_by_a_new_operation()
    {
        var id = new TaskId(Guid.NewGuid());
        _workspace.Data.Tombstones.Add((EntityKind.Task, id.Value));

        var result = await new CreateTask(_workspace.Coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), id, Draft()), CancellationToken.None);

        Failed<TaskId, RecordNotFoundError>(result).Should().Be(new RecordNotFoundError(EntityKind.Task, id.Value, WasDeleted: true));
    }

    [Fact]
    public async Task An_unknown_status_is_rejected()
    {
        var result = await new CreateTask(_workspace.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft(), StatusId: new TaskStatusId(Guid.NewGuid())), CancellationToken.None);

        Failed<TaskId, ValidationFailedError>(result).Errors.Should().Equal(new DomainError(DomainErrorCodes.UnknownReference, "Status"));
    }
}
