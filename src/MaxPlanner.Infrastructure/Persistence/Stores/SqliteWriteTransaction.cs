using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

/// <summary>
/// The stores of one <c>BEGIN IMMEDIATE</c> transaction on the writer connection.
/// </summary>
/// <remarks>
/// T-06.2: created, used, and closed on the writer worker thread inside one synchronous delegate. Every
/// command checks that it still runs on that thread and before <see cref="Close"/>, so a delegate that
/// leaks the transaction (or awaits and resumes elsewhere) fails loudly instead of writing outside it.
/// </remarks>
internal sealed class SqliteWriteTransaction : IWriteTransaction
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly Action<string>? _beforeWrite;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly RecordLoader _loader;
    private bool _isOpen = true;

    public SqliteWriteTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset utcNow,
        DateOnly localToday,
        Action<string>? beforeWrite)
    {
        _connection = connection;
        _transaction = transaction;
        _beforeWrite = beforeWrite;
        UtcNow = utcNow;
        LocalToday = localToday;
        _loader = new RecordLoader(connection, transaction);
        Tasks = new SqliteTaskStore(this);
        SubtaskCards = new SqliteSubtaskCardStore(this);
        ChangeRequests = new SqliteChangeRequestStore(this);
        Links = new SqliteLinkStore(this);
        Reviews = new SqliteReviewStore(this);
        GeneralTodos = new SqliteGeneralTodoStore(this);
        Workflow = new SqliteWorkflowReader(this);
        Lookups = new SqliteLookupStore(this);
        Tombstones = new SqliteTombstoneStore(this);
    }

    public DateTimeOffset UtcNow { get; }

    public DateOnly LocalToday { get; }

    public ChangeSetBuilder Changes { get; } = new();

    public ITaskStore Tasks { get; }

    public ISubtaskCardStore SubtaskCards { get; }

    public IChangeRequestStore ChangeRequests { get; }

    public ILinkStore Links { get; }

    public IReviewStore Reviews { get; }

    public IGeneralTodoStore GeneralTodos { get; }

    public IWorkflowReader Workflow { get; }

    public ILookupStore Lookups { get; }

    public ITombstoneStore Tombstones { get; }

    internal RecordLoader Loader
    {
        get
        {
            EnsureOpen();
            return _loader;
        }
    }

    /// <summary>Ends store access; the coordinator then commits or rolls back the SQLite transaction.</summary>
    public void Close() => _isOpen = false;

    internal SqliteCommand Command(string sql)
    {
        EnsureOpen();
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>Fault-injection point named like the in-memory fake's (for example <c>tasks.insert</c>).</summary>
    internal void BeforeWrite(string write)
    {
        EnsureOpen();
        _beforeWrite?.Invoke(write);
    }

    internal int Execute(SqliteCommand command)
    {
        using (command)
        {
            EnsureOpen();
            return command.ExecuteNonQuery();
        }
    }

    private void EnsureOpen()
    {
        if (!_isOpen || Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("The write transaction is only valid inside its synchronous delegate on the writer thread (T-06.2).");
        }
    }
}
