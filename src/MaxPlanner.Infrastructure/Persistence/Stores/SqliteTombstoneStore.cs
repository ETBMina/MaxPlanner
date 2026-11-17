using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Infrastructure.Persistence.Mapping;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

internal sealed class SqliteTombstoneStore(SqliteWriteTransaction tx) : ITombstoneStore
{
    public void Add(EntityKind kind, Guid id, DateTimeOffset deletedAtUtc)
    {
        tx.BeforeWrite("tombstones.insert");

        // A deleted identity stays deleted; recording it twice keeps the first deletion time.
        var command = tx.Command("INSERT OR IGNORE INTO tombstones (entity_kind, id, deleted_at_utc) VALUES ($kind, $id, $deleted_at_utc);");
        command.Add("$kind", StorageNames.Of(kind));
        command.Add("$id", id);
        command.Add("$deleted_at_utc", deletedAtUtc);
        tx.Execute(command);
    }

    public bool Contains(EntityKind kind, Guid id)
    {
        using var command = tx.Command("SELECT EXISTS (SELECT 1 FROM tombstones WHERE entity_kind = $kind AND id = $id);");
        command.Add("$kind", StorageNames.Of(kind));
        command.Add("$id", id);
        return command.ExecuteScalar() is long exists && exists != 0;
    }
}
