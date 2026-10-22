using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Tasks;

namespace MaxPlanner.Application.ReadModels;

/// <summary>
/// Builds read models from domain records, their current relationships, and the current workflow.
/// Pure: callers recompose affected models after every change set (ADR 0010), so inherited values are
/// always those of the current CR and lookup names are always current (T-07, T-13).
/// </summary>
/// <remarks>
/// A lookup ID missing from the workflow (which the database constraints rule out) is shown with an
/// empty name rather than failing the whole projection.
/// </remarks>
public sealed class ReadModelComposer
{
    /// <summary>The English label for "no squad" (ADR 0020); Presentation passes its localized string.</summary>
    public const string DefaultGeneralSquadLabel = "General";

    private readonly string _generalSquadLabel;

    public ReadModelComposer(string generalSquadLabel = DefaultGeneralSquadLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generalSquadLabel);
        _generalSquadLabel = generalSquadLabel;
    }

    /// <param name="linkedCr">The CR named by the task's link, or null for an unlinked task.</param>
    public TaskReadModel ComposeTask(TaskItem task, ChangeRequest? linkedCr, int subtaskCardCount, WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workflow);
        EnsureLinkMatches(task, linkedCr);

        var status = workflow.FindTaskStatus(task.StatusId);
        var priority = workflow.FindPriority(task.PriorityId);
        return new TaskReadModel(
            task.Id,
            BoardCardKind.Task,
            InheritancePolicy.EffectiveTitle(task.OwnTitle, linkedCr?.Title).Value,
            IsTitleInherited: linkedCr is not null,
            task.Service?.Value,
            new LookupRef<TaskStatusId>(task.StatusId, status?.Name.Display ?? string.Empty),
            status?.Order ?? 0,
            status?.IsCompleted ?? false,
            new LookupRef<PriorityId>(task.PriorityId, priority?.Name.Display ?? string.Empty),
            priority?.Order ?? 0,
            Inherit(linkedCr, workflow),
            People(task.DesignerIds, workflow),
            People(task.DeveloperIds, workflow),
            [.. task.TagIds.Select(id => new LookupRef<TagId>(id, workflow.FindTag(id)?.Name.Display ?? string.Empty))],
            task.Notes?.Value,
            task.Dates.Start,
            task.Dates.End,
            task.IsArchived,
            task.Rank,
            task.ChecklistItems.Count(i => i.IsCompleted),
            task.ChecklistItems.Count,
            [.. task.ChecklistItems
                .OrderBy(i => i.Rank)
                .ThenBy(i => i.Id.Value)
                .Select(i => new ChecklistItemReadModel(i.Id, i.Text.Value, i.IsCompleted, i.Rank))],
            subtaskCardCount,
            task.CustomValues,
            task.CreatedAtUtc,
            task.UpdatedAtUtc,
            task.Revision);
    }

    /// <param name="parentCr">The CR linked to <paramref name="parent"/>, or null when the parent is unlinked.</param>
    public SubtaskCardReadModel ComposeSubtaskCard(SubtaskCard card, TaskItem parent, ChangeRequest? parentCr, WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(workflow);
        if (card.ParentTaskId != parent.Id)
        {
            throw new ArgumentException("The parent does not own this Subtask card.", nameof(parent));
        }

        EnsureLinkMatches(parent, parentCr);
        var status = workflow.FindTaskStatus(card.StatusId);
        return new SubtaskCardReadModel(
            card.Id,
            BoardCardKind.SubtaskCard,
            card.ParentTaskId,
            card.Subtitle.Value,
            InheritancePolicy.EffectiveTitle(parent.OwnTitle, parentCr?.Title).Value,
            parent.Service?.Value,
            parent.IsArchived,
            new LookupRef<TaskStatusId>(card.StatusId, status?.Name.Display ?? string.Empty),
            status?.Order ?? 0,
            status?.IsCompleted ?? false,
            Inherit(parentCr, workflow),
            card.Notes?.Value,
            card.Dates.Start,
            card.Dates.End,
            card.Rank,
            card.CreatedAtUtc,
            card.UpdatedAtUtc,
            card.Revision);
    }

    /// <param name="today">The user's local day; overdue flags are only valid for this day (B-15).</param>
    public ChangeRequestReadModel ComposeChangeRequest(ChangeRequest changeRequest, int linkedTaskCount, DateOnly today, WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(changeRequest);
        ArgumentNullException.ThrowIfNull(workflow);

        var status = workflow.FindCrStatus(changeRequest.StatusId);
        var priority = workflow.FindPriority(changeRequest.PriorityId);
        var isCompleted = status?.IsCompleted ?? false;
        return new ChangeRequestReadModel(
            changeRequest.Id,
            changeRequest.Number.Display,
            changeRequest.Number.Normalized,
            changeRequest.Title.Value,
            changeRequest.TaskNumber?.Value,
            new LookupRef<PersonId>(changeRequest.OwnerId, workflow.FindPerson(changeRequest.OwnerId)?.Name.Display ?? string.Empty),
            changeRequest.SquadId,
            SquadName(changeRequest.SquadId, workflow),
            new LookupRef<CrStatusId>(changeRequest.StatusId, status?.Name.Display ?? string.Empty),
            status?.Order ?? 0,
            isCompleted,
            status?.IsBacklog ?? false,
            new LookupRef<PriorityId>(changeRequest.PriorityId, priority?.Name.Display ?? string.Empty),
            priority?.Order ?? 0,
            changeRequest.Planned?.Start,
            changeRequest.Planned?.End,
            changeRequest.EmailSubject?.Value,
            changeRequest.SolutionDesignUrl?.ToString(),
            changeRequest.Description?.Value,
            changeRequest.CustomValues,
            OverduePolicy.IsOverdue(changeRequest.Planned, isCompleted, today),
            OverduePolicy.DaysOverdue(changeRequest.Planned, isCompleted, today),
            linkedTaskCount,
            changeRequest.CreatedAtUtc,
            changeRequest.UpdatedAtUtc,
            changeRequest.Revision);
    }

    private static void EnsureLinkMatches(TaskItem task, ChangeRequest? linkedCr)
    {
        if (task.ChangeRequestId != linkedCr?.Id)
        {
            throw new ArgumentException("The CR passed is not the one the task is linked to.", nameof(linkedCr));
        }
    }

    private static List<LookupRef<PersonId>> People(IReadOnlyList<PersonId> ids, WorkflowSnapshot workflow) =>
        [.. ids.Select(id => new LookupRef<PersonId>(id, workflow.FindPerson(id)?.Name.Display ?? string.Empty))];

    private InheritedCrValues Inherit(ChangeRequest? cr, WorkflowSnapshot workflow) =>
        cr is null
            ? new InheritedCrValues(null, null, null, null, _generalSquadLabel, null, null)
            : new InheritedCrValues(
                cr.Id,
                cr.Number.Display,
                cr.TaskNumber?.Value,
                cr.SquadId,
                SquadName(cr.SquadId, workflow),
                cr.OwnerId,
                workflow.FindPerson(cr.OwnerId)?.Name.Display ?? string.Empty);

    private string SquadName(SquadId? squadId, WorkflowSnapshot workflow) =>
        squadId is { } id ? workflow.FindSquad(id)?.Name.Display ?? string.Empty : _generalSquadLabel;
}
