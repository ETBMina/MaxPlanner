using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence;

/// <summary>
/// Opens SQLite connections for one dataset with the settings required by T-06 and ADR 0009.
/// </summary>
/// <remarks>
/// <para>The single writer connection is owned by the database worker for the dataset's lifetime;
/// readers are short-lived, read-only, and may run concurrently with the writer under WAL.</para>
/// <para>Reader connections are pooled by Microsoft.Data.Sqlite, so pooled handles keep the files
/// open after <c>Dispose</c>. Call <see cref="SqliteConnection.ClearAllPools"/> before any
/// operation that moves or replaces the database files (restore, relocation tests).</para>
/// </remarks>
public sealed class SqliteConnectionFactory
{
    private readonly string _writerConnectionString;
    private readonly string _readerConnectionString;

    public SqliteConnectionFactory(string databaseFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFile);
        DatabaseFile = Path.GetFullPath(databaseFile);

        _writerConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // The writer lives for the whole session; pooling would only hide its lifetime.
            Pooling = false,
        }.ToString();

        _readerConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    public string DatabaseFile { get; }

    /// <summary>Opens the dataset's writer connection, creating the file if it does not exist.</summary>
    public SqliteConnection OpenWriter()
    {
        var connection = new SqliteConnection(_writerConnectionString);
        connection.Open();
        // WAL is persistent in the file; FULL makes every reported commit durable (T-06.1).
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = FULL;");
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "PRAGMA busy_timeout = 5000;");
        return connection;
    }

    /// <summary>Opens a read-only connection; a transaction on it sees one consistent WAL snapshot.</summary>
    public SqliteConnection OpenReader()
    {
        var connection = new SqliteConnection(_readerConnectionString);
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 5000;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
