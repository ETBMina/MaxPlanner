using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Identifiers;
using Microsoft.Data.Sqlite;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

/// <summary>
/// ADR 0024 on a real SQLite file: M0002 keeps the first Designer and Developer of each task by position and drops
/// the others, the schema then refuses a second person in a role, and single values survive a reopen.
/// </summary>
[Trait("Req", "T-09")]
[Trait("Req", "B-02")]
public sealed class SingleDesignerDeveloperMigrationTests
{
    [Fact]
    public async Task Upgrading_a_v1_dataset_keeps_the_first_person_per_role_by_position_and_drops_the_rest()
    {
        await using var db = await CreateAsync();
        var alice = db.AddPerson("Alice");
        var bob = db.AddPerson("Bob");
        var carol = db.AddPerson("Carol");
        var first = await db.AddTaskAsync("Several people");
        var second = await db.AddTaskAsync("One developer");
        await db.CloseAsync();

        // A v1 dataset: no unique index, and lists of people stored in position order.
        db.Execute(
            """
            DROP INDEX ux_task_people_task_role;
            UPDATE app_metadata SET schema_version = 1, migration_history_json = json_remove(migration_history_json, '$[1]');
            PRAGMA user_version = 1;
            """);
        AddPerson(db, first, bob, "Designer", 1);
        AddPerson(db, first, alice, "Designer", 0);
        AddPerson(db, first, carol, "Designer", 2);
        AddPerson(db, first, carol, "Developer", 3);
        AddPerson(db, first, alice, "Developer", 4);
        AddPerson(db, second, bob, "Developer", 0);
        var tie = new[] { alice, carol }.OrderBy(p => p.Value.ToByteArray(), Comparer<byte[]>.Create(CompareBlobs)).ToArray();
        AddPerson(db, second, tie[1], "Designer", 5);
        AddPerson(db, second, tie[0], "Designer", 5);

        await db.ReopenAsync();

        db.Dataset.SchemaVersion.Should().Be(2);
        var tasks = (await db.SnapshotAsync()).Tasks;
        var several = tasks.Single(t => t.Id == first);
        several.DesignerId.Should().Be(alice, "Alice had the lowest position among the Designers");
        several.DeveloperId.Should().Be(carol);
        var single = tasks.Single(t => t.Id == second);
        single.DesignerId.Should().Be(tie[0], "a position tie keeps the lower person_id (SQLite compares the GUID bytes)");
        single.DeveloperId.Should().Be(bob);
        db.Scalar("SELECT COUNT(*) FROM task_people;").Should().Be(4);
        db.Scalar("SELECT COUNT(*) FROM people;").Should().Be(3, "dropping an assignment never removes the person");
    }

    [Fact]
    public async Task A_designer_and_developer_survive_a_reopen_and_a_second_person_in_a_role_is_refused()
    {
        await using var db = await CreateAsync();
        var alice = db.AddPerson("Alice");
        var bob = db.AddPerson("Bob");
        var id = new TaskId(Guid.NewGuid());
        Succeeded(await new CreateTask(db.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), id, Draft("Card freeze") with { DesignerId = alice, DeveloperId = alice }), Ct));

        await db.ReopenAsync();

        var task = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == id);
        task.DesignerId.Should().Be(alice);
        task.DeveloperId.Should().Be(alice, "one person may be both Designer and Developer");
        var insert = () => AddPerson(db, id, bob, "Designer", 9);
        insert.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(19, "SQLITE_CONSTRAINT: one Designer per task");
    }

    // SQLite compares BLOBs with memcmp: byte by byte, then by length.
    private static int CompareBlobs(byte[] x, byte[] y) => ((ReadOnlySpan<byte>)x).SequenceCompareTo(y);

    private static void AddPerson(TestDataset db, TaskId task, PersonId person, string role, long position) =>
        db.Execute(
            "INSERT INTO task_people (task_id, person_id, role, position) VALUES ($task, $person, $role, $position);",
            ("$task", task.Value),
            ("$person", person.Value),
            ("$role", role),
            ("$position", position));
}
