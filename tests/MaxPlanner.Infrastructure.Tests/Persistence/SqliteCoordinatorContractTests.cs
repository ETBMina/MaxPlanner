using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

/// <summary>
/// The coordinator contract that the in-memory fake (Application.Tests MutationCoordinatorContractTests)
/// specifies, verified against real SQLite files.
/// </summary>
[Trait("Req", "T-06")]
[Trait("Req", "T-07")]
[Trait("Req", "T-11.2")]
public sealed class SqliteCoordinatorContractTests
{
    [Fact]
    [Trait("Req", "T-05")]
    public async Task Replaying_a_committed_operation_returns_the_recorded_outcome_without_a_second_effect()
    {
        await using var db = await CreateAsync();
        using var feed = Recorder.Attach(db.Coordinator);
        var command = new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft());

        var first = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(command, Ct));
        var retry = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(command, Ct));

        first.WasAlreadyApplied.Should().BeFalse();
        retry.WasAlreadyApplied.Should().BeTrue();
        retry.Value.Should().Be(first.Value);
        retry.CommitSequence.Should().Be(first.CommitSequence);
        (await db.SnapshotAsync()).Tasks.Should().ContainSingle();
        feed.Sequences.Should().Equal(1);
    }

    [Fact]
    public async Task Sequences_are_gap_free_because_aborted_operations_consume_none()
    {
        await using var db = await CreateAsync();
        using var feed = Recorder.Attach(db.Coordinator);
        var first = await db.AddTaskAsync("One");
        var stale = await new UpdateTaskContent(db.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), first, ExpectedRevision: 99, Draft("Stale")), Ct);
        await db.AddTaskAsync("Two");
        await db.AddTaskAsync("Three");

        Failed<long, ConcurrencyConflictError>(stale);
        feed.Sequences.Should().Equal(1, 2, 3);
        db.Scalar("SELECT commit_sequence FROM app_metadata;").Should().Be(3);
        db.Scalar("SELECT COUNT(*) FROM operation_ledger;").Should().Be(3);
        db.Scalar("SELECT MAX(commit_sequence) FROM operation_ledger;").Should().Be(3);
        db.Coordinator.LastPublishedSequence.Should().Be(3);
    }

    [Fact]
    public async Task A_throwing_transaction_body_rolls_back_everything_consumes_no_sequence_and_propagates()
    {
        await using var db = await CreateAsync();
        using var feed = Recorder.Attach(db.Coordinator);
        var taskId = new TaskId(Guid.NewGuid());

        var act = () => db.Coordinator.ExecuteAsync<long>(OperationId.New(), "test.defect", tx =>
        {
            var backlog = tx.Workflow.Read().TaskStatuses[0];
            var content = new TaskContent(TitleText.Create("Never stored").Value, null, [], [], [], null, CustomValue.NoValues);
            tx.Tasks.Insert(TaskItem.Create(taskId, content, backlog, tx.Workflow.Read().Defaults.DefaultPriorityId, default, default, null, 1, tx.LocalToday, tx.UtcNow).Value);
            throw new InvalidOperationException("Defect inside the transaction.");
        }, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Defect inside the transaction.");
        db.Scalar("SELECT COUNT(*) FROM tasks;").Should().Be(0);
        db.Scalar("SELECT commit_sequence FROM app_metadata;").Should().Be(0);
        Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft()), Ct)).CommitSequence.Should().Be(1);
        feed.Sequences.Should().Equal(1);
    }

    [Fact]
    [Trait("Req", "T-06.6")]
    public async Task Cancellation_before_the_transaction_begins_changes_nothing()
    {
        await using var db = await CreateAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft()), cancelled.Token);

        Failed<TaskId, OperationCancelledError>(result);
        db.Scalar("SELECT COUNT(*) FROM tasks;").Should().Be(0);
        db.Scalar("SELECT COUNT(*) FROM operation_ledger;").Should().Be(0);
    }

    [Fact]
    [Trait("Req", "T-06.6")]
    public async Task Cancelling_an_operation_while_it_waits_in_the_queue_changes_nothing()
    {
        await using var db = await CreateAsync();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var blocker = db.Coordinator.RunOnWriterAsync(_ => release.Wait(TimeSpan.FromSeconds(30)), Ct);

        var queued = new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft()), cancellation.Token);
        await cancellation.CancelAsync();
        release.Set();
        await blocker;

        Failed<TaskId, OperationCancelledError>(await queued);
        db.Scalar("SELECT COUNT(*) FROM tasks;").Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_callers_are_committed_and_published_in_one_gap_free_order()
    {
        await using var db = await CreateAsync();
        using var feed = Recorder.Attach(db.Coordinator);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => Task.Run(() => new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft($"Task {i}")), Ct))));

        outcomes.Select(o => Succeeded(o).CommitSequence).Should().BeEquivalentTo(Enumerable.Range(1, 25).Select(i => (long)i));
        feed.Sequences.Should().Equal(Enumerable.Range(1, 25).Select(i => (long)i));
        feed.ChangeSets.Should().AllSatisfy(c => c.UpsertedTasks.Should().ContainSingle());
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_fail_the_commit_and_is_ended_with_OnError()
    {
        await using var db = await CreateAsync();
        var faulty = new Recorder(throwOnNext: true);
        using var faultySubscription = db.Coordinator.Subscribe(faulty);
        using var healthy = Recorder.Attach(db.Coordinator);

        var first = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft()), Ct));
        await db.AddTaskAsync("Second");

        first.CommitSequence.Should().Be(1);
        faulty.Sequences.Should().Equal(1);
        faulty.Error.Should().BeOfType<InvalidOperationException>();
        healthy.Sequences.Should().Equal(1, 2);
        (await db.SnapshotAsync()).Tasks.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Req", "T-05")]
    public async Task Ledger_outcomes_survive_a_restart_and_the_sequence_continues()
    {
        await using var db = await CreateAsync();
        var command = new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft());
        var original = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(command, Ct));

        await db.ReopenAsync();
        var replayed = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(command, Ct));
        var next = await new CreateTask(db.Coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft()), Ct);

        db.Coordinator.LastPublishedSequence.Should().Be(2);
        replayed.WasAlreadyApplied.Should().BeTrue();
        replayed.Value.Should().Be(original.Value);
        replayed.CommitSequence.Should().Be(1);
        Succeeded(next).CommitSequence.Should().Be(2);
        (await db.SnapshotAsync()).Tasks.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_replay_under_a_different_operation_kind_is_a_defect()
    {
        await using var db = await CreateAsync();
        var command = new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft());
        await new CreateTask(db.Coordinator).ExecuteAsync(command, Ct);

        var act = () => db.Coordinator.ExecuteAsync(command.OperationId, "something.else", _ => TxResult.Commit(1L), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Result_types_the_ledger_cannot_record_are_refused_before_anything_runs()
    {
        await using var db = await CreateAsync();
        var ran = false;

        var act = () => db.Coordinator.ExecuteAsync(OperationId.New(), "test.unsupported", _ =>
        {
            ran = true;
            return TxResult.Commit("text");
        }, Ct);

        await act.Should().ThrowAsync<NotSupportedException>();
        ran.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "B-04")]
    public async Task Today_is_the_users_local_day_captured_once_per_transaction()
    {
        await using var db = await CreateAsync();
        // 23:30 UTC is already the next day at UTC+3.
        db.Clock.UtcNow = new DateTimeOffset(2026, 10, 22, 23, 30, 0, TimeSpan.Zero);
        db.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");

        var id = await db.AddTaskAsync(status: (await db.TaskStatusAsync("In Progress")).Id);

        var task = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == id);
        task.Dates.Start.Should().Be(new DateOnly(2026, 10, 23));
        task.CreatedAtUtc.Should().Be(new DateTimeOffset(2026, 10, 22, 23, 30, 0, TimeSpan.Zero));
        db.Scalar("SELECT start_date = '2026-10-23' FROM tasks;").Should().Be(1, "dates are stored as ISO text, never via UTC");
    }

    [Fact]
    public async Task Closing_drains_queued_operations_and_checkpoints_the_wal()
    {
        await using var db = await CreateAsync();
        var pending = Enumerable.Range(0, 10)
            .Select(i => new CreateTask(db.Coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()), Draft($"Task {i}")), Ct))
            .ToList();

        await db.CloseAsync();

        (await Task.WhenAll(pending)).Should().AllSatisfy(r => r.IsSuccess.Should().BeTrue());
        new FileInfo(db.Paths.DatabaseFile + "-wal").Exists.Should().BeFalse("closing the last connection after TRUNCATE leaves no WAL");
        db.Scalar("SELECT COUNT(*) FROM tasks;").Should().Be(10);
    }

    /// <summary>Records change sets; optionally throws to simulate a failing projection store.</summary>
    internal sealed class Recorder(bool throwOnNext = false) : IObserver<CommittedChangeSet>, IDisposable
    {
        private readonly List<CommittedChangeSet> _changeSets = [];
        private IDisposable? _subscription;

        public IReadOnlyList<CommittedChangeSet> ChangeSets
        {
            get
            {
                lock (_changeSets)
                {
                    return [.. _changeSets];
                }
            }
        }

        public IReadOnlyList<long> Sequences => [.. ChangeSets.Select(c => c.Sequence)];

        public Exception? Error { get; private set; }

        public bool IsCompleted { get; private set; }

        public static Recorder Attach(IChangeFeed feed)
        {
            var recorder = new Recorder();
            recorder._subscription = feed.Subscribe(recorder);
            return recorder;
        }

        public void OnNext(CommittedChangeSet value)
        {
            lock (_changeSets)
            {
                _changeSets.Add(value);
            }

            if (throwOnNext)
            {
                throw new InvalidOperationException("Subscriber failure.");
            }
        }

        public void OnError(Exception error) => Error = error;

        public void OnCompleted() => IsCompleted = true;

        public void Dispose() => _subscription?.Dispose();
    }
}
