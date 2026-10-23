using MaxPlanner.Domain.Policies;

namespace MaxPlanner.Application.Common;

/// <summary>
/// Moves one record of an explicitly ordered list (checklist items, General to-dos, reviews; B-09, B-17.2)
/// next to an anchor record. Order is (Rank, Id) over the complete list.
/// </summary>
internal static class ExplicitOrder
{
    /// <summary>The list as stored, ordered by rank and then by the .NET <see cref="Guid"/> order (never SQLite's byte order).</summary>
    public static List<(Guid Id, long Rank)> Sort(IEnumerable<(Guid Id, long Rank)> items) =>
        [.. items.OrderBy(i => i.Rank).ThenBy(i => i.Id)];

    /// <summary>
    /// The rank changes that put <paramref name="moving"/> immediately before or after <paramref name="anchor"/>.
    /// Usually only the moved record changes; when its neighbours leave no room, the complete list is
    /// renumbered (B-09: completed items keep their relative order because every record keeps its place).
    /// </summary>
    /// <returns>The changes, or null when either record is not in the list.</returns>
    public static IReadOnlyList<(Guid Id, long Rank)>? Place(IReadOnlyList<(Guid Id, long Rank)> ordered, Guid moving, Guid anchor, bool placeAfter)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var current = ordered.FirstOrDefault(i => i.Id == moving);
        var others = ordered.Where(i => i.Id != moving).ToList();
        var anchorIndex = others.FindIndex(i => i.Id == anchor);
        if (current.Id != moving || anchorIndex < 0)
        {
            return null;
        }

        var insertAt = placeAfter ? anchorIndex + 1 : anchorIndex;
        long? before = insertAt > 0 ? others[insertAt - 1].Rank : null;
        long? after = insertAt < others.Count ? others[insertAt].Rank : null;
        if (RankPolicy.Between(before, after) is { } rank)
        {
            return [(moving, rank)];
        }

        others.Insert(insertAt, current);
        return RankPolicy.Renumber(others);
    }
}
