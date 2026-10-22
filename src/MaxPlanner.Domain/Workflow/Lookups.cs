using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Workflow;

// B-04/T-04: configurable lookups are records with stable IDs. Behavior comes from the explicit
// flags, never from matching the (renamable) label.

/// <summary>A Board column / task workflow status. Also used by Subtask cards.</summary>
public sealed record TaskStatusDefinition(
    TaskStatusId Id,
    LookupName Name,
    int Order,
    bool IsCompleted,
    bool SetsStartDate,
    bool IsBacklog);

public sealed record CrStatusDefinition(
    CrStatusId Id,
    LookupName Name,
    int Order,
    bool IsCompleted,
    bool IsBacklog);

/// <summary>Lower <see cref="Order"/> means higher priority (Urgent first).</summary>
public sealed record PriorityDefinition(PriorityId Id, LookupName Name, int Order);

public sealed record SquadDefinition(SquadId Id, LookupName Name, int Order);

public sealed record PersonDefinition(PersonId Id, LookupName Name);

public sealed record TagDefinition(TagId Id, LookupName Name);
