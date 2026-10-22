using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Domain.Policies;

/// <summary>
/// B-05/ADR 0007: deleting a CR deletes every linked task (archived included) with their checklist
/// items and Subtask cards. The user confirms an exact set of tasks; deletion proceeds only if that
/// set is still the complete set at commit time.
/// </summary>
public static class CrDeletionPolicy
{
    public static DomainError? EnsureConfirmedScope(IReadOnlyCollection<TaskId> currentlyLinked, IReadOnlyCollection<TaskId> confirmed)
    {
        ArgumentNullException.ThrowIfNull(currentlyLinked);
        ArgumentNullException.ThrowIfNull(confirmed);

        // A task linked (or deleted) after the confirmation was shown changes what would be lost;
        // the user must see the new scope instead of deleting more or less than they agreed to.
        return currentlyLinked.Count == confirmed.Count && new HashSet<TaskId>(currentlyLinked).SetEquals(confirmed)
            ? null
            : new DomainError(DomainErrorCodes.CascadeScopeChanged);
    }
}
