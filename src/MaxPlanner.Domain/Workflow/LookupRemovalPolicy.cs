using MaxPlanner.Domain.Common;

namespace MaxPlanner.Domain.Workflow;

public enum LookupKind
{
    TaskStatus,
    CrStatus,
    Priority,
    Squad,
    Person,
    Tag,
}

/// <summary>How a lookup value is referenced at the moment its removal is requested.</summary>
/// <param name="RecordCount">Records that reference it in any role, archived tasks included.</param>
/// <param name="CrOwnerCount">CRs it owns; only meaningful for people.</param>
/// <param name="IsWorkspaceDefault">
/// Referenced by a workspace default: default task/CR status or priority, calendar scheduling or
/// backlog status, or the default CR Owner.
/// </param>
public sealed record LookupUsage(int RecordCount, int CrOwnerCount, bool IsWorkspaceDefault)
{
    public static LookupUsage Unused { get; } = new(0, 0, false);
}

/// <summary>
/// B-04/B-11 rules for removing a configurable lookup value. The remap itself (replacing or clearing
/// every reference, preserving dates) runs as one transaction in the use case after the counts are shown.
/// </summary>
public static class LookupRemovalPolicy
{
    /// <param name="existingIds">Every current value of this lookup kind, including the one being removed.</param>
    /// <param name="replacementId">The value that takes over references, or null to clear them where allowed.</param>
    /// <returns>The violated rule, or null when the removal may proceed.</returns>
    public static DomainError? Validate<TId>(LookupKind kind, IReadOnlyCollection<TId> existingIds, TId removedId, TId? replacementId, LookupUsage usage)
        where TId : struct, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(existingIds);
        ArgumentNullException.ThrowIfNull(usage);

        if (!existingIds.Contains(removedId))
        {
            return new DomainError(DomainErrorCodes.ItemNotFound, "Removed");
        }

        // B-04: at least one task status, one CR status, and one priority must always exist.
        if ((kind is LookupKind.TaskStatus or LookupKind.CrStatus or LookupKind.Priority) && existingIds.Count < 2)
        {
            return new DomainError(DomainErrorCodes.LastLookupRequired, "Removed");
        }

        if (replacementId is { } replacement)
        {
            if (replacement.Equals(removedId))
            {
                return new DomainError(DomainErrorCodes.ReplacementSameAsRemoved, "Replacement");
            }

            return existingIds.Contains(replacement) ? null : new DomainError(DomainErrorCodes.ItemNotFound, "Replacement");
        }

        return RequiresReplacement(kind, usage) ? new DomainError(DomainErrorCodes.ReplacementRequired, "Replacement") : null;
    }

    // Statuses and priorities are required on every record and default, so they can only be replaced.
    // A person can be cleared from Designer/Developer/review Developer roles, but a CR always needs an
    // Owner (ADR 0020). A cleared squad becomes "General"; a cleared tag is simply removed.
    private static bool RequiresReplacement(LookupKind kind, LookupUsage usage) => kind switch
    {
        LookupKind.TaskStatus or LookupKind.CrStatus or LookupKind.Priority => usage.RecordCount > 0 || usage.IsWorkspaceDefault,
        LookupKind.Person => usage.CrOwnerCount > 0 || usage.IsWorkspaceDefault,
        _ => false,
    };
}
