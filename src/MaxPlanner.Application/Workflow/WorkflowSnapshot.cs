using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Workflow;

/// <summary>Workspace defaults stored with the business data (T-05 <c>app_metadata</c>, B-04).</summary>
/// <param name="DefaultCrOwnerId">Null until the user chooses one (B-04); prefilled into new CRs.</param>
/// <param name="CalendarSchedulingStatusId">B-08.5: the CR status set when a backlog CR is scheduled.</param>
/// <param name="CrBacklogStatusId">B-08.11: the CR status set by "Move to backlog".</param>
public sealed record WorkflowDefaults(
    TaskStatusId DefaultTaskStatusId,
    CrStatusId DefaultCrStatusId,
    PriorityId DefaultPriorityId,
    PersonId? DefaultCrOwnerId,
    CrStatusId CalendarSchedulingStatusId,
    CrStatusId CrBacklogStatusId);

/// <summary>
/// All configurable lookups, field definitions, and defaults at one point in time. Small enough to
/// read whole inside a transaction and to publish whole when any part changes.
/// </summary>
public sealed record WorkflowSnapshot(
    IReadOnlyList<TaskStatusDefinition> TaskStatuses,
    IReadOnlyList<CrStatusDefinition> CrStatuses,
    IReadOnlyList<PriorityDefinition> Priorities,
    IReadOnlyList<SquadDefinition> Squads,
    IReadOnlyList<PersonDefinition> People,
    IReadOnlyList<TagDefinition> Tags,
    IReadOnlyList<FieldDefinition> Fields,
    WorkflowDefaults Defaults)
{
    public TaskStatusDefinition? FindTaskStatus(TaskStatusId id) => TaskStatuses.FirstOrDefault(s => s.Id == id);

    public CrStatusDefinition? FindCrStatus(CrStatusId id) => CrStatuses.FirstOrDefault(s => s.Id == id);

    public PriorityDefinition? FindPriority(PriorityId id) => Priorities.FirstOrDefault(p => p.Id == id);

    public SquadDefinition? FindSquad(SquadId id) => Squads.FirstOrDefault(s => s.Id == id);

    public PersonDefinition? FindPerson(PersonId id) => People.FirstOrDefault(p => p.Id == id);

    public TagDefinition? FindTag(TagId id) => Tags.FirstOrDefault(t => t.Id == id);

    /// <summary>B-16.3: the first status flagged completed in configured order, or null if none is flagged.</summary>
    public TaskStatusDefinition? FirstCompletedTaskStatus() =>
        TaskStatuses.Where(s => s.IsCompleted).OrderBy(s => s.Order).FirstOrDefault();

    public IReadOnlyList<FieldDefinition> FieldsFor(FieldTarget target) => [.. Fields.Where(f => f.Target == target)];
}
