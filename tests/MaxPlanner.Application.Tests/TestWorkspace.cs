using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Tests.Fakes;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;
using Microsoft.Extensions.Time.Testing;

namespace MaxPlanner.Application.Tests;

/// <summary>
/// A dataset seeded with the B-04 default workflow, the fake coordinator over it, and shortcuts that
/// create records through the real use cases.
/// </summary>
internal sealed class TestWorkspace
{
    public static readonly DateOnly Today = new(2026, 10, 22);

    public TestWorkspace()
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
        Coordinator = new FakeMutationCoordinator(new InMemoryDataset(SeedWorkflow), Time);
    }

    public static TaskStatusDefinition Backlog { get; } = TaskStatus("Backlog", 0, isBacklog: true);

    public static TaskStatusDefinition Blocked { get; } = TaskStatus("Blocked", 1);

    public static TaskStatusDefinition InProgress { get; } = TaskStatus("In Progress", 2, setsStart: true);

    public static TaskStatusDefinition Review { get; } = TaskStatus("Review", 3);

    public static TaskStatusDefinition UnitTesting { get; } = TaskStatus("Unit Testing", 4);

    public static TaskStatusDefinition Completed { get; } = TaskStatus("Completed", 5, completed: true);

    public static TaskStatusDefinition Designing { get; } = TaskStatus("Designing", 6, setsStart: true);

    public static TaskStatusDefinition Developing { get; } = TaskStatus("Developing", 7, setsStart: true);

    public static CrStatusDefinition CrBacklog { get; } = new(new CrStatusId(Guid.NewGuid()), Name("Backlog"), 0, false, true);

    public static CrStatusDefinition CrInProgress { get; } = new(new CrStatusId(Guid.NewGuid()), Name("In Progress"), 1, false, false);

    public static CrStatusDefinition CrCompleted { get; } = new(new CrStatusId(Guid.NewGuid()), Name("Completed"), 2, true, false);

    public static PriorityDefinition Urgent { get; } = new(new PriorityId(Guid.NewGuid()), Name("Urgent"), 0);

    public static PriorityDefinition High { get; } = new(new PriorityId(Guid.NewGuid()), Name("High"), 1);

    public static PriorityDefinition Medium { get; } = new(new PriorityId(Guid.NewGuid()), Name("Medium"), 2);

    public static PriorityDefinition Low { get; } = new(new PriorityId(Guid.NewGuid()), Name("Low"), 3);

    public static SquadDefinition RetailFinance { get; } = new(new SquadId(Guid.NewGuid()), Name("Retail Finance"), 0);

    public static SquadDefinition RetailCards { get; } = new(new SquadId(Guid.NewGuid()), Name("Retail Cards"), 1);

    public static PersonDefinition Alice { get; } = new(new PersonId(Guid.NewGuid()), Name("Alice"));

    public static PersonDefinition Bob { get; } = new(new PersonId(Guid.NewGuid()), Name("Bob"));

    public static TagDefinition Mobile { get; } = new(new TagId(Guid.NewGuid()), Name("Mobile"));

    public static WorkflowSnapshot SeedWorkflow { get; } = new(
        [Backlog, Blocked, InProgress, Review, UnitTesting, Completed, Designing, Developing],
        [CrBacklog, CrInProgress, CrCompleted],
        [Urgent, High, Medium, Low],
        [RetailFinance, RetailCards],
        [Alice, Bob],
        [Mobile],
        [],
        new WorkflowDefaults(Backlog.Id, CrBacklog.Id, Medium.Id, DefaultCrOwnerId: null, CrInProgress.Id, CrBacklog.Id));

    public FakeTimeProvider Time { get; }

    public FakeMutationCoordinator Coordinator { get; }

    public InMemoryDataset Data => Coordinator.Committed;

    public static TaskContentDraft Draft(string? title = "Card freeze", string? service = null, string? notes = null) =>
        new(title, service, null, null, [], notes, CustomValue.NoValues);

    public static ChangeRequestDraft CrDraft(string number = "CR-1042", string title = "Instant card freeze", PersonDefinition? owner = null, SquadId? squad = null) =>
        new(number, title, (owner ?? Alice).Id, squad, null, null, null, null, null, null, "0042", CustomValue.NoValues);

    public TaskItem Task(TaskId id) => Data.Tasks[id];

    public ChangeRequest Cr(ChangeRequestId id) => Data.ChangeRequests[id];

    public SubtaskCard Card(SubtaskCardId id) => Data.SubtaskCards[id];

    public async Task<TaskId> AddTaskAsync(string title = "Card freeze", TaskStatusDefinition? status = null, string? service = null)
    {
        var id = new TaskId(Guid.NewGuid());
        return Succeeded(await new CreateTask(Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), id, Draft(title, service), status?.Id), CancellationToken.None)).Value;
    }

    public async Task<TaskId> AddLinkedTaskAsync(ChangeRequestId cr, string service = "Mobile Banking", TaskStatusDefinition? status = null)
    {
        var id = new TaskId(Guid.NewGuid());
        return Succeeded(await new CreateTask(Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), id, Draft(title: null, service), status?.Id, LinkTo: new CrLinkTarget(cr, Cr(cr).Revision)),
            CancellationToken.None)).Value;
    }

    public async Task<ChangeRequestId> AddCrAsync(string number = "CR-1042", string title = "Instant card freeze", SquadId? squad = null)
    {
        var id = new ChangeRequestId(Guid.NewGuid());
        return Succeeded(await new CreateChangeRequest(Coordinator).ExecuteAsync(
            new CreateChangeRequestCommand(OperationId.New(), id, CrDraft(number, title, squad: squad)), CancellationToken.None)).Value;
    }

    /// <summary>Applies a domain change directly to the committed data, for arranging states no use case reaches yet.</summary>
    public void Arrange(Func<TaskItem, TaskItem> change, TaskId id) => Data.Tasks[id] = change(Data.Tasks[id]);

    public static CommandOutcome<T> Succeeded<T>(AppResult<CommandOutcome<T>> result)
    {
        result.Error.Should().BeNull();
        return result.Value;
    }

    public static TError Failed<T, TError>(AppResult<CommandOutcome<T>> result)
        where TError : AppError
    {
        result.IsSuccess.Should().BeFalse();
        return result.Error.Should().BeOfType<TError>().Subject;
    }

    private static TaskStatusDefinition TaskStatus(string name, int order, bool completed = false, bool setsStart = false, bool isBacklog = false) =>
        new(new TaskStatusId(Guid.NewGuid()), Name(name), order, completed, setsStart, isBacklog);

    private static LookupName Name(string name) => LookupName.Create(name).Value;
}
