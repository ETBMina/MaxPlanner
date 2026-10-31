using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Policies;

/// <summary>What a copy takes from its source (B-06). Every option is off unless the user checks it.</summary>
public sealed record CopyOptions
{
    /// <summary>Start in the source's current status instead of the configured default for new tasks.</summary>
    public bool CopyStatus { get; init; }

    public bool CopyPeople { get; init; }

    public bool CopyDates { get; init; }

    public bool CopyTags { get; init; }

    public bool CopyNotes { get; init; }

    public bool CopyChecklistItems { get; init; }

    /// <summary>Only meaningful with <see cref="CopyChecklistItems"/>; by default copied items start unchecked.</summary>
    public bool PreserveChecklistCompletion { get; init; }

    public bool CopyCustomValues { get; init; }
}

/// <summary>The editable draft shown before a copy is saved; it is then created through <see cref="TaskItem.Create"/>.</summary>
/// <remarks>
/// Start/End carry the source dates only when dates are copied; otherwise they are unchanged, so the
/// ordinary B-04 creation rules apply to the chosen status (default or copied, B-06).
/// </remarks>
public sealed record TaskCopyDraft(
    TaskContent Content,
    TaskStatusId StatusId,
    PriorityId PriorityId,
    DateIntent Start,
    DateIntent End,
    IReadOnlyList<ChecklistItem> ChecklistItems);

/// <summary>B-06 copy task, ADR 0005: a genuinely new, unlinked task prefilled from its source.</summary>
public static class CopyTaskPolicy
{
    /// <param name="source">The task being copied; it may be archived and linked (AC-09).</param>
    /// <param name="sourceEffectiveTitle">The source's displayed title, i.e. its CR's title when linked.</param>
    /// <param name="defaultStatus">The configured default task status for new tasks.</param>
    /// <param name="availableStatuses">Task statuses that still exist; a removed source status falls back to the default.</param>
    /// <param name="availablePriorities">Priorities that still exist; a removed source priority falls back to the default.</param>
    /// <param name="newId">Creates IDs for the copied checklist items.</param>
    public static TaskCopyDraft Draft(
        TaskItem source,
        TitleText sourceEffectiveTitle,
        CopyOptions options,
        TaskStatusId defaultStatus,
        IReadOnlyCollection<TaskStatusId> availableStatuses,
        IReadOnlyCollection<PriorityId> availablePriorities,
        PriorityId defaultPriority,
        Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceEffectiveTitle);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(availableStatuses);
        ArgumentNullException.ThrowIfNull(availablePriorities);
        ArgumentNullException.ThrowIfNull(newId);

        // The copy starts unlinked: it has its own title (prefilled from what the user saw) and
        // inherits no CR title, number, squad, or owner until it is explicitly linked (T-13).
        var content = new TaskContent(
            sourceEffectiveTitle,
            Service: null,
            options.CopyPeople ? source.DesignerId : null,
            options.CopyPeople ? source.DeveloperId : null,
            options.CopyTags ? source.TagIds : [],
            options.CopyNotes ? source.Notes : null,
            options.CopyCustomValues ? source.CustomValues : CustomValue.NoValues);

        IReadOnlyList<ChecklistItem> items = options.CopyChecklistItems
            ? [.. source.ChecklistItems.Select(i => i with
            {
                Id = new ChecklistItemId(newId()),
                IsCompleted = options.PreserveChecklistCompletion && i.IsCompleted,
            })]
            : [];

        return new TaskCopyDraft(
            content,
            options.CopyStatus && availableStatuses.Contains(source.StatusId) ? source.StatusId : defaultStatus,
            availablePriorities.Contains(source.PriorityId) ? source.PriorityId : defaultPriority,
            options.CopyDates && source.Dates.Start is { } start ? DateIntent.Set(start) : DateIntent.Unchanged,
            options.CopyDates && source.Dates.End is { } end ? DateIntent.Set(end) : DateIntent.Unchanged,
            items);
    }
}
