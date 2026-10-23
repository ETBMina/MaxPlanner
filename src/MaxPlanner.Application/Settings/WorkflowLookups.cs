using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Settings;

/// <summary>One configurable lookup value, independent of its kind, as Settings lists it.</summary>
/// <param name="Order">The configured order; people and tags have none and list by name (0).</param>
public sealed record LookupEntry(LookupKind Kind, Guid Id, LookupName Name, int Order);

/// <summary>
/// Reads and rewrites the lookup lists of a <see cref="WorkflowSnapshot"/> by <see cref="LookupKind"/>, so the
/// Settings use cases share one implementation for statuses, priorities, squads, people, and tags.
/// </summary>
public static class WorkflowLookups
{
    /// <summary>True for lookups with a user-defined order (B-04: statuses, priorities, squads).</summary>
    public static bool IsOrdered(LookupKind kind) => kind is not (LookupKind.Person or LookupKind.Tag);

    /// <summary>The values of one lookup kind in display order (configured order, or name for people and tags).</summary>
    public static IReadOnlyList<LookupEntry> Entries(WorkflowSnapshot workflow, LookupKind kind)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        IEnumerable<LookupEntry> entries = kind switch
        {
            LookupKind.TaskStatus => workflow.TaskStatuses.Select(s => new LookupEntry(kind, s.Id.Value, s.Name, s.Order)),
            LookupKind.CrStatus => workflow.CrStatuses.Select(s => new LookupEntry(kind, s.Id.Value, s.Name, s.Order)),
            LookupKind.Priority => workflow.Priorities.Select(p => new LookupEntry(kind, p.Id.Value, p.Name, p.Order)),
            LookupKind.Squad => workflow.Squads.Select(s => new LookupEntry(kind, s.Id.Value, s.Name, s.Order)),
            LookupKind.Person => workflow.People.Select(p => new LookupEntry(kind, p.Id.Value, p.Name, 0)),
            LookupKind.Tag => workflow.Tags.Select(t => new LookupEntry(kind, t.Id.Value, t.Name, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return IsOrdered(kind)
            ? [.. entries.OrderBy(e => e.Order).ThenBy(e => e.Id)]
            : [.. entries.OrderBy(e => e.Name.Normalized, StringComparer.Ordinal).ThenBy(e => e.Id)];
    }

    public static LookupEntry? Find(WorkflowSnapshot workflow, LookupKind kind, Guid id) =>
        Entries(workflow, kind).FirstOrDefault(e => e.Id == id);

    /// <summary>Appends a new value at the end of the configured order; new statuses start without flags.</summary>
    public static WorkflowSnapshot Add(WorkflowSnapshot workflow, LookupKind kind, Guid id, LookupName name)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var order = Entries(workflow, kind) is { Count: > 0 } existing ? existing.Max(e => e.Order) + 1 : 0;
        return kind switch
        {
            LookupKind.TaskStatus => workflow with { TaskStatuses = [.. workflow.TaskStatuses, new TaskStatusDefinition(new TaskStatusId(id), name, order, false, false, false)] },
            LookupKind.CrStatus => workflow with { CrStatuses = [.. workflow.CrStatuses, new CrStatusDefinition(new CrStatusId(id), name, order, false, false)] },
            LookupKind.Priority => workflow with { Priorities = [.. workflow.Priorities, new PriorityDefinition(new PriorityId(id), name, order)] },
            LookupKind.Squad => workflow with { Squads = [.. workflow.Squads, new SquadDefinition(new SquadId(id), name, order)] },
            LookupKind.Person => workflow with { People = [.. workflow.People, new PersonDefinition(new PersonId(id), name)] },
            LookupKind.Tag => workflow with { Tags = [.. workflow.Tags, new TagDefinition(new TagId(id), name)] },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>B-04: a rename changes the label, never the identity, so every reference follows it.</summary>
    public static WorkflowSnapshot Rename(WorkflowSnapshot workflow, LookupKind kind, Guid id, LookupName name)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return kind switch
        {
            LookupKind.TaskStatus => workflow with { TaskStatuses = [.. workflow.TaskStatuses.Select(s => s.Id.Value == id ? s with { Name = name } : s)] },
            LookupKind.CrStatus => workflow with { CrStatuses = [.. workflow.CrStatuses.Select(s => s.Id.Value == id ? s with { Name = name } : s)] },
            LookupKind.Priority => workflow with { Priorities = [.. workflow.Priorities.Select(p => p.Id.Value == id ? p with { Name = name } : p)] },
            LookupKind.Squad => workflow with { Squads = [.. workflow.Squads.Select(s => s.Id.Value == id ? s with { Name = name } : s)] },
            LookupKind.Person => workflow with { People = [.. workflow.People.Select(p => p.Id.Value == id ? p with { Name = name } : p)] },
            LookupKind.Tag => workflow with { Tags = [.. workflow.Tags.Select(t => t.Id.Value == id ? t with { Name = name } : t)] },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>Gives each value of an ordered lookup its index in <paramref name="orderedIds"/> as its order.</summary>
    public static WorkflowSnapshot Reorder(WorkflowSnapshot workflow, LookupKind kind, IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(orderedIds);
        var positions = orderedIds.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
        int OrderOf(Guid id) => positions.TryGetValue(id, out var index) ? index : throw new ArgumentException("Every value must be listed.", nameof(orderedIds));
        return kind switch
        {
            LookupKind.TaskStatus => workflow with { TaskStatuses = [.. workflow.TaskStatuses.Select(s => s with { Order = OrderOf(s.Id.Value) }).OrderBy(s => s.Order)] },
            LookupKind.CrStatus => workflow with { CrStatuses = [.. workflow.CrStatuses.Select(s => s with { Order = OrderOf(s.Id.Value) }).OrderBy(s => s.Order)] },
            LookupKind.Priority => workflow with { Priorities = [.. workflow.Priorities.Select(p => p with { Order = OrderOf(p.Id.Value) }).OrderBy(p => p.Order)] },
            LookupKind.Squad => workflow with { Squads = [.. workflow.Squads.Select(s => s with { Order = OrderOf(s.Id.Value) }).OrderBy(s => s.Order)] },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "People and tags have no configured order."),
        };
    }

    /// <summary>Drops one value. The caller remaps every reference and default first (B-04).</summary>
    public static WorkflowSnapshot Remove(WorkflowSnapshot workflow, LookupKind kind, Guid id)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return kind switch
        {
            LookupKind.TaskStatus => workflow with { TaskStatuses = [.. workflow.TaskStatuses.Where(s => s.Id.Value != id)] },
            LookupKind.CrStatus => workflow with { CrStatuses = [.. workflow.CrStatuses.Where(s => s.Id.Value != id)] },
            LookupKind.Priority => workflow with { Priorities = [.. workflow.Priorities.Where(p => p.Id.Value != id)] },
            LookupKind.Squad => workflow with { Squads = [.. workflow.Squads.Where(s => s.Id.Value != id)] },
            LookupKind.Person => workflow with { People = [.. workflow.People.Where(p => p.Id.Value != id)] },
            LookupKind.Tag => workflow with { Tags = [.. workflow.Tags.Where(t => t.Id.Value != id)] },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>The workspace defaults that refer to a lookup value (B-04 removal needs a replacement for each).</summary>
    public static WorkspaceDefaultRoles DefaultRoles(WorkflowDefaults defaults, LookupKind kind, Guid id)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var roles = WorkspaceDefaultRoles.None;
        void Add(bool condition, WorkspaceDefaultRoles role) => roles |= condition ? role : WorkspaceDefaultRoles.None;
        switch (kind)
        {
            case LookupKind.TaskStatus:
                Add(defaults.DefaultTaskStatusId.Value == id, WorkspaceDefaultRoles.DefaultTaskStatus);
                break;
            case LookupKind.CrStatus:
                Add(defaults.DefaultCrStatusId.Value == id, WorkspaceDefaultRoles.DefaultCrStatus);
                Add(defaults.CalendarSchedulingStatusId.Value == id, WorkspaceDefaultRoles.CalendarSchedulingStatus);
                Add(defaults.CrBacklogStatusId.Value == id, WorkspaceDefaultRoles.CrBacklogStatus);
                break;
            case LookupKind.Priority:
                Add(defaults.DefaultPriorityId.Value == id, WorkspaceDefaultRoles.DefaultPriority);
                break;
            case LookupKind.Person:
                Add(defaults.DefaultCrOwnerId?.Value == id, WorkspaceDefaultRoles.DefaultCrOwner);
                break;
            default:
                break;
        }

        return roles;
    }

    /// <summary>Points every default that names the removed value at the replacement.</summary>
    public static WorkflowDefaults RemapDefaults(WorkflowDefaults defaults, LookupRemap remap)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(remap);
        return remap.Kind switch
        {
            LookupKind.TaskStatus when defaults.DefaultTaskStatusId.Value == remap.RemovedId =>
                defaults with { DefaultTaskStatusId = new TaskStatusId(remap.RequiredReplacement) },
            LookupKind.CrStatus => defaults with
            {
                DefaultCrStatusId = defaults.DefaultCrStatusId.Value == remap.RemovedId ? new CrStatusId(remap.RequiredReplacement) : defaults.DefaultCrStatusId,
                CalendarSchedulingStatusId = defaults.CalendarSchedulingStatusId.Value == remap.RemovedId ? new CrStatusId(remap.RequiredReplacement) : defaults.CalendarSchedulingStatusId,
                CrBacklogStatusId = defaults.CrBacklogStatusId.Value == remap.RemovedId ? new CrStatusId(remap.RequiredReplacement) : defaults.CrBacklogStatusId,
            },
            LookupKind.Priority when defaults.DefaultPriorityId.Value == remap.RemovedId =>
                defaults with { DefaultPriorityId = new PriorityId(remap.RequiredReplacement) },
            LookupKind.Person when defaults.DefaultCrOwnerId?.Value == remap.RemovedId =>
                defaults with { DefaultCrOwnerId = new PersonId(remap.RequiredReplacement) },
            _ => defaults,
        };
    }
}

/// <summary>Workspace defaults (T-05 <c>app_metadata</c>) that can refer to a lookup value.</summary>
[Flags]
public enum WorkspaceDefaultRoles
{
    None = 0,
    DefaultTaskStatus = 1,
    DefaultCrStatus = 2,
    DefaultPriority = 4,
    DefaultCrOwner = 8,
    CalendarSchedulingStatus = 16,
    CrBacklogStatus = 32,
}
