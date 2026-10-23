using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Workflow;

/// <summary>Rules for changing the configurable workflow in Settings (B-04, B-11).</summary>
public static class WorkflowConfigurationPolicy
{
    /// <summary>B-04: names are unique within their lookup type, compared after trimming and case normalization.</summary>
    /// <param name="otherNames">The names of every other value of the same lookup type.</param>
    public static DomainError? ValidateUniqueName(IEnumerable<LookupName> otherNames, LookupName name, string field = "Name")
    {
        ArgumentNullException.ThrowIfNull(otherNames);
        ArgumentNullException.ThrowIfNull(name);
        return otherNames.Any(n => n.Normalized == name.Normalized) ? new DomainError(DomainErrorCodes.DuplicateName, field) : null;
    }

    /// <summary>
    /// B-08.3/B-08.11: "Move to backlog" sets the configured CR backlog status, and the Calendar backlog lists
    /// CRs whose status is flagged Backlog. The configured status must therefore keep the flag, which also
    /// guarantees that at least one CR status is flagged Backlog.
    /// </summary>
    public static DomainError? ValidateCrBacklogStatus(CrStatusDefinition? backlogStatus) =>
        backlogStatus is { IsBacklog: true } ? null : new DomainError(DomainErrorCodes.BacklogStatusMustBeFlagged, "CrBacklogStatus");
}
