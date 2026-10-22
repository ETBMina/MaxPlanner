using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Tasks;

/// <summary>An ordered checkbox item owned by one task; edited through the parent's revision (T-05).</summary>
public sealed record ChecklistItem(ChecklistItemId Id, ChecklistText Text, bool IsCompleted, long Rank);
