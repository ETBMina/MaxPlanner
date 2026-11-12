using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Presentation.Common.Options;

/// <summary>A priority offered by the editors' Priority box.</summary>
public sealed record PriorityOption(PriorityId Id, string Name, PriorityLevel Level = PriorityLevel.Mid) : IPriorityChoice
{
    string IPriorityChoice.DisplayName => Name;

    PriorityLevel? IPriorityChoice.Level => Level;

    /// <summary>The configured priorities in rank order, each with the glyph level of its rank (design §6.1).</summary>
    public static IReadOnlyList<PriorityOption> From(WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var levels = PriorityLevels.Of(workflow.Priorities);
        return [.. workflow.Priorities.OrderBy(p => p.Order).Select(p => new PriorityOption(p.Id, p.Name.Display, levels.GetValueOrDefault(p.Id, PriorityLevel.Mid)))];
    }

    public override string ToString() => Name;
}
