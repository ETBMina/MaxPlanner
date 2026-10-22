using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Common;

namespace MaxPlanner.Application.Tests.Fakes;

/// <summary>Thrown by fake stores to simulate an I/O failure inside a transaction (fault injection).</summary>
public sealed class FakeStorageException(string message) : Exception(message);

/// <summary>
/// An in-memory <see cref="IMutationCoordinator"/> with the transaction semantics the SQLite coordinator
/// (slice 1d) must reproduce. It is an executable specification:
/// <list type="number">
/// <item>Cancellation is honored only before the transaction begins.</item>
/// <item>One transaction at a time (the single ordered writer).</item>
/// <item>A ledger hit returns the recorded outcome without running or publishing anything.</item>
/// <item>The body runs against a private copy; abort or exception discards it and consumes no sequence.</item>
/// <item>Commit writes the data, the next gap-free sequence, and the ledger entry together, then publishes.</item>
/// <item>A publication failure is not retried and does not undo the commit.</item>
/// </list>
/// </summary>
public sealed class FakeMutationCoordinator(InMemoryDataset dataset, TimeProvider timeProvider) : IMutationCoordinator
{
    private readonly Lock _writer = new();

    /// <summary>The durable state: replaced only at the commit point.</summary>
    public InMemoryDataset Committed { get; private set; } = dataset;

    /// <summary>Change sets in publication order.</summary>
    public List<CommittedChangeSet> Published { get; } = [];

    /// <summary>How many transaction bodies actually ran (ledger replays do not run one).</summary>
    public int TransactionsStarted { get; private set; }

    /// <summary>Called by every fake store before it writes; throw a <see cref="FakeStorageException"/> to inject a fault.</summary>
    public Action<string>? BeforeWrite { get; set; }

    /// <summary>Drops the next publication after its commit, as if the subscriber pipeline failed.</summary>
    public bool FailNextPublication { get; set; }

    public Task<AppResult<CommandOutcome<T>>> ExecuteAsync<T>(
        OperationId operationId,
        string operationKind,
        Func<IWriteTransaction, TxResult<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (cancellationToken.IsCancellationRequested)
        {
            return Done(AppResult.Failure<CommandOutcome<T>>(OperationCancelledError.Instance));
        }

        lock (_writer)
        {
            if (Committed.Ledger.TryGetValue(operationId, out var recorded))
            {
                if (recorded.OperationKind != operationKind)
                {
                    throw new InvalidOperationException($"Operation {operationId} was recorded as {recorded.OperationKind}, not {operationKind}.");
                }

                return Done(AppResult.Success(new CommandOutcome<T>((T)recorded.Value!, recorded.Sequence, WasAlreadyApplied: true)));
            }

            TransactionsStarted++;
            var working = Committed.Clone();
            var transaction = new FakeWriteTransaction(working, timeProvider, write => BeforeWrite?.Invoke(write));
            TxResult<T> result;
            try
            {
                result = work(transaction);
            }
            catch (FakeStorageException)
            {
                return Done(AppResult.Failure<CommandOutcome<T>>(new StorageFailureError(StorageFailureKind.Io)));
            }

            if (!result.IsCommit)
            {
                return Done(AppResult.Failure<CommandOutcome<T>>(result.Error!));
            }

            working.AssertIntegrity();
            var sequence = working.CommitSequence + 1;
            working.CommitSequence = sequence;
            working.Ledger[operationId] = new LedgerEntry(operationKind, sequence, result.Value);
            Committed = working;

            var changeSet = transaction.Changes.Build(sequence, operationId, operationKind, transaction.UtcNow);
            if (FailNextPublication)
            {
                FailNextPublication = false;
            }
            else
            {
                Published.Add(changeSet);
            }

            return Done(AppResult.Success(new CommandOutcome<T>(result.Value, sequence, WasAlreadyApplied: false)));
        }
    }

    private static Task<AppResult<CommandOutcome<T>>> Done<T>(AppResult<CommandOutcome<T>> result) => Task.FromResult(result);
}
