using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaxPlanner.Infrastructure.Persistence;

/// <summary>Loads complete snapshots on pooled read-only connections, concurrently with the writer (T-06.1).</summary>
public sealed class SqliteReadSnapshotSource : IReadSnapshotSource
{
    private readonly SqliteConnectionFactory _connections;
    private readonly ILogger _logger;

    public SqliteReadSnapshotSource(SqliteConnectionFactory connections, ILogger<SqliteReadSnapshotSource>? logger = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _logger = logger ?? NullLogger<SqliteReadSnapshotSource>.Instance;
    }

    /// <inheritdoc/>
    public Task<AppResult<ReadSnapshot>> LoadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Load(cancellationToken), CancellationToken.None);

    private AppResult<ReadSnapshot> Load(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = _connections.OpenReader();

            // ADR 0010 handoff: the commit sequence and every table are read in this one WAL read transaction,
            // so the snapshot reflects exactly the commits up to CommitSequence, no more and no less. The
            // sequence is read first because a deferred transaction takes its snapshot at the first read.
            using var transaction = connection.BeginTransaction(deferred: true);
            var loader = new RecordLoader(connection, transaction);
            var sequence = loader.ReadCommitSequence();
            var workflow = loader.LoadWorkflow();
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = loader.LoadTasks();
            cancellationToken.ThrowIfCancellationRequested();
            var cards = loader.LoadSubtaskCards();
            var changeRequests = loader.LoadChangeRequests();
            var links = loader.LoadLinks();
            cancellationToken.ThrowIfCancellationRequested();
            var reviews = loader.LoadReviewItems();
            var todos = loader.LoadGeneralTodos();
            return AppResult.Success(new ReadSnapshot(sequence, tasks, cards, changeRequests, links, reviews, todos, workflow));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AppResult.Failure<ReadSnapshot>(OperationCancelledError.Instance);
        }
        catch (Exception ex) when (SqliteFailures.Classify(ex) is { } kind)
        {
            PersistenceLog.SnapshotFailed(_logger, kind, ex);
            return AppResult.Failure<ReadSnapshot>(new StorageFailureError(kind));
        }
    }
}
