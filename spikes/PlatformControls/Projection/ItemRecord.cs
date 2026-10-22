namespace PlatformControls.Projection;

/// <summary>Immutable committed record, as the product store would publish it.</summary>
public sealed record ItemRecord(Guid Id, int Number, string Title, string Status, int Rank)
{
    /// <summary>Predictable key so UI Automation selectors can be computed: number in hex, zero padded.</summary>
    public static Guid KeyFor(int number) => new(number, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>Sort by Rank, then Number for a stable order among equal ranks.</summary>
public sealed class RankComparer : IComparer<ItemViewModel>
{
    public static RankComparer Instance { get; } = new();

    public int Compare(ItemViewModel? x, ItemViewModel? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var byRank = x.Rank.CompareTo(y.Rank);
        return byRank != 0 ? byRank : x.Number.CompareTo(y.Number);
    }
}
