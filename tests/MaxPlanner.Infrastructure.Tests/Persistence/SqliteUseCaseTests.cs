using System.Globalization;
using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using Microsoft.Data.Sqlite;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

/// <summary>The real Application use cases running against real SQLite files (T-11.2).</summary>
[Trait("Req", "T-11.2")]
public sealed class SqliteUseCaseTests
{
    [Fact]
    [Trait("Req", "T-05")]
    public async Task Tasks_and_crs_written_by_use_cases_reload_identically_after_a_restart()
    {
        await using var db = await CreateAsync();
        using var feed = SqliteCoordinatorContractTests.Recorder.Attach(db.Coordinator);
        var alice = db.AddPerson("Alice");
        var bob = db.AddPerson("Bob");
        var mobile = db.AddTag("Mobile");
        var estimate = db.AddField("estimate", FieldKind.Number);
        var highRisk = Guid.NewGuid();
        var risk = db.AddField("risk", FieldKind.Lookup, $$"""[{"Id":"{{highRisk}}","Label":"High","Order":0}]""");
        var cards = (await db.SnapshotAsync()).Workflow.Squads.Single(s => s.Name.Display == "Retail Cards").Id;

        var crId = new ChangeRequestId(Guid.NewGuid());
        Succeeded(await new CreateChangeRequest(db.Coordinator).ExecuteAsync(new CreateChangeRequestCommand(OperationId.New(), crId, new ChangeRequestDraft(
            " CR-105 ", "Instant card freeze", alice, cards, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), "Re: freeze",
            "https://example.test/design?id=7", "Multi\nline ✓", "0042", CustomValue.NoValues)), Ct));

        var taskId = new TaskId(Guid.NewGuid());
        var custom = new Dictionary<FieldId, CustomValue>
        {
            [estimate] = new NumberCustomValue(3.25m),
            [risk] = new LookupCustomValue(new FieldOptionId(highRisk)),
        };
        var created = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), taskId,
            new TaskContentDraft("Own title", "Mobile Banking", [alice, bob], [bob], [mobile], "Line 1\r\nLïne 2 — ✓", custom)), Ct));
        var revision = Succeeded(await new AddChecklistItem(db.Coordinator).ExecuteAsync(
            new AddChecklistItemCommand(OperationId.New(), taskId, 1, new ChecklistItemId(Guid.NewGuid()), "First"), Ct)).Value;
        revision = Succeeded(await new AddChecklistItem(db.Coordinator).ExecuteAsync(
            new AddChecklistItemCommand(OperationId.New(), taskId, revision, new ChecklistItemId(Guid.NewGuid()), "Second"), Ct)).Value;
        Succeeded(await new LinkTaskToChangeRequest(db.Coordinator).ExecuteAsync(
            new LinkTaskToChangeRequestCommand(OperationId.New(), taskId, revision, crId, 1), Ct));
        var linkedAtCreation = await new CreateTask(db.Coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()),
            Draft(title: null, service: "Cards API"), LinkTo: new CrLinkTarget(crId, 1)), Ct);

        var expectedTasks = Latest(feed, c => c.UpsertedTasks, t => t.Id);
        var expectedCr = Latest(feed, c => c.UpsertedChangeRequests, c => c.Id).Single();
        var expectedLinks = feed.ChangeSets.SelectMany(c => c.AddedLinks).ToList();
        created.Value.Should().Be(taskId);
        Succeeded(linkedAtCreation);

        await db.ReopenAsync();
        var snapshot = await db.SnapshotAsync();

        snapshot.Tasks.Select(Shape).Should().BeEquivalentTo(expectedTasks.Select(Shape));
        snapshot.ChangeRequests.Select(Shape).Should().Equal(Shape(expectedCr));
        snapshot.Links.Should().BeEquivalentTo(expectedLinks);
        var linked = snapshot.Tasks.Single(t => t.Id == taskId);
        linked.OwnTitle.Should().BeNull();
        linked.Preserved.PreLinkTitle.Should().Be("Own title");
        linked.ChecklistItems.Select(i => i.Text.Value).Should().Equal("First", "Second");
        snapshot.ChangeRequests[0].Number.Display.Should().Be("CR-105");
        snapshot.ChangeRequests[0].TaskNumber!.Value.Should().Be("0042", "leading zeros are text");
        snapshot.CommitSequence.Should().Be(6);
    }

    [Fact]
    [Trait("Req", "B-07")]
    public async Task Renumbering_writes_only_changed_cards_and_orders_rank_ties_by_dotnet_guid_comparison()
    {
        await using var db = await CreateAsync();
        var backlog = await db.TaskStatusAsync("Backlog");
        var blocked = await db.TaskStatusAsync("Blocked");

        // .NET orders low < high (first field 1 < 256), but their stored bytes (01 00 .. vs 00 01 ..)
        // sort the other way in SQLite, so ordering in SQL would swap them.
        var low = new Guid("00000001-0000-0000-0000-0000000000aa");
        var high = new Guid("00000100-0000-0000-0000-0000000000bb");
        low.CompareTo(high).Should().BeNegative();
        var first = await db.AddTaskAsync("P");
        await db.AddTaskAsync("Low", id: low);
        await db.AddTaskAsync("High", id: high);
        var last = await db.AddTaskAsync("Q");
        var archived = await db.AddTaskAsync("Archived, still ranked");
        var moved = await db.AddTaskAsync("Moved", blocked.Id);
        SetRank(db, first.Value, 1024);
        SetRank(db, low, 2048);
        SetRank(db, high, 2048);
        SetRank(db, last.Value, 2049);
        SetRank(db, archived.Value, 2048);
        db.Execute("UPDATE tasks SET is_archived = 1 WHERE id = $id;", ("$id", archived.Value));

        // Between 2048 and 2049 there is no free rank, so the full column is renumbered (B-07).
        var result = await new MoveTask(db.Coordinator).ExecuteAsync(
            new MoveTaskCommand(OperationId.New(), moved, 1, backlog.Id, BoardPlacement.Between(2048, 2049)), Ct);

        Succeeded(result);
        var tasks = (await db.SnapshotAsync()).Tasks.ToDictionary(t => t.Id.Value);
        (tasks[first.Value].Rank, tasks[first.Value].Revision).Should().Be((1024, 1), "already in place: not written");
        (tasks[low].Rank, tasks[low].Revision).Should().Be((2048, 1), "the .NET-first tied card keeps its rank");
        (tasks[high].Rank, tasks[high].Revision).Should().Be((3072, 2));
        (tasks[moved.Value].Rank, tasks[moved.Value].StatusId).Should().Be((4096, backlog.Id));
        (tasks[last.Value].Rank, tasks[last.Value].Revision).Should().Be((5120, 2));
        (tasks[archived.Value].Rank, tasks[archived.Value].Revision).Should().Be((2048, 1), "archived tasks are not in the Board column");
    }

    [Fact]
    [Trait("Req", "B-16.5")]
    public async Task A_board_column_holds_active_tasks_and_the_subtask_cards_of_active_parents()
    {
        await using var db = await CreateAsync();
        var backlog = await db.TaskStatusAsync("Backlog");
        var parent = await db.AddTaskAsync("Parent");
        var card = Succeeded(await new CreateSubtaskCard(db.Coordinator).ExecuteAsync(
            new CreateSubtaskCardCommand(OperationId.New(), parent, new SubtaskCardId(Guid.NewGuid()), "Card"), Ct)).Value;
        var archivedParent = await db.AddTaskAsync("Archived parent");
        await new CreateSubtaskCard(db.Coordinator).ExecuteAsync(
            new CreateSubtaskCardCommand(OperationId.New(), archivedParent, new SubtaskCardId(Guid.NewGuid()), "Hidden card"), Ct);
        db.Execute("UPDATE tasks SET is_archived = 1 WHERE id = $id;", ("$id", archivedParent.Value));

        IReadOnlyList<(BoardCardKey Key, long Rank)> column = [];
        long? max = null;
        await db.Coordinator.ExecuteAsync(OperationId.New(), "test.read-column", tx =>
        {
            column = tx.Tasks.ListBoardColumn(backlog.Id);
            max = tx.Tasks.MaxBoardRank(backlog.Id);
            return TxResult.Abort<long>(OperationCancelledError.Instance);
        }, Ct);

        column.Select(c => c.Key).Should().Equal(new BoardCardKey(BoardCardKind.Task, parent.Value), new BoardCardKey(BoardCardKind.SubtaskCard, card.Value));
        max.Should().Be(column[^1].Rank);
    }

    [Fact]
    [Trait("Req", "B-03.7")]
    public async Task Links_are_permanent_in_the_use_case_and_in_the_database()
    {
        await using var db = await CreateAsync();
        var alice = db.AddPerson("Alice");
        var cr = await db.AddCrAsync(alice, "CR-1");
        var other = await db.AddCrAsync(alice, "CR-2");
        var task = await db.AddTaskAsync(service: "Cards");
        Succeeded(await new LinkTaskToChangeRequest(db.Coordinator).ExecuteAsync(new LinkTaskToChangeRequestCommand(OperationId.New(), task, 1, cr, 1), Ct));

        var relink = await new LinkTaskToChangeRequest(db.Coordinator).ExecuteAsync(new LinkTaskToChangeRequestCommand(OperationId.New(), task, 2, other, 1), Ct);

        Failed<long, ValidationFailedError>(relink).Errors.Should().ContainSingle(e => e.Code == DomainErrorCodes.TaskAlreadyLinked);
        var reassign = () => db.Execute("UPDATE cr_task_links SET cr_id = $cr WHERE task_id = $task;", ("$cr", other.Value), ("$task", task.Value));
        reassign.Should().Throw<SqliteException>().WithMessage("*permanent*");
        var secondLink = () => db.Execute(
            "INSERT INTO cr_task_links (task_id, cr_id, linked_at_utc) VALUES ($task, $cr, 0);", ("$cr", other.Value), ("$task", task.Value));
        secondLink.Should().Throw<SqliteException>().Which.SqliteExtendedErrorCode.Should().Be(1555, "SQLITE_CONSTRAINT_PRIMARYKEY");
        (await db.SnapshotAsync()).Links.Should().ContainSingle(l => l.TaskId == task && l.ChangeRequestId == cr);
    }

    [Theory]
    [InlineData("CR-105")]
    [InlineData(" cr-105 ")]
    [InlineData("Cr-105")]
    [Trait("Req", "B-03.2")]
    public async Task A_duplicate_cr_number_in_any_spelling_is_rejected_naming_the_existing_cr(string duplicate)
    {
        await using var db = await CreateAsync();
        var alice = db.AddPerson("Alice");
        var existing = await db.AddCrAsync(alice, "CR-105");

        var result = await new CreateChangeRequest(db.Coordinator).ExecuteAsync(new CreateChangeRequestCommand(OperationId.New(), new ChangeRequestId(Guid.NewGuid()),
            new ChangeRequestDraft(duplicate, "Another", alice, null, null, null, null, null, null, null, null, CustomValue.NoValues)), Ct);

        Failed<ChangeRequestId, DuplicateCrNumberError>(result).Should().Be(new DuplicateCrNumberError("CR-105", existing));
        db.Scalar("SELECT COUNT(*) FROM change_requests;").Should().Be(1);
    }

    [Fact]
    [Trait("Req", "B-05")]
    [Trait("Req", "AC-30")]
    public async Task Cascade_deletion_removes_the_cr_links_tasks_items_and_cards_with_tombstones()
    {
        await using var db = await CreateAsync();
        var fixture = await CascadeFixture.ArrangeAsync(db);

        var result = await fixture.DeleteAsync(db);

        Succeeded(result).Value.Should().Be(new CascadeDeletionSummary(2, 3, 2));
        foreach (var table in new[] { "change_requests", "cr_task_links", "subtask_cards" })
        {
            db.Scalar($"SELECT COUNT(*) FROM {table};").Should().Be(0, table);
        }

        db.Scalar("SELECT COUNT(*) FROM tasks;").Should().Be(1, "the unrelated task stays");
        db.Scalar("SELECT COUNT(*) FROM checklist_items;").Should().Be(0);
        db.Scalar("SELECT COUNT(*) FROM tombstones;").Should().Be(1 + 2 + 3 + 2);
        db.Scalar("SELECT COUNT(*) FROM tombstones WHERE entity_kind = 'ChangeRequest' AND id = $id;", ("$id", fixture.Cr.Value)).Should().Be(1);
    }

    [Fact]
    [Trait("Req", "B-05")]
    [Trait("Req", "T-11.7")]
    public async Task A_failure_in_the_middle_of_a_cascade_deletes_nothing()
    {
        await using var db = await CreateAsync();
        var fixture = await CascadeFixture.ArrangeAsync(db);
        var before = Counts(db);
        db.Coordinator.FaultInjection = write =>
        {
            if (write == "change-requests.delete")
            {
                throw new SqliteException("disk I/O error", 10);
            }
        };

        var result = await fixture.DeleteAsync(db);
        db.Coordinator.FaultInjection = null;

        Failed<CascadeDeletionSummary, StorageFailureError>(result).Kind.Should().Be(StorageFailureKind.Io);
        Counts(db).Should().Equal(before);
        db.Scalar("SELECT COUNT(*) FROM tombstones;").Should().Be(0);
        Succeeded(await fixture.DeleteAsync(db)).WasAlreadyApplied.Should().BeFalse("the rolled-back operation is not in the ledger");
    }

    [Fact]
    [Trait("Req", "B-16.3")]
    [Trait("Req", "T-06.3")]
    public async Task Popping_out_a_checklist_item_is_atomic()
    {
        await using var db = await CreateAsync();
        var task = await db.AddTaskAsync();
        var item = new ChecklistItemId(Guid.NewGuid());
        var revision = Succeeded(await new AddChecklistItem(db.Coordinator).ExecuteAsync(new AddChecklistItemCommand(OperationId.New(), task, 1, item, "Pop me"), Ct)).Value;
        var command = new PopOutChecklistItemCommand(OperationId.New(), task, revision, item, new SubtaskCardId(Guid.NewGuid()));
        db.Coordinator.FaultInjection = write =>
        {
            if (write == "subtask-cards.insert")
            {
                throw new SqliteException("disk I/O error", 10);
            }
        };

        var failed = await new PopOutChecklistItem(db.Coordinator).ExecuteAsync(command, Ct);
        db.Coordinator.FaultInjection = null;

        Failed<SubtaskCardId, StorageFailureError>(failed);
        db.Scalar("SELECT COUNT(*) FROM checklist_items;").Should().Be(1);
        db.Scalar("SELECT COUNT(*) FROM subtask_cards;").Should().Be(0);
        db.Scalar("SELECT COUNT(*) FROM tombstones;").Should().Be(0);
        db.Scalar("SELECT revision FROM tasks;").Should().Be(revision);

        var card = Succeeded(await new PopOutChecklistItem(db.Coordinator).ExecuteAsync(command, Ct)).Value;

        var snapshot = await db.SnapshotAsync();
        snapshot.Tasks.Single().ChecklistItems.Should().BeEmpty();
        snapshot.SubtaskCards.Should().ContainSingle(c => c.Id == card && c.Subtitle.Value == "Pop me" && c.ParentTaskId == task);
        db.Scalar("SELECT COUNT(*) FROM tombstones WHERE entity_kind = 'ChecklistItem';").Should().Be(1);
    }

    [Fact]
    [Trait("Req", "T-05")]
    public async Task A_stale_revision_is_a_concurrency_conflict_and_nothing_changes()
    {
        await using var db = await CreateAsync();
        var task = await db.AddTaskAsync("Original");
        Succeeded(await new UpdateTaskContent(db.Coordinator).ExecuteAsync(new UpdateTaskContentCommand(OperationId.New(), task, 1, Draft("Edited")), Ct));

        var stale = await new UpdateTaskContent(db.Coordinator).ExecuteAsync(new UpdateTaskContentCommand(OperationId.New(), task, 1, Draft("Stale edit")), Ct);

        Failed<long, ConcurrencyConflictError>(stale).Should().Be(new ConcurrencyConflictError(EntityKind.Task, task.Value, 1, 2));
        var stored = (await db.SnapshotAsync()).Tasks.Single();
        (stored.OwnTitle!.Value, stored.Revision).Should().Be(("Edited", 2));
    }

    [Fact]
    [Trait("Req", "B-09")]
    public async Task Toggling_one_checklist_item_rewrites_only_that_row()
    {
        await using var db = await CreateAsync();
        var task = await db.AddTaskAsync();
        var first = new ChecklistItemId(Guid.NewGuid());
        var second = new ChecklistItemId(Guid.NewGuid());
        var revision = Succeeded(await new AddChecklistItem(db.Coordinator).ExecuteAsync(new AddChecklistItemCommand(OperationId.New(), task, 1, first, "One"), Ct)).Value;
        revision = Succeeded(await new AddChecklistItem(db.Coordinator).ExecuteAsync(new AddChecklistItemCommand(OperationId.New(), task, revision, second, "Two"), Ct)).Value;
        var rowIdBefore = db.Scalar("SELECT rowid FROM checklist_items WHERE id = $id;", ("$id", first.Value));

        Succeeded(await new SetChecklistItemCompleted(db.Coordinator).ExecuteAsync(new SetChecklistItemCompletedCommand(OperationId.New(), task, revision, second, true), Ct));

        db.Scalar("SELECT rowid FROM checklist_items WHERE id = $id;", ("$id", first.Value)).Should().Be(rowIdBefore, "untouched rows are not deleted and re-inserted");
        db.Scalar("SELECT is_completed FROM checklist_items WHERE id = $id;", ("$id", second.Value)).Should().Be(1);
    }

    private static void SetRank(TestDataset db, Guid id, long rank) =>
        db.Execute("UPDATE tasks SET rank = $rank WHERE id = $id;", ("$rank", rank), ("$id", id));

    private static readonly string[] CascadeTables = ["change_requests", "cr_task_links", "tasks", "checklist_items", "subtask_cards", "task_people"];

    private static long[] Counts(TestDataset db) => [.. CascadeTables.Select(table => db.Scalar($"SELECT COUNT(*) FROM {table};"))];

    private static List<T> Latest<T, TKey>(SqliteCoordinatorContractTests.Recorder feed, Func<CommittedChangeSet, IEnumerable<T>> select, Func<T, TKey> key)
        where TKey : notnull
    {
        var latest = new Dictionary<TKey, T>();
        foreach (var record in feed.ChangeSets.SelectMany(select))
        {
            latest[key(record)] = record;
        }

        return [.. latest.Values];
    }

    // Records hold lists, which record equality compares by reference; these shapes compare by content.
    private static string Shape(TaskItem t) => string.Join(" | ",
        t.Id, t.OwnTitle, t.Service, t.StatusId, t.PriorityId, string.Join(",", t.DesignerIds), string.Join(",", t.DeveloperIds), string.Join(",", t.TagIds),
        t.Notes, t.Dates.Start, t.Dates.End, t.IsArchived, t.Rank, t.ChangeRequestId,
        string.Join(";", t.ChecklistItems.Select(i => $"{i.Id}:{i.Text}:{i.IsCompleted}:{i.Rank}")),
        Describe(t.CustomValues), t.Preserved.PreLinkTitle, string.Join(",", t.Preserved.UnselectedCrReferences), t.Preserved.LegacyJson,
        t.CreatedAtUtc.UtcTicks, t.UpdatedAtUtc.UtcTicks, t.Revision);

    private static string Shape(ChangeRequest c) => string.Join(" | ",
        c.Id, c.Number.Display, c.Number.Normalized, c.Title, c.OwnerId, c.SquadId, c.StatusId, c.PriorityId, c.Planned?.Start, c.Planned?.End,
        c.EmailSubject, c.SolutionDesignUrl, c.Description, c.TaskNumber, Describe(c.CustomValues), c.CreatedAtUtc.UtcTicks, c.UpdatedAtUtc.UtcTicks, c.Revision);

    private static string Describe(IReadOnlyDictionary<FieldId, CustomValue> values) => string.Join(";", values.OrderBy(v => v.Key.Value).Select(v => v.Key + "=" + v.Value switch
    {
        NumberCustomValue n => n.Value.ToString(CultureInfo.InvariantCulture),
        LookupCustomValue l => l.OptionId.ToString(),
        var other => other.ToString(),
    }));

    private sealed record CascadeFixture(ChangeRequestId Cr, IReadOnlyCollection<TaskId> Linked)
    {
        public static async Task<CascadeFixture> ArrangeAsync(TestDataset db)
        {
            var alice = db.AddPerson("Alice");
            var cr = await db.AddCrAsync(alice);
            var linked = new List<TaskId>();
            for (var i = 0; i < 2; i++)
            {
                var task = Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), new TaskId(Guid.NewGuid()),
                    new TaskContentDraft(null, "Service", [alice], [], [], null, CustomValue.NoValues), LinkTo: new CrLinkTarget(cr, 1)), Ct)).Value;
                var revision = 1L;
                for (var item = 0; item <= i; item++)
                {
                    revision = Succeeded(await new AddChecklistItem(db.Coordinator).ExecuteAsync(
                        new AddChecklistItemCommand(OperationId.New(), task, revision, new ChecklistItemId(Guid.NewGuid()), $"Item {item}"), Ct)).Value;
                }

                Succeeded(await new CreateSubtaskCard(db.Coordinator).ExecuteAsync(
                    new CreateSubtaskCardCommand(OperationId.New(), task, new SubtaskCardId(Guid.NewGuid()), $"Card {i}"), Ct));
                linked.Add(task);
            }

            // The cascade covers archived tasks too (B-05).
            db.Execute("UPDATE tasks SET is_archived = 1 WHERE id = $id;", ("$id", linked[1].Value));
            await db.AddTaskAsync("Unrelated");
            return new CascadeFixture(cr, linked);
        }

        public Task<AppResult<CommandOutcome<CascadeDeletionSummary>>> DeleteAsync(TestDataset db) =>
            new DeleteChangeRequest(db.Coordinator).ExecuteAsync(new DeleteChangeRequestCommand(_operation, Cr, 1, Linked), Ct);

        private readonly OperationId _operation = OperationId.New();
    }
}
