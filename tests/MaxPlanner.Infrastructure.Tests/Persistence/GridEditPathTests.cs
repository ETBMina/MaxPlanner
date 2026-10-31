using System.Globalization;
using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using Microsoft.Data.Sqlite;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

/// <summary>
/// The failure paths of the Phase 2 Grid cell edits, through the same use cases the Grid page calls and a
/// real SQLite file (T-11.2): a full disk and a stale revision never change committed data or publish.
/// </summary>
[Trait("Req", "T-07")]
[Trait("Req", "AC-07")]
public sealed class GridEditPathTests
{
    [Fact]
    [Trait("Req", "T-06.7")]
    public async Task A_title_edit_on_a_full_disk_fails_typed_and_leaves_the_committed_task_and_feed_unchanged()
    {
        await using var db = await CreateAsync();
        using var feed = SqliteCoordinatorContractTests.Recorder.Attach(db.Coordinator);
        var task = await db.AddTaskAsync("Committed title");
        var before = (await db.SnapshotAsync()).Tasks.Single();
        FillDisk(db);

        // A long title-and-notes draft needs new pages; the page limit makes the write fail with SQLITE_FULL.
        var command = new UpdateTaskContentCommand(OperationId.New(), task, before.Revision,
            new TaskContentDraft("Renamed in the Grid", null, null, null, [], new string('n', 60_000), CustomValue.NoValues));
        var result = await new UpdateTaskContent(db.Coordinator).ExecuteAsync(command, Ct);

        Failed<long, StorageFailureError>(result).Kind.Should().Be(StorageFailureKind.DiskFull);
        var after = (await db.SnapshotAsync()).Tasks.Single();
        after.Should().BeEquivalentTo(before);
        feed.Sequences.Should().Equal(1);

        // The retained draft commits once space is available, under the same operation.
        FreeDisk(db);
        Succeeded(await new UpdateTaskContent(db.Coordinator).ExecuteAsync(command, Ct)).Value.Should().Be(before.Revision + 1);
        await db.ReopenAsync();
        (await db.SnapshotAsync()).Tasks.Single().OwnTitle!.Value.Should().Be("Renamed in the Grid");
    }

    [Fact]
    public async Task A_title_edit_from_a_stale_revision_is_a_conflict_that_keeps_the_newer_title()
    {
        await using var db = await CreateAsync();
        using var feed = SqliteCoordinatorContractTests.Recorder.Attach(db.Coordinator);
        var task = await db.AddTaskAsync("Original");
        var editStartedAt = (await db.SnapshotAsync()).Tasks.Single().Revision;

        // Another route (for example the Checklists page) changes the task after the cell editor opened.
        Succeeded(await new UpdateTaskContent(db.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), task, editStartedAt, Draft("Saved elsewhere")), Ct));
        var stale = await new UpdateTaskContent(db.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), task, editStartedAt, Draft("Grid draft")), Ct);

        var conflict = Failed<long, ConcurrencyConflictError>(stale);
        (conflict.Entity, conflict.ExpectedRevision, conflict.ActualRevision).Should().Be((EntityKind.Task, editStartedAt, editStartedAt + 1));
        (await db.SnapshotAsync()).Tasks.Single().OwnTitle!.Value.Should().Be("Saved elsewhere");
        feed.Sequences.Should().Equal(1, 2);
    }

    [Fact]
    [Trait("Req", "B-07")]
    public async Task A_status_edit_from_a_stale_revision_is_a_conflict_that_keeps_the_status_and_dates()
    {
        await using var db = await CreateAsync();
        var inProgress = await db.TaskStatusAsync("In Progress");
        var task = await db.AddTaskAsync("Card freeze");
        var editStartedAt = (await db.SnapshotAsync()).Tasks.Single().Revision;
        Succeeded(await new UpdateTaskContent(db.Coordinator).ExecuteAsync(
            new UpdateTaskContentCommand(OperationId.New(), task, editStartedAt, Draft("Renamed elsewhere")), Ct));
        var before = (await db.SnapshotAsync()).Tasks.Single();

        var result = await new MoveTask(db.Coordinator).ExecuteAsync(
            new MoveTaskCommand(OperationId.New(), task, editStartedAt, inProgress.Id, BoardPlacement.AppendToEnd), Ct);

        Failed<long, ConcurrencyConflictError>(result);
        (await db.SnapshotAsync()).Tasks.Single().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task A_subtitle_edit_from_a_stale_revision_is_a_conflict_and_a_full_disk_is_typed()
    {
        await using var db = await CreateAsync();
        var parent = await db.AddTaskAsync("Parent");
        var card = new SubtaskCardId(Guid.NewGuid());
        Succeeded(await new CreateSubtaskCard(db.Coordinator).ExecuteAsync(new CreateSubtaskCardCommand(OperationId.New(), parent, card, "Copy fix"), Ct));
        Succeeded(await new UpdateSubtaskCardContent(db.Coordinator).ExecuteAsync(
            new UpdateSubtaskCardContentCommand(OperationId.New(), card, 1, "Saved elsewhere", null), Ct));

        var conflict = Failed<long, ConcurrencyConflictError>(await new UpdateSubtaskCardContent(db.Coordinator).ExecuteAsync(
            new UpdateSubtaskCardContentCommand(OperationId.New(), card, 1, "Grid draft", null), Ct));
        conflict.Entity.Should().Be(EntityKind.SubtaskCard);

        FillDisk(db);
        var full = await new UpdateSubtaskCardContent(db.Coordinator).ExecuteAsync(
            new UpdateSubtaskCardContentCommand(OperationId.New(), card, 2, "Grid draft", new string('n', 60_000)), Ct);
        Failed<long, StorageFailureError>(full).Kind.Should().Be(StorageFailureKind.DiskFull);
        (await db.SnapshotAsync()).SubtaskCards.Single().Subtitle.Value.Should().Be("Saved elsewhere");
    }

    // The same SQLITE_FULL technique as StorageFaultTests: the file may not grow past its current size.
    private static void FillDisk(TestDataset db) =>
        db.Coordinator.RunOnWriterAsync(c => Execute(c, "PRAGMA max_page_count = " + Format(PageCount(c)) + ";"), Ct).GetAwaiter().GetResult();

    private static void FreeDisk(TestDataset db) =>
        db.Coordinator.RunOnWriterAsync(c => Execute(c, "PRAGMA max_page_count = 1073741823;"), Ct).GetAwaiter().GetResult();

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
