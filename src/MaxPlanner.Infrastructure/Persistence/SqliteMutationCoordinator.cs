using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Common;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using MaxPlanner.Infrastructure.Persistence.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Infrastructure.Persistence;

/// <summary>
/// The single ordered SQLite writer of one dataset (T-06, ADR 0009): every mutation runs here as one
/// <c>BEGIN IMMEDIATE</c> transaction with the operation ledger and commit sequence, and committed change
/// sets are published to <see cref="IChangeFeed"/> subscribers.
/// </summary>
/// <remarks>
/// <para>Thread ownership: the writer connection is used only by the dedicated worker thread started in the
/// constructor, from opening to the final WAL checkpoint. Work reaches it through a bounded channel whose
/// order is enqueue order. Transaction bodies are synchronous delegates (T-06.2), so a transaction never
/// spans an <c>await</c> and never leaves this thread.</para>
/// <para>Results complete asynchronously (never inline on the worker) after the change set is published,
/// so a caller resuming from <see cref="ExecuteAsync{T}"/> cannot stall the writer.</para>
/// </remarks>
public sealed class SqliteMutationCoordinator : IMutationCoordinator, IChangeFeed, IOperationStatusQuery, IAsyncDisposable
{
    private readonly SqliteConnection _writer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<WorkItem> _queue;
    private readonly ChangeFeedPublisher _feed;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _isDisposed;

    internal SqliteMutationCoordinator(SqliteConnection writer, long commitSequence, TimeProvider time, ILogger logger, int queueCapacity)
    {
        _writer = writer;
        _time = time;
        _logger = logger;
        _feed = new ChangeFeedPublisher(commitSequence, logger);
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        var worker = new Thread(RunWorker) { IsBackground = true, Name = "Max Planner database writer" };
        worker.Start();
    }

    public long LastPublishedSequence => _feed.LastPublishedSequence;

    /// <summary>
    /// Test-only fault injection, called with the fake's write names (<c>tasks.insert</c>, …) before each
    /// store write and with <c>commit.before</c> / <c>commit.after</c> around COMMIT. Throwing simulates a
    /// failure at that point. Set it before enqueuing the operation it should affect.
    /// </summary>
    internal Action<string>? FaultInjection { get; set; }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> cannot be recorded in the operation ledger.</exception>
    /// <exception cref="ObjectDisposedException">The dataset is closing or closed.</exception>
    public async Task<AppResult<CommandOutcome<T>>> ExecuteAsync<T>(
        OperationId operationId,
        string operationKind,
        Func<IWriteTransaction, TxResult<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKind);
        ArgumentNullException.ThrowIfNull(work);
        var resultType = StorageJson.LedgerTypeInfo<T>();
        if (cancellationToken.IsCancellationRequested)
        {
            return AppResult.Failure<CommandOutcome<T>>(OperationCancelledError.Instance);
        }

        var item = new MutationItem<T>(operationId, operationKind, work, resultType, cancellationToken);
        if (!await EnqueueAsync(item, cancellationToken).ConfigureAwait(false))
        {
            return AppResult.Failure<CommandOutcome<T>>(OperationCancelledError.Instance);
        }

        return await item.Completion.Task.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<AppResult<OperationStatus>> GetOperationStatusAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await RunOnWriterAsync(connection => OperationLedger.Find(connection, operationId), cancellationToken).ConfigureAwait(false);
            return AppResult.Success(entry is null
                ? new OperationStatus(operationId, IsCommitted: false, null, null)
                : new OperationStatus(operationId, IsCommitted: true, entry.Kind, entry.Sequence));
        }
        catch (OperationCanceledException)
        {
            return AppResult.Failure<OperationStatus>(OperationCancelledError.Instance);
        }
        catch (Exception ex) when (SqliteFailures.Classify(ex) is { } kind)
        {
            return AppResult.Failure<OperationStatus>(new StorageFailureError(kind));
        }
    }

    public IDisposable Subscribe(IObserver<CommittedChangeSet> observer) => _feed.Subscribe(observer);

    /// <summary>
    /// Stops accepting work, lets every queued operation finish, checkpoints the WAL into the database file
    /// (<c>wal_checkpoint(TRUNCATE)</c>), closes the writer, and completes the change feed (T-06.6).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _queue.Writer.TryComplete();
        }

        await _stopped.Task.ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="query"/> on the writer thread, ordered after all previously queued work.</summary>
    internal async Task<T> RunOnWriterAsync<T>(Func<SqliteConnection, T> query, CancellationToken cancellationToken)
    {
        var item = new WriterQuery<T>(query, cancellationToken);
        if (!await EnqueueAsync(item, cancellationToken).ConfigureAwait(false))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await item.Completion.Task.ConfigureAwait(false);
    }

    private async Task<bool> EnqueueAsync(WorkItem item, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        try
        {
            await _queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ChannelClosedException ex)
        {
            throw new ObjectDisposedException("The dataset was closed.", ex);
        }
    }

    private void RunWorker()
    {
        try
        {
            var reader = _queue.Reader;

            // Blocking is intended: this dedicated thread exists only to own the writer connection.
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var item))
                {
                    item.Run(this);
                }
            }
        }
        finally
        {
            CloseWriter();
            _feed.Complete();
            _stopped.TrySetResult();
        }
    }

    private void CloseWriter()
    {
        try
        {
            using var checkpoint = _writer.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            PersistenceLog.CloseFailed(_logger, ex);
        }
        finally
        {
            _writer.Dispose();
        }
    }

    private AppResult<CommandOutcome<T>> RunMutation<T>(MutationItem<T> item)
    {
        // T-06.6: cancellation counts only until the transaction begins.
        if (item.CancellationToken.IsCancellationRequested)
        {
            return AppResult.Failure<CommandOutcome<T>>(OperationCancelledError.Instance);
        }

        try
        {
            // T-05: a retry after an uncertain outcome replays the recorded result instead of applying twice.
            if (OperationLedger.Find(_writer, item.OperationId) is { } recorded)
            {
                if (recorded.Kind != item.OperationKind)
                {
                    throw new InvalidOperationException($"Operation {item.OperationId} was recorded as {recorded.Kind}, not {item.OperationKind}.");
                }

                var value = StorageJson.ReadLedgerResult(recorded.ResultJson, item.ResultType);
                return AppResult.Success(new CommandOutcome<T>(value, recorded.Sequence, WasAlreadyApplied: true));
            }
        }
        catch (Exception ex) when (SqliteFailures.Classify(ex) is { } kind)
        {
            PersistenceLog.TransactionFailed(_logger, item.OperationId.Value, item.OperationKind, kind, ex);
            return AppResult.Failure<CommandOutcome<T>>(new StorageFailureError(kind));
        }

        var utcNow = _time.GetUtcNow();
        var localToday = _time.LocalToday();
        SqliteTransaction? transaction = null;
        SqliteWriteTransaction? tx = null;
        TxResult<T> result;
        long sequence;
        try
        {
            transaction = _writer.BeginTransaction(deferred: false);
            tx = new SqliteWriteTransaction(_writer, transaction, utcNow, localToday, FaultInjection);
            result = item.Work(tx);
            tx.Close();
            if (!result.IsCommit)
            {
                EndFailedTransaction(transaction);
                return AppResult.Failure<CommandOutcome<T>>(result.Error!);
            }

            sequence = OperationLedger.Record(
                _writer, transaction, item.OperationId, item.OperationKind, utcNow, StorageJson.WriteLedgerResult(result.Value, item.ResultType));
        }
        catch (Exception ex)
        {
            tx?.Close();
            if (transaction is not null)
            {
                EndFailedTransaction(transaction);
            }

            if (SqliteFailures.Classify(ex) is { } kind)
            {
                PersistenceLog.TransactionFailed(_logger, item.OperationId.Value, item.OperationKind, kind, ex);
                return AppResult.Failure<CommandOutcome<T>>(new StorageFailureError(kind));
            }

            // Constraint violations and other exceptions are defects; they propagate after rollback.
            PersistenceLog.TransactionDefect(_logger, item.OperationId.Value, item.OperationKind, ex);
            throw;
        }

        try
        {
            FaultInjection?.Invoke("commit.before");
            transaction.Commit();
            FaultInjection?.Invoke("commit.after");
        }
        catch (Exception ex)
        {
            // The storage cannot say whether COMMIT reached the file. Reporting failure could make the user
            // redo committed work and reporting success could hide lost work, so the caller is told the
            // outcome is unknown; the ledger answers later (GetOperationStatusAsync or a same-ID retry).
            // Nothing is published, so subscribers see a sequence gap if it did commit (T-07).
            PersistenceLog.CommitOutcomeUnknown(_logger, item.OperationId.Value, item.OperationKind, ex);
            EndFailedTransaction(transaction);
            return AppResult.Failure<CommandOutcome<T>>(new CommitOutcomeUnknownError(item.OperationId));
        }

        transaction.Dispose();
        _feed.Publish(tx.Changes.Build(sequence, item.OperationId, item.OperationKind, utcNow));
        return AppResult.Success(new CommandOutcome<T>(result.Value, sequence, WasAlreadyApplied: false));
    }

    /// <summary>Rolls back if SQLite still has the transaction open (it may already have rolled back itself).</summary>
    private void EndFailedTransaction(SqliteTransaction transaction)
    {
        try
        {
            if (SQLitePCL.raw.sqlite3_get_autocommit(_writer.Handle) == 0)
            {
                transaction.Rollback();
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            PersistenceLog.RollbackFailed(_logger, ex);
        }
        finally
        {
            try
            {
                transaction.Dispose();
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                PersistenceLog.RollbackFailed(_logger, ex);
            }
        }
    }

    private abstract class WorkItem
    {
        public abstract void Run(SqliteMutationCoordinator owner);
    }

    private sealed class MutationItem<T>(
        OperationId operationId,
        string operationKind,
        Func<IWriteTransaction, TxResult<T>> work,
        JsonTypeInfo<T> resultType,
        CancellationToken cancellationToken) : WorkItem
    {
        public OperationId OperationId { get; } = operationId;

        public string OperationKind { get; } = operationKind;

        public Func<IWriteTransaction, TxResult<T>> Work { get; } = work;

        public JsonTypeInfo<T> ResultType { get; } = resultType;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public TaskCompletionSource<AppResult<CommandOutcome<T>>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Run(SqliteMutationCoordinator owner)
        {
            try
            {
                Completion.TrySetResult(owner.RunMutation(this));
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }
    }

    private sealed class WriterQuery<T>(Func<SqliteConnection, T> query, CancellationToken cancellationToken) : WorkItem
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Run(SqliteMutationCoordinator owner)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                Completion.TrySetResult(query(owner._writer));
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }
    }
}
