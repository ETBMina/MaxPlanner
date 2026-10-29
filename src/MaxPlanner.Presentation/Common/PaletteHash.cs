namespace MaxPlanner.Presentation.Common;

/// <summary>Picks a fixed categorical palette entry for a record Id (calendar bars, tag pills).</summary>
internal static class PaletteHash
{
    /// <summary>
    /// The palette entry of <paramref name="id"/> in a palette of <paramref name="count"/> entries. A fixed FNV-1a hash
    /// over the Guid bytes keeps the color stable across runs; <see cref="string.GetHashCode()"/> is randomized per
    /// process and would recolor every record on each start.
    /// </summary>
    public static int IndexOf(Guid id, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var hash = 2166136261u;
        foreach (var value in bytes)
        {
            hash = (hash ^ value) * 16777619u;
        }

        return (int)(hash % (uint)count);
    }
}
