using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

// Phase 0 spike 2 (ADR 0009): a reduced slice of the planned schema, enough to prove the engine
// behaviors the design relies on. The production schema arrives with migration M0001 in Phase 1.
internal static class SpikeSchema
{
    public const string Sql = """
        CREATE TABLE app_metadata (
            id               INTEGER PRIMARY KEY CHECK (id = 1),
            commit_sequence  INTEGER NOT NULL
        );
        INSERT INTO app_metadata (id, commit_sequence) VALUES (1, 0);

        CREATE TABLE change_requests (
            id           BLOB PRIMARY KEY,
            number       TEXT NOT NULL,
            number_norm  TEXT NOT NULL UNIQUE,
            title        TEXT NOT NULL,
            planned_end  TEXT NULL CHECK (planned_end IS NULL OR planned_end GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]')
        );

        CREATE TABLE tasks (
            id         BLOB PRIMARY KEY,
            own_title  TEXT NULL,
            revision   INTEGER NOT NULL
        );

        CREATE TABLE cr_task_links (
            task_id  BLOB PRIMARY KEY REFERENCES tasks(id),
            cr_id    BLOB NOT NULL REFERENCES change_requests(id)
        );
        CREATE INDEX ix_cr_task_links_cr ON cr_task_links(cr_id);
        """;

    public static void Create(SqliteConnection writer)
    {
        using var command = writer.CreateCommand();
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Inserts one task and advances the commit sequence in a single write transaction.</summary>
    public static void CommitTask(SqliteConnection writer, Guid taskId)
    {
        using var transaction = writer.BeginTransaction(deferred: false);
        using (var insert = writer.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO tasks (id, own_title, revision) VALUES ($id, 'Task', 1);";
            insert.Parameters.AddWithValue("$id", taskId);
            insert.ExecuteNonQuery();
        }

        using (var sequence = writer.CreateCommand())
        {
            sequence.Transaction = transaction;
            sequence.CommandText = "UPDATE app_metadata SET commit_sequence = commit_sequence + 1 WHERE id = 1;";
            sequence.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
