using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Domain.Tests;

/// <summary>The B-04 seed workflow plus small builders shared by the domain tests.</summary>
internal static class Fixtures
{
    public static readonly DateOnly Today = new(2026, 10, 22);
    public static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    public static readonly TaskStatusDefinition Backlog = Status("Backlog", isBacklog: true);
    public static readonly TaskStatusDefinition Blocked = Status("Blocked");
    public static readonly TaskStatusDefinition InProgress = Status("In Progress", setsStart: true);
    public static readonly TaskStatusDefinition Review = Status("Review");
    public static readonly TaskStatusDefinition Completed = Status("Completed", completed: true);
    public static readonly TaskStatusDefinition Developing = Status("Developing", setsStart: true);

    public static readonly PriorityId Medium = new(Guid.NewGuid());

    public static TaskStatusDefinition Status(string name, bool completed = false, bool setsStart = false, bool isBacklog = false) =>
        new(new TaskStatusId(Guid.NewGuid()), LookupName.Create(name).Value, 0, completed, setsStart, isBacklog);

    public static TitleText Title(string text = "Card freeze") => TitleText.Create(text).Value;

    public static ServiceText Service(string text = "Mobile Banking") => ServiceText.CreateOptional(text).Value!;

    public static TaskContent Content(TitleText? ownTitle = null, ServiceText? service = null, bool unlinkedDefaults = true) =>
        new(ownTitle ?? (unlinkedDefaults ? Title() : null), service, null, null, [], null, CustomValue.NoValues);

    public static TaskItem UnlinkedTask(TaskStatusDefinition? status = null, ServiceText? service = null) =>
        TaskItem.Create(new TaskId(Guid.NewGuid()), Content(service: service), status ?? Backlog, Medium,
            DateIntent.Unchanged, DateIntent.Unchanged, linkTo: null, rank: 1_024, Today, Now).Value;
}
