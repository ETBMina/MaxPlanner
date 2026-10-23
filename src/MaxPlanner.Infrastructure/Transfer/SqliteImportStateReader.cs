using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Persistence.Mapping;

namespace MaxPlanner.Infrastructure.Transfer;

/// <summary>Reads the import ledger and tombstones on pooled read-only connections (T-06.1), concurrently with the writer.</summary>
internal sealed class SqliteImportStateReader(SqliteConnectionFactory connections) : IImportStateReader
{
    public Task<AppResult<IReadOnlyDictionary<(string EntityKind, string SourceKey), Guid>>> LoadLedgerAsync(string sourceNamespace, CancellationToken cancellationToken) =>
        Task.Run(() => Guard<IReadOnlyDictionary<(string, string), Guid>>(() =>
        {
            using var connection = connections.OpenReader();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT entity_kind, source_key, record_id FROM import_ledger WHERE namespace = $ns;";
            command.Add("$ns", sourceNamespace);
            using var reader = command.ExecuteReader();
            var ledger = new Dictionary<(string, string), Guid>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ledger[(reader.GetString(0), reader.GetString(1))] = reader.GetGuid(2, "import_ledger", "record_id");
            }

            return ledger;
        }, cancellationToken), CancellationToken.None);

    public Task<AppResult<IReadOnlySet<Guid>>> FindDeletedAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return Task.Run(() => Guard<IReadOnlySet<Guid>>(() =>
        {
            var wanted = ids.ToHashSet();
            var deleted = new HashSet<Guid>();
            if (wanted.Count == 0)
            {
                return deleted;
            }

            // Tombstones are few compared with an import, so reading them all avoids one query per record.
            using var connection = connections.OpenReader();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM tombstones;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = reader.GetGuid(0, "tombstones", "id");
                if (wanted.Contains(id))
                {
                    deleted.Add(id);
                }
            }

            return deleted;
        }, cancellationToken), CancellationToken.None);
    }

    private static AppResult<T> Guard<T>(Func<T> read, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AppResult.Success(read());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AppResult.Failure<T>(OperationCancelledError.Instance);
        }
        catch (Exception ex) when (SqliteFailures.Classify(ex) is { } kind)
        {
            return AppResult.Failure<T>(new StorageFailureError(kind));
        }
    }
}
