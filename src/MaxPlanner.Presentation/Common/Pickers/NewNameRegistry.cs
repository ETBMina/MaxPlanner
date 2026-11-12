namespace MaxPlanner.Presentation.Common.Pickers;

/// <summary>
/// New names typed into the pickers of one draft, so the same new name gets one ID even when it is used
/// as both Designer and Developer. The IDs are sent with the save; nothing is created before it (B-02.6).
/// </summary>
public sealed class NewNameRegistry
{
    private readonly Dictionary<string, (Guid Id, string Name)> _byName = new(StringComparer.Ordinal);

    public Guid IdFor(string name)
    {
        var key = Normalize(name);
        if (!_byName.TryGetValue(key, out var entry))
        {
            entry = (Guid.NewGuid(), name.Trim());
            _byName[key] = entry;
        }

        return entry.Id;
    }

    /// <summary>The new names among <paramref name="ids"/>, in first-use order.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Used(IEnumerable<Guid> ids)
    {
        var used = ids.ToHashSet();
        return [.. _byName.Values.Where(e => used.Contains(e.Id))];
    }

    internal static string Normalize(string name) => name.Trim().ToUpperInvariant();
}
