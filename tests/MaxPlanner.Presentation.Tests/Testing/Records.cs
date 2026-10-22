using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Presentation.Tests.Testing;

/// <summary>A small workflow and domain-record builders that go through the real domain factories.</summary>
internal static class Records
{
    public static readonly DateOnly Today = new(2026, 10, 22);

    public static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    public static TaskStatusDefinition Backlog { get; } = new(new TaskStatusId(Guid.NewGuid()), Name("Backlog"), 0, false, false, true);

    public static TaskStatusDefinition InProgress { get; } = new(new TaskStatusId(Guid.NewGuid()), Name("In Progress"), 1, false, true, false);

    public static TaskStatusDefinition Completed { get; } = new(new TaskStatusId(Guid.NewGuid()), Name("Completed"), 2, true, false, false);

    public static CrStatusDefinition CrBacklog { get; } = new(new CrStatusId(Guid.NewGuid()), Name("Backlog"), 0, false, true);

    public static CrStatusDefinition CrInProgress { get; } = new(new CrStatusId(Guid.NewGuid()), Name("In Progress"), 1, false, false);

    public static PriorityDefinition Medium { get; } = new(new PriorityId(Guid.NewGuid()), Name("Medium"), 0);

    public static SquadDefinition RetailCards { get; } = new(new SquadId(Guid.NewGuid()), Name("Retail Cards"), 0);

    public static SquadDefinition RetailFinance { get; } = new(new SquadId(Guid.NewGuid()), Name("Retail Finance"), 1);

    public static PersonDefinition Alice { get; } = new(new PersonId(Guid.NewGuid()), Name("Alice"));

    public static PersonDefinition Bob { get; } = new(new PersonId(Guid.NewGuid()), Name("Bob"));

    public static WorkflowSnapshot Workflow { get; } = new(
        [Backlog, InProgress, Completed],
        [CrBacklog, CrInProgress],
        [Medium],
        [RetailCards, RetailFinance],
        [Alice, Bob],
        [],
        [],
        new WorkflowDefaults(Backlog.Id, CrBacklog.Id, Medium.Id, null, CrInProgress.Id, CrBacklog.Id));

    public static LookupName Name(string name) => LookupName.Create(name).Value;

    public static TaskItem Task(string title = "Card freeze", TaskStatusDefinition? status = null, long rank = 1000, string service = "Mobile Banking") =>
        TaskItem.Create(new TaskId(Guid.NewGuid()), Content(title, service), status ?? Backlog, Medium.Id,
            DateIntent.Unchanged, DateIntent.Unchanged, null, rank, Today, Now).Value;

    public static TaskItem Rename(TaskItem task, string title) =>
        task.UpdateContent(Content(title, task.Service?.Value ?? "Mobile Banking"), Now).Value;

    public static TaskItem Move(TaskItem task, TaskStatusDefinition target, long? rank = null) =>
        task.MoveTo(StatusOf(task.StatusId), target, rank ?? task.Rank, DateIntent.Unchanged, DateIntent.Unchanged, Today, Now).Value;

    public static TaskItem Archive(TaskItem task) => task.Archive(Now).Value;

    public static ChangeRequest Cr(string number = "CR-1042", string title = "Instant card freeze", SquadId? squad = null, PersonId? owner = null, PlannedRange? planned = null)
    {
        var content = new ChangeRequestContent(CrNumber.Create(number).Value, TitleText.Create(title).Value, owner ?? Alice.Id, squad, Medium.Id,
            planned, null, null, null, TaskNumber.CreateOptional("0042").Value, CustomValue.NoValues);
        return ChangeRequest.Create(new ChangeRequestId(Guid.NewGuid()), content, CrInProgress, Now);
    }

    public static ChangeRequest EditCr(ChangeRequest cr, Func<ChangeRequestContent, ChangeRequestContent> change)
    {
        var content = new ChangeRequestContent(cr.Number, cr.Title, cr.OwnerId, cr.SquadId, cr.PriorityId, cr.Planned, cr.EmailSubject,
            cr.SolutionDesignUrl, cr.Description, cr.TaskNumber, cr.CustomValues);
        return cr.Update(change(content), CrInProgress, Now);
    }

    /// <summary>Links <paramref name="task"/> as LinkTaskToChangeRequest does: the task and its link row together.</summary>
    public static (TaskItem Task, CrTaskLink Link) Link(TaskItem task, ChangeRequest cr) =>
        (task.LinkTo(cr.Id, Now).Value, new CrTaskLink(task.Id, cr.Id, Now));

    public static SubtaskCard Card(TaskItem parent, string subtitle = "Copy fix", long rank = 5000) =>
        SubtaskCard.Create(new SubtaskCardId(Guid.NewGuid()), parent.Id, SubtitleText.Create(subtitle).Value, null, Backlog,
            DateIntent.Unchanged, DateIntent.Unchanged, rank, Today, Now).Value;

    public static ReviewItem Review(string title = "Review login flow") =>
        ReviewItem.Create(new ReviewItemId(Guid.NewGuid()), new ReviewContent(TitleText.Create(title).Value, null, null, [], null, null), 1, Now);

    public static GeneralTodo Todo(string text = "Renew certificate") =>
        GeneralTodo.Create(new GeneralTodoId(Guid.NewGuid()), ChecklistText.Create(text).Value, 1, Now);

    public static ReadSnapshot Snapshot(
        long sequence,
        IReadOnlyList<TaskItem>? tasks = null,
        IReadOnlyList<SubtaskCard>? cards = null,
        IReadOnlyList<ChangeRequest>? crs = null,
        IReadOnlyList<CrTaskLink>? links = null,
        IReadOnlyList<ReviewItem>? reviews = null,
        IReadOnlyList<GeneralTodo>? todos = null,
        WorkflowSnapshot? workflow = null) =>
        new(sequence, tasks ?? [], cards ?? [], crs ?? [], links ?? [], reviews ?? [], todos ?? [], workflow ?? Workflow);

    public static CommittedChangeSet Change(long sequence) => new()
    {
        Sequence = sequence,
        OperationId = OperationId.New(),
        OperationKind = "test",
        CommittedAtUtc = Now,
    };

    private static TaskContent Content(string title, string service) =>
        new(TitleText.Create(title).Value, ServiceText.CreateOptional(service).Value, [], [], [], null, CustomValue.NoValues);

    private static TaskStatusDefinition StatusOf(TaskStatusId id) => Workflow.FindTaskStatus(id)!;
}
