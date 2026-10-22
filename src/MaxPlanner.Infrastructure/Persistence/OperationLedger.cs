using MaxPlanner.Application.Common;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence;

/// <summary>The <c>operation_ledger</c> table and the commit sequence in <c>app_metadata</c> (T-05, T-07).</summary>
internal static class OperationLedger
{
    public sealed record Entry(string Kind, long Sequence, string ResultJson);

    public static Entry? Find(SqliteConnection connection, OperationId operationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, commit_sequence, result_json FROM operation_ledger WHERE operation_id = $id;";
        command.Add("$id", operationId.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new Entry(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)) : null;
    }

    /// <summary>
    /// Takes the next commit sequence and records the operation, inside the operation's own transaction.
    /// </summary>
    /// <remarks>
    /// T-07: the sequence is incremented in the same transaction as the data, so a rollback also rolls the
    /// sequence back and committed sequences stay gap-free. It is read from the row, not from a cached
    /// counter, so it stays right even after a commit whose outcome was unknown.
    /// </remarks>
    public static long Record(SqliteConnection connection, SqliteTransaction transaction, OperationId operationId, string kind, DateTimeOffset committedAtUtc, string resultJson)
    {
        long sequence;
        using (var next = connection.CreateCommand())
        {
            next.Transaction = transaction;
            next.CommandText = "UPDATE app_metadata SET commit_sequence = commit_sequence + 1 WHERE id = 1 RETURNING commit_sequence;";
            sequence = next.ExecuteScalar() is long value ? value : throw new CorruptStoredDataException("app_metadata", "commit_sequence", "row missing");
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO operation_ledger (operation_id, kind, commit_sequence, committed_at_utc, result_json)
            VALUES ($id, $kind, $sequence, $committed_at_utc, $result_json);
            """;
        insert.Add("$id", operationId.Value);
        insert.Add("$kind", kind);
        insert.Add("$sequence", sequence);
        insert.Add("$committed_at_utc", committedAtUtc);
        insert.Add("$result_json", resultJson);
        insert.ExecuteNonQuery();
        return sequence;
    }
}
