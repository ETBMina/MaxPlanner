namespace MaxPlanner.Application.Tests.Fakes;

/// <summary>
/// The update rule documented on <c>IWriteTransaction</c> and enforced by the SQLite stores (slice 1d
/// <c>RevisionGuard</c>): an <c>Update</c> must receive a record this transaction loaded or wrote, with a
/// higher revision than that state. The fake enforces it too, so a use case that breaks it fails here
/// instead of only against SQLite.
/// </summary>
internal sealed class FakeRevisionGuard(string entity)
{
    private readonly Dictionary<Guid, long> _seen = [];

    public T Seen<T>(T? record, Func<T, Guid> id, Func<T, long> revision)
        where T : class
    {
        if (record is not null)
        {
            _seen[id(record)] = revision(record);
        }

        return record!;
    }

    public void Forget(Guid id) => _seen.Remove(id);

    /// <exception cref="InvalidOperationException">The record was not loaded here, or the revision does not advance.</exception>
    public void CheckUpdate(Guid id, long newRevision)
    {
        if (!_seen.TryGetValue(id, out var previous))
        {
            throw new InvalidOperationException($"Update of {entity} {id} without loading it in this transaction.");
        }

        if (newRevision <= previous)
        {
            throw new InvalidOperationException($"Update of {entity} {id} does not advance its revision ({previous} to {newRevision}).");
        }

        _seen[id] = newRevision;
    }
}
