using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Presentation.Common;

/// <summary>Chip tokens assigned by configured priority rank, never by name (design §6.1).</summary>
public enum PriorityLevel
{
    Level1,
    Level2,
    Mid,
    Lowest,
}

/// <summary>Assigns <see cref="PriorityLevel"/>s by rank; Board, Calendar and the filter menu share it.</summary>
public static class PriorityLevels
{
    /// <summary>Design §6.1: highest rank Level1, second Level2, lowest (with three or more) Lowest, the rest Mid.</summary>
    public static Dictionary<PriorityId, PriorityLevel> Of(IReadOnlyList<PriorityDefinition> priorities)
    {
        ArgumentNullException.ThrowIfNull(priorities);
        var ordered = priorities.OrderBy(p => p.Order).ToList();
        var levels = new Dictionary<PriorityId, PriorityLevel>();
        for (var i = 0; i < ordered.Count; i++)
        {
            levels[ordered[i].Id] = i == 0 ? PriorityLevel.Level1
                : ordered.Count >= 3 && i == ordered.Count - 1 ? PriorityLevel.Lowest
                : i == 1 ? PriorityLevel.Level2
                : PriorityLevel.Mid;
        }

        return levels;
    }
}
