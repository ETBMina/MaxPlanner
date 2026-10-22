using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Application.Tests.Fakes;
using MaxPlanner.Domain.Identifiers;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Coordinator;

/// <summary>
/// The coordinator contract (T-05, T-06, T-07) as exercised through real use cases. The SQLite
/// coordinator in slice 1d must pass the same scenarios.
/// </summary>
[Trait("Req", "T-06")]
[Trait("Req", "T-07")]
public sealed class MutationCoordinatorContractTests
{
    private readonly TestWorkspace _workspace = new();

    [Fact]
    [Trait("Req", "T-05")]
    public async Task Replaying_a_committed_operation_returns_the_recorded_outcome_without_a_second_effect()
    {
        var command = new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft());
        var createTask = new CreateTask(_workspace.Coordinator);

        var first = Succeeded(await createTask.ExecuteAsync(command, CancellationToken.None));
        var retry = Succeeded(await createTask.ExecuteAsync(command, CancellationToken.None));

        first.WasAlreadyApplied.Should().BeFalse();
        retry.WasAlreadyApplied.Should().BeTrue();
        retry.Value.Should().Be(first.Value);
        retry.CommitSequence.Should().Be(first.CommitSequence);
        _workspace.Data.Tasks.Should().ContainSingle();
        _workspace.Coordinator.Published.Should().ContainSingle();
        _workspace.Coordinator.TransactionsStarted.Should().Be(1);
    }

    [Fact]
    public async Task Sequences_are_gap_free_because_aborted_operations_consume_none()
    {
        var first = await _workspace.AddTaskAsync("One");
        var stale = await new UpdateTaskContent(_workspace.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), first, ExpectedRevision: 99, Draft("Stale")), CancellationToken.None);
        await _workspace.AddTaskAsync("Two");
        await _workspace.AddTaskAsync("Three");

        Failed<long, ConcurrencyConflictError>(stale);
        _workspace.Coordinator.Published.Select(c => c.Sequence).Should().Equal(1, 2, 3);
        _workspace.Data.CommitSequence.Should().Be(3);
        _workspace.Data.Ledger.Values.Select(l => l.Sequence).Should().BeEquivalentTo([1L, 2L, 3L]);
    }

    [Fact]
    [Trait("Req", "T-06.6")]
    public async Task Cancellation_before_the_transaction_begins_changes_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await new CreateTask(_workspace.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft()), cancelled.Token);

        Failed<TaskId, OperationCancelledError>(result);
        _workspace.Coordinator.TransactionsStarted.Should().Be(0);
        _workspace.Data.Tasks.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "T-06.7")]
    public async Task A_storage_failure_rolls_back_everything_and_the_same_operation_can_then_be_retried()
    {
        var before = _workspace.Data;
        var command = new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft());
        _workspace.Coordinator.BeforeWrite = write => throw new FakeStorageException(write);

        var failed = await new CreateTask(_workspace.Coordinator).ExecuteAsync(command, CancellationToken.None);
        _workspace.Coordinator.BeforeWrite = null;
        var retried = await new CreateTask(_workspace.Coordinator).ExecuteAsync(command, CancellationToken.None);

        Failed<TaskId, StorageFailureError>(failed).Kind.Should().Be(StorageFailureKind.Io);
        before.Tasks.Should().BeEmpty("the failed transaction worked on a discarded copy");
        Succeeded(retried).WasAlreadyApplied.Should().BeFalse("a rolled-back operation is not in the ledger");
        _workspace.Coordinator.Published.Select(c => c.Sequence).Should().Equal(1);
    }

    [Fact]
    public async Task A_lost_publication_is_not_retried_and_shows_up_as_a_sequence_gap()
    {
        _workspace.Coordinator.FailNextPublication = true;
        var command = new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft("Committed but unpublished"));

        var committed = Succeeded(await new CreateTask(_workspace.Coordinator).ExecuteAsync(command, CancellationToken.None));
        var replayed = Succeeded(await new CreateTask(_workspace.Coordinator).ExecuteAsync(command, CancellationToken.None));
        await _workspace.AddTaskAsync("Next");

        committed.CommitSequence.Should().Be(1);
        replayed.WasAlreadyApplied.Should().BeTrue("the durable commit is reported, never re-applied");
        _workspace.Data.Tasks.Should().HaveCount(2);
        _workspace.Coordinator.Published.Select(c => c.Sequence).Should().Equal([2L], "subscribers see 1 missing and resynchronize");
    }

    [Fact]
    [Trait("Req", "B-04")]
    public async Task Today_is_the_users_local_day_captured_once_per_transaction()
    {
        // 23:30 UTC is already the next day at UTC+3.
        _workspace.Time.SetUtcNow(new DateTimeOffset(2026, 10, 22, 23, 30, 0, TimeSpan.Zero));
        _workspace.Time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3"));

        var id = await _workspace.AddTaskAsync(status: InProgress);

        _workspace.Task(id).Dates.Start.Should().Be(new DateOnly(2026, 10, 23));
        _workspace.Task(id).CreatedAtUtc.Should().Be(new DateTimeOffset(2026, 10, 22, 23, 30, 0, TimeSpan.Zero));
    }
}
