namespace MaxPlanner.Infrastructure.Persistence.Stores;

/// <summary>
/// The revision of each record as this transaction last loaded or wrote it, used as the
/// <c>WHERE revision = …</c> guard of an UPDATE (T-05).
/// </summary>
/// <remarks>
/// The single writer means no other connection can change a row inside the transaction, so a failed
/// guard is never a user conflict (use cases report those as <c>ConcurrencyConflictError</c> after
/// loading). It means a use case tried to write an entity it did not load here, or one older than what
/// it loaded, which is a defect; the store throws so the transaction rolls back.
/// </remarks>
internal sealed class RevisionGuard(string entity)
{
    private readonly Dictionary<Guid, long> _seen = [];

    public void Seen(Guid id, long revision) => _seen[id] = revision;

    public void Forget(Guid id) => _seen.Remove(id);

    /// <returns>The revision the stored row must still have.</returns>
    public long ExpectedPrevious(Guid id, long newRevision)
    {
        if (!_seen.TryGetValue(id, out var previous))
        {
            throw new InvalidOperationException($"Update of {entity} {id} without loading it in this transaction.");
        }

        return newRevision > previous
            ? previous
            : throw new InvalidOperationException($"Update of {entity} {id} does not advance its revision ({previous} to {newRevision}).");
    }

    public static void EnsureOneRow(int affected, string entity, Guid id, string operation)
    {
        if (affected != 1)
        {
            throw new InvalidOperationException($"{operation} of {entity} {id} affected {affected} rows; the stored row is missing or changed.");
        }
    }
}
