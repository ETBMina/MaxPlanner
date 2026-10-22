using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence;

/// <summary>
/// Opens SQLite connections for one dataset with the settings required by T-06 and ADR 0009.
/// </summary>
/// <remarks>
/// <para>The single writer connection is owned by the database worker for the dataset's lifetime;
/// readers are short-lived, read-only, and may run concurrently with the writer under WAL.</para>
/// <para>Reader connections are pooled by Microsoft.Data.Sqlite, so pooled handles keep the files
/// open after <c>Dispose</c>. Call <see cref="ClearReaderPool"/> (or <see cref="SqliteConnection.ClearAllPools"/>)
/// before any operation that moves or replaces the database files (restore, relocation tests).</para>
/// </remarks>
public sealed class SqliteConnectionFactory
{
    private readonly string _writerOpenExisting;
    private readonly string _writerCreate;
    private readonly string _readerConnectionString;

    public SqliteConnectionFactory(string databaseFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFile);
        DatabaseFile = Path.GetFullPath(databaseFile);

        // The writer lives for the whole session; pooling would only hide its lifetime.
        _writerOpenExisting = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        _writerCreate = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        _readerConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    public string DatabaseFile { get; }

    /// <summary>Opens the dataset's writer connection with the T-06.1 settings applied.</summary>
    /// <param name="createIfMissing">
    /// False opens an existing file only (a missing file fails with SQLITE_CANTOPEN). True may create an
    /// empty file; only the explicit dataset-creation path passes true, so opening never silently
    /// creates an empty database (T-09).
    /// </param>
    public SqliteConnection OpenWriter(bool createIfMissing)
    {
        var connection = OpenWriterConnection(createIfMissing);
        try
        {
            ConfigureWriter(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Opens a read-only connection; a transaction on it sees one consistent WAL snapshot.</summary>
    public SqliteConnection OpenReader()
    {
        var connection = new SqliteConnection(_readerConnectionString);
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 5000;");
        return connection;
    }

    /// <summary>Closes this dataset's idle pooled reader handles so the files can be moved or deleted.</summary>
    public void ClearReaderPool()
    {
        using var connection = new SqliteConnection(_readerConnectionString);
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>
    /// Opens the writer without changing anything in the file, so the caller can inspect the header
    /// (schema version, integrity) before <see cref="ConfigureWriter"/> switches the file to WAL.
    /// </summary>
    internal SqliteConnection OpenWriterConnection(bool createIfMissing)
    {
        var connection = new SqliteConnection(createIfMissing ? _writerCreate : _writerOpenExisting);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static void ConfigureWriter(SqliteConnection connection)
    {
        // WAL is persistent in the file; FULL makes every reported commit durable (T-06.1).
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = FULL;");
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "PRAGMA busy_timeout = 5000;");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
