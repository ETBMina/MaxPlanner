namespace MaxPlanner.Domain.Policies;

/// <summary>
/// Persisted ordering for Board columns and checklists (B-07). Order is (Rank, Id); the ID is the
/// final deterministic tie-breaker.
/// </summary>
public static class RankPolicy
{
    /// <summary>Gap between neighbours after renumbering, leaving room for many inserts.</summary>
    public const long Step = 1_024;

    /// <summary>Rank for an item appended after <paramref name="lastRank"/> (null for an empty list).</summary>
    public static long After(long? lastRank) => (lastRank ?? 0) + Step;

    /// <summary>
    /// A rank strictly between two neighbours, or null when there is no room and the list must be
    /// renumbered first.
    /// </summary>
    public static long? Between(long? before, long? after)
    {
        var low = before ?? (after is { } a ? a - (2 * Step) : 0);
        var high = after ?? low + (2 * Step);
        return high - low > 1 ? low + ((high - low) / 2) : null;
    }

    /// <summary>
    /// Assigns evenly spaced ranks to the full ordered list and returns only the items whose rank
    /// changes, so a renumber touches no unaffected record.
    /// </summary>
    /// <remarks>
    /// B-07: pass the complete column, hidden (filtered-out) cards included. Renumbering only the
    /// visible cards would corrupt the hidden order.
    /// </remarks>
    public static IReadOnlyList<(TKey Key, long Rank)> Renumber<TKey>(IReadOnlyList<(TKey Key, long Rank)> orderedItems)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        var changes = new List<(TKey, long)>();
        for (var i = 0; i < orderedItems.Count; i++)
        {
            var rank = (i + 1) * Step;
            if (orderedItems[i].Rank != rank)
            {
                changes.Add((orderedItems[i].Key, rank));
            }
        }

        return changes;
    }
}
