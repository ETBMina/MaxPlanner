using System.Globalization;
using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Identifiers;
using Microsoft.Data.Sqlite;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

[Trait("Req", "T-06.7")]
[Trait("Req", "T-11.7")]
public sealed class StorageFaultTests
{
    [Fact]
    public async Task A_full_disk_is_a_typed_failure_that_keeps_previous_data_and_commits_nothing()
    {
        await using var db = await CreateAsync();
        using var feed = SqliteCoordinatorContractTests.Recorder.Attach(db.Coordinator);
        var kept = await db.AddTaskAsync("Committed before the disk filled");

        // A real SQLITE_FULL: the database may not grow past its current size, as on a full volume.
        await db.Coordinator.RunOnWriterAsync(connection => Execute(connection, "PRAGMA max_page_count = " + Format(PageCount(connection)) + ";"), Ct);
        var draft = new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft("Large draft", notes: new string('x', 100_000)));

        var full = await new CreateTask(db.Coordinator).ExecuteAsync(draft, Ct);

        Failed<TaskId, StorageFailureError>(full).Kind.Should().Be(StorageFailureKind.DiskFull);
        var snapshot = await db.SnapshotAsync();
        snapshot.Tasks.Should().ContainSingle(t => t.Id == kept);
        snapshot.CommitSequence.Should().Be(1);
        feed.Sequences.Should().Equal(1);

        // Once space is available the caller's retained draft (same operation) commits normally.
        await db.Coordinator.RunOnWriterAsync(connection => Execute(connection, "PRAGMA max_page_count = 1073741823;"), Ct);
        var retried = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(draft, Ct));
        retried.WasAlreadyApplied.Should().BeFalse();
        retried.CommitSequence.Should().Be(2);
        db.Scalar("SELECT COUNT(*) FROM tasks;").Should().Be(2);
        await db.ReopenAsync();
        (await db.SnapshotAsync()).Tasks.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Req", "T-07")]
    public async Task A_commit_that_failed_before_reaching_the_file_is_reported_unknown_then_resolved_as_not_committed()
    {
        await using var db = await CreateAsync();
        using var feed = SqliteCoordinatorContractTests.Recorder.Attach(db.Coordinator);
        var command = new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft());
        db.Coordinator.FaultInjection = write => ThrowAt(write, "commit.before");

        var uncertain = await new CreateTask(db.Coordinator).ExecuteAsync(command, Ct);
        db.Coordinator.FaultInjection = null;

        Failed<TaskId, CommitOutcomeUnknownError>(uncertain).OperationId.Should().Be(command.OperationId);
        var status = await db.Coordinator.GetOperationStatusAsync(command.OperationId, Ct);
        status.Value.Should().Be(new OperationStatus(command.OperationId, false, null, null));
        feed.Sequences.Should().BeEmpty();

        var retried = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(command, Ct));
        (retried.WasAlreadyApplied, retried.CommitSequence).Should().Be((false, 1));
        feed.Sequences.Should().Equal(1);
    }

    [Fact]
    [Trait("Req", "T-07")]
    public async Task A_commit_that_reached_the_file_but_reported_failure_is_found_in_the_ledger_and_never_applied_twice()
    {
        await using var db = await CreateAsync();
        using var feed = SqliteCoordinatorContractTests.Recorder.Attach(db.Coordinator);
        var command = new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft());
        db.Coordinator.FaultInjection = write => ThrowAt(write, "commit.after");

        var uncertain = await new CreateTask(db.Coordinator).ExecuteAsync(command, Ct);
        db.Coordinator.FaultInjection = null;

        Failed<TaskId, CommitOutcomeUnknownError>(uncertain);
        var status = (await db.Coordinator.GetOperationStatusAsync(command.OperationId, Ct)).Value;
        (status.IsCommitted, status.OperationKind, status.CommitSequence).Should().Be((true, CreateTask.OperationKind, 1L));

        var retried = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(command, Ct));
        (retried.WasAlreadyApplied, retried.CommitSequence).Should().Be((true, 1));
        await db.AddTaskAsync("Next");

        feed.Sequences.Should().Equal([2L], "sequence 1 was never published, so subscribers see the gap and resynchronize");
        (await db.SnapshotAsync()).Tasks.Should().HaveCount(2);
    }

    private static void ThrowAt(string write, string point)
    {
        if (write == point)
        {
            throw new SqliteException("disk I/O error", 10);
        }
    }

    private static long PageCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA page_count;";
        return (long)command.ExecuteScalar()!;
    }

    private static int Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteScalar();
        return 0;
    }

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}
