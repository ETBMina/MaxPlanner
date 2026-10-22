using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Identifiers;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

[Trait("Req", "T-06.2")]
[Trait("Req", "T-11.2")]
public sealed class ReadSnapshotTests
{
    [Fact]
    [Trait("Req", "T-07")]
    public async Task Each_snapshot_matches_its_commit_sequence_while_the_writer_keeps_committing()
    {
        await using var db = await CreateAsync();
        var writes = Task.Run(async () =>
        {
            for (var i = 0; i < 60; i++)
            {
                await db.AddTaskAsync($"Task {i}");
            }
        }, Ct);

        var snapshots = new List<ReadSnapshot>();
        while (!writes.IsCompleted)
        {
            snapshots.Add(await db.SnapshotAsync());
        }

        await writes;
        snapshots.Add(await db.SnapshotAsync());

        // Every commit adds exactly one task, so a consistent snapshot has as many tasks as its sequence.
        snapshots.Should().AllSatisfy(s => s.Tasks.Should().HaveCount((int)s.CommitSequence));
        snapshots[^1].CommitSequence.Should().Be(60);
        snapshots.Select(s => s.CommitSequence).Distinct().Should().HaveCountGreaterThan(1, "snapshots were taken while commits continued");
    }

    [Fact]
    [Trait("Req", "T-07")]
    public async Task A_snapshot_holds_archived_tasks_cards_links_reviews_and_general_todos()
    {
        await using var db = await CreateAsync();
        var alice = db.AddPerson("Alice");
        var cr = await db.AddCrAsync(alice);
        var task = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()),
            Draft(title: null, service: "Cards"), LinkTo: new CrLinkTarget(cr, 1)), Ct)).Value;
        Succeeded(await new CreateSubtaskCard(db.Coordinator).ExecuteAsync(new CreateSubtaskCardCommand(OperationId.New(), task, new SubtaskCardId(Guid.NewGuid()), "Card"), Ct));
        db.Execute("UPDATE tasks SET is_archived = 1 WHERE id = $id;", ("$id", task.Value));
        var review = Guid.NewGuid();
        db.Execute(
            """
            INSERT INTO review_items (id, cr_number_text, title, task_number_text, developer_id, comments, is_done, completed_on, rank, created_at_utc, updated_at_utc, revision)
            VALUES ($id, 'CR-9', 'Review login', '007', $dev, 'Looks good', 1, '2026-10-21', 1024, 0, 0, 1);
            """,
            ("$id", review),
            ("$dev", alice.Value));
        db.Execute("INSERT INTO review_services (review_id, position, service) VALUES ($id, 0, 'Auth'), ($id, 1, 'Mobile');", ("$id", review));
        db.Execute("INSERT INTO general_todos (id, text, is_completed, rank, created_at_utc, updated_at_utc, revision) VALUES ($id, 'Call bank', 0, 1024, 0, 0, 1);", ("$id", Guid.NewGuid()));

        var snapshot = await db.SnapshotAsync();

        snapshot.Tasks.Should().ContainSingle(t => t.Id == task && t.IsArchived && t.ChangeRequestId == cr);
        snapshot.SubtaskCards.Should().ContainSingle(c => c.ParentTaskId == task);
        snapshot.Links.Should().ContainSingle(l => l.TaskId == task && l.ChangeRequestId == cr);
        var item = snapshot.ReviewItems.Single();
        (item.Content.Title.Value, item.Content.CrNumberText!.Value, item.Content.TaskNumberText!.Value, item.Content.DeveloperId, item.IsDone, item.CompletedOn)
            .Should().Be(("Review login", "CR-9", "007", alice, true, new DateOnly(2026, 10, 21)));
        item.Content.Services.Select(s => s.Value).Should().Equal("Auth", "Mobile");
        snapshot.GeneralTodos.Should().ContainSingle(t => t.Text.Value == "Call bank" && !t.IsCompleted);
    }

    [Fact]
    [Trait("Req", "T-09")]
    public async Task A_row_that_breaks_a_domain_rule_is_reported_as_corrupt_never_skipped()
    {
        await using var db = await CreateAsync();
        var alice = db.AddPerson("Alice");
        var cr = await db.AddCrAsync(alice);
        var task = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()),
            Draft(title: null, service: "Cards"), LinkTo: new CrLinkTarget(cr, 1)), Ct)).Value;

        // ADR 0005: a linked task has no title of its own; a stored one means the row was damaged.
        db.Execute("UPDATE tasks SET own_title = 'Shadow title';");
        var snapshot = await db.Dataset.Snapshots.LoadAsync(Ct);
        var write = await new UpdateTaskContent(db.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), task, 1, Draft(title: null, service: "Cards")), Ct);

        snapshot.Error.Should().Be(new StorageFailureError(StorageFailureKind.Corrupt));
        Failed<long, StorageFailureError>(write).Kind.Should().Be(StorageFailureKind.Corrupt);
    }

    [Fact]
    public async Task A_cancelled_load_returns_the_cancellation_error()
    {
        await using var db = await CreateAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await db.Dataset.Snapshots.LoadAsync(cancelled.Token);

        result.Error.Should().Be(OperationCancelledError.Instance);
    }
}
