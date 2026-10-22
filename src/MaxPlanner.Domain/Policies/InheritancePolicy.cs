using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Policies;

/// <summary>
/// Rules for values a task inherits from its CR (B-02.2–4, T-13, ADR 0005).
/// </summary>
public static class InheritancePolicy
{
    /// <summary>
    /// A linked task displays its CR's title; an unlinked task displays its own. The CR title is
    /// passed in from the current CR, never copied onto the task.
    /// </summary>
    public static TitleText EffectiveTitle(TitleText? ownTitle, TitleText? linkedCrTitle) =>
        linkedCrTitle ?? ownTitle ?? throw new InvalidOperationException("An unlinked task must have its own title.");

    /// <summary>
    /// Unlinked tasks need their own title. Linked tasks have none (the CR supplies it) and need a
    /// Service, because sibling tasks under one CR share the title (D10).
    /// </summary>
    public static DomainError? ValidateOwnFields(bool isLinked, TitleText? ownTitle, ServiceText? service) =>
        ValidateOwnFields(isLinked, hasOwnTitle: ownTitle is not null, hasService: service is not null);

    /// <summary>
    /// The same rule on presence alone, so an editor can report it together with text errors (for example
    /// an oversized service) before the values themselves are valid.
    /// </summary>
    public static DomainError? ValidateOwnFields(bool isLinked, bool hasOwnTitle, bool hasService)
    {
        if (isLinked)
        {
            if (hasOwnTitle)
            {
                return new DomainError(DomainErrorCodes.TitleNotAllowedWhenLinked, "Title");
            }

            return hasService ? null : new DomainError(DomainErrorCodes.ServiceRequiredWhenLinked, "Service");
        }

        return hasOwnTitle ? null : new DomainError(DomainErrorCodes.Required, "Title");
    }
}
