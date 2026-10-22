using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Persistence.Mapping;
using MaxPlanner.Infrastructure.Persistence.Migrations;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

[Trait("Req", "T-09")]
public sealed class DatasetLifecycleTests
{
    [Fact]
    [Trait("Req", "B-04")]
    public async Task A_new_dataset_is_seeded_with_exactly_the_B04_defaults()
    {
        await using var db = await TestDataset.CreateAsync();

        var snapshot = await db.SnapshotAsync();
        var workflow = snapshot.Workflow;

        workflow.TaskStatuses.Select(s => s.Name.Display).Should()
            .Equal("Backlog", "Blocked", "In Progress", "Review", "Unit Testing", "Completed", "Designing", "Developing");
        workflow.TaskStatuses.Where(s => s.SetsStartDate).Select(s => s.Name.Display).Should().Equal("In Progress", "Designing", "Developing");
        workflow.TaskStatuses.Where(s => s.IsCompleted).Select(s => s.Name.Display).Should().Equal("Completed");
        workflow.TaskStatuses.Where(s => s.IsBacklog).Select(s => s.Name.Display).Should().Equal("Backlog");
        workflow.CrStatuses.Select(s => s.Name.Display).Should().Equal("Backlog", "In Progress", "Completed");
        workflow.CrStatuses.Where(s => s.IsCompleted).Select(s => s.Name.Display).Should().Equal("Completed");
        workflow.CrStatuses.Where(s => s.IsBacklog).Select(s => s.Name.Display).Should().Equal("Backlog");
        workflow.Priorities.Select(p => p.Name.Display).Should().Equal("Urgent", "High", "Medium", "Low");
        workflow.Squads.Select(s => s.Name.Display).Should().Equal("Retail Finance", "Retail Cards");
        workflow.People.Should().BeEmpty();
        workflow.Tags.Should().BeEmpty();
        workflow.Fields.Should().BeEmpty();

        var defaults = workflow.Defaults;
        workflow.FindTaskStatus(defaults.DefaultTaskStatusId)!.Name.Display.Should().Be("Backlog");
        workflow.FindCrStatus(defaults.DefaultCrStatusId)!.Name.Display.Should().Be("Backlog");
        workflow.FindPriority(defaults.DefaultPriorityId)!.Name.Display.Should().Be("Medium");
        workflow.FindCrStatus(defaults.CalendarSchedulingStatusId)!.Name.Display.Should().Be("In Progress");
        workflow.FindCrStatus(defaults.CrBacklogStatusId)!.Name.Display.Should().Be("Backlog");
        defaults.DefaultCrOwnerId.Should().BeNull();

        snapshot.CommitSequence.Should().Be(0);
        snapshot.Tasks.Should().BeEmpty();
        snapshot.ChangeRequests.Should().BeEmpty();
        db.Dataset.Id.Value.Should().NotBeEmpty();
        db.Dataset.SchemaVersion.Should().Be(1);
        db.Scalar("PRAGMA user_version;").Should().Be(1);
        db.Scalar("SELECT schema_version FROM app_metadata;").Should().Be(1);
        StorageJson.ReadHistory(Text(db, "SELECT migration_history_json FROM app_metadata;"))
            .Select(h => (h.Version, h.Name)).Should().Equal((1, "M0001_initial"));
        Text(db, "PRAGMA journal_mode;").Should().Be("wal");
    }

    [Fact]
    [Trait("Req", "B-04")]
    public async Task Seed_ids_are_new_per_dataset_and_stable_across_reopen()
    {
        await using var first = await TestDataset.CreateAsync();
        await using var second = await TestDataset.CreateAsync();
        var before = (await first.SnapshotAsync()).Workflow;
        var datasetId = first.Dataset.Id;

        await first.ReopenAsync();
        var after = (await first.SnapshotAsync()).Workflow;

        after.TaskStatuses.Should().Equal(before.TaskStatuses);
        after.CrStatuses.Should().Equal(before.CrStatuses);
        after.Priorities.Should().Equal(before.Priorities);
        after.Squads.Should().Equal(before.Squads);
        after.Defaults.Should().Be(before.Defaults);
        first.Dataset.Id.Should().Be(datasetId);
        (await second.SnapshotAsync()).Workflow.TaskStatuses[0].Id.Should().NotBe(before.TaskStatuses[0].Id);
        second.Dataset.Id.Should().NotBe(datasetId);
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task Opening_a_missing_database_reports_missing_and_creates_nothing()
    {
        var paths = DataPaths.ForDirectory(TestDataset.NewRoot());

        var result = await SqliteDataset.OpenExistingAsync(paths);

        result.Status.Should().Be(DatasetOpenStatus.Missing);
        result.Dataset.Should().BeNull();
        Directory.Exists(paths.ApplicationDirectory).Should().BeFalse("opening must not create the data folder or an empty database");
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task Opening_a_missing_database_in_an_existing_data_folder_leaves_the_folder_empty()
    {
        var paths = DataPaths.ForDirectory(TestDataset.NewRoot());
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            var result = await SqliteDataset.OpenExistingAsync(paths);

            result.Status.Should().Be(DatasetOpenStatus.Missing);
            Directory.EnumerateFileSystemEntries(paths.DataDirectory).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(paths.ApplicationDirectory, recursive: true);
        }
    }

    [Fact]
    [Trait("Req", "T-03.8")]
    public async Task A_newer_schema_is_refused_and_the_file_is_not_touched()
    {
        await using var db = await TestDataset.CreateAsync();
        await db.AddTaskAsync();
        await db.CloseAsync();
        db.Execute("PRAGMA user_version = 99;");
        var before = File.ReadAllBytes(db.Paths.DatabaseFile);

        var result = await SqliteDataset.OpenExistingAsync(db.Paths, db.Options);

        result.Status.Should().Be(DatasetOpenStatus.Failed);
        result.Error.Should().Be(new UnsupportedSchemaError(99, SqliteDataset.SupportedSchemaVersion));
        File.ReadAllBytes(db.Paths.DatabaseFile).Should().Equal(before);
        Directory.Exists(db.Paths.BackupsDirectory).Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "T-11.7")]
    public async Task A_corrupt_file_is_reported_and_left_untouched_without_creating_anything()
    {
        var paths = DataPaths.ForDirectory(TestDataset.NewRoot());
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            var garbage = new byte[8192];
            new Random(42).NextBytes(garbage);
            File.WriteAllBytes(paths.DatabaseFile, garbage);

            var result = await SqliteDataset.OpenExistingAsync(paths);

            result.Status.Should().Be(DatasetOpenStatus.Failed);
            result.Error.Should().Be(new StorageFailureError(StorageFailureKind.Corrupt));
            File.ReadAllBytes(paths.DatabaseFile).Should().Equal(garbage);
            Directory.EnumerateFileSystemEntries(paths.DataDirectory).Should().ContainSingle("no WAL, journal, or replacement database is created");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(paths.ApplicationDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_sqlite_file_that_is_not_a_max_planner_dataset_is_never_initialized()
    {
        var paths = DataPaths.ForDirectory(TestDataset.NewRoot());
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            using (var foreign = new SqliteConnection($"Data Source={paths.DatabaseFile};Pooling=False"))
            {
                foreign.Open();
                using var create = foreign.CreateCommand();
                create.CommandText = "CREATE TABLE other_app (x INTEGER);";
                create.ExecuteNonQuery();
            }

            var result = await SqliteDataset.OpenExistingAsync(paths);

            result.Error.Should().Be(new StorageFailureError(StorageFailureKind.Corrupt));
            using var check = new SqliteConnection($"Data Source={paths.DatabaseFile};Mode=ReadOnly;Pooling=False");
            check.Open();
            using var count = check.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            count.ExecuteScalar().Should().Be(1L, "no Max Planner tables were added");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(paths.ApplicationDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Creation_never_overwrites_an_existing_database()
    {
        await using var db = await TestDataset.CreateAsync();
        await db.AddTaskAsync();
        await db.CloseAsync();

        var result = await SqliteDataset.CreateNewAsync(db.Paths, db.Options);

        result.Status.Should().Be(DatasetOpenStatus.AlreadyExists);
        await db.ReopenAsync();
        (await db.SnapshotAsync()).Tasks.Should().ContainSingle();
    }

    [Fact]
    public async Task An_older_dataset_is_backed_up_and_then_migrated_with_a_history_entry()
    {
        await using var db = await TestDataset.CreateAsync();
        var task = await db.AddTaskAsync("Kept through the upgrade");
        await db.CloseAsync();
        db.Options = db.Options with { Migrations = [M0001InitialSchema.Migration, new SchemaMigration(2, "M0002_test", (c, t, _) => Run(c, t, "CREATE TABLE upgrade_probe (x INTEGER);"))] };

        var result = await SqliteDataset.OpenExistingAsync(db.Paths, db.Options);

        result.Status.Should().Be(DatasetOpenStatus.Opened);
        await result.Dataset!.DisposeAsync();
        result.Dataset.SchemaVersion.Should().Be(2);
        db.Scalar("PRAGMA user_version;").Should().Be(2);
        StorageJson.ReadHistory(Text(db, "SELECT migration_history_json FROM app_metadata;")).Select(h => h.Version).Should().Equal(1, 2);

        var backup = Path.Combine(result.SafetyBackupDirectory!, "data.db");
        Path.GetFileName(result.SafetyBackupDirectory).Should().EndWith("-migration-safety");
        using var copy = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
        copy.Open();
        Scalar(copy, "PRAGMA user_version;").Should().Be(1);
        Scalar(copy, "SELECT COUNT(*) FROM tasks;").Should().Be(1);
        Text(copy, "PRAGMA integrity_check;").Should().Be("ok");
        task.Value.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Req", "T-11.7")]
    public async Task A_failed_migration_rolls_back_keeps_the_safety_copy_and_returns_a_typed_error()
    {
        await using var db = await TestDataset.CreateAsync();
        await db.AddTaskAsync("Must survive");
        await db.CloseAsync();
        var original = db.Options;
        db.Options = original with
        {
            Migrations =
            [
                M0001InitialSchema.Migration,
                new SchemaMigration(2, "M0002_broken", (c, t, _) =>
                {
                    Run(c, t, "CREATE TABLE half_done (x INTEGER); DELETE FROM tasks;");
                    throw new InvalidOperationException("Simulated migration bug.");
                }),
            ],
        };

        var result = await SqliteDataset.OpenExistingAsync(db.Paths, db.Options);

        result.Status.Should().Be(DatasetOpenStatus.Failed);
        result.Error.Should().Be(new MigrationFailedError(1, 2));
        File.Exists(Path.Combine(result.SafetyBackupDirectory!, "data.db")).Should().BeTrue();

        db.Options = original;
        await db.ReopenAsync();
        db.Dataset.SchemaVersion.Should().Be(1);
        (await db.SnapshotAsync()).Tasks.Should().ContainSingle("the failed step rolled back");
        db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'half_done';").Should().Be(0);
    }

    private static void Run(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Text(TestDataset db, string sql)
    {
        using var connection = db.OpenRaw();
        return Text(connection, sql);
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
