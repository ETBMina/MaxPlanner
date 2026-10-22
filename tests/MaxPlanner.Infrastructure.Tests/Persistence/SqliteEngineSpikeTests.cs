using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

// Phase 0 spike 2 (ADR 0009): proves the SQLite behaviors the persistence design depends on,
// using real files. Timings are written to the test output for docs/evidence.
public sealed class SqliteEngineSpikeTests(ITestOutputHelper output)
{
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;
    private const int SqliteConstraintForeignKey = 787;

    [Fact]
    [Trait("Req", "T-06")]
    public void Writer_uses_wal_full_sync_and_enforced_foreign_keys()
    {
        using var db = new TempDatabase();
        using var writer = db.Factory.OpenWriter();

        SpikeSchema.Text(writer, "PRAGMA journal_mode;").Should().Be("wal");
        SpikeSchema.Scalar(writer, "PRAGMA synchronous;").Should().Be(2, "2 = FULL");
        SpikeSchema.Scalar(writer, "PRAGMA foreign_keys;").Should().Be(1);
    }

    [Fact]
    [Trait("Req", "T-05")]
    public void Cr_number_uniqueness_is_enforced_on_the_normalized_value()
    {
        using var db = new TempDatabase();
        using var writer = db.Factory.OpenWriter();
        SpikeSchema.Create(writer);
        InsertCr(writer, Guid.NewGuid(), "CR-105");

        var duplicate = () => InsertCr(writer, Guid.NewGuid(), "  cr-105 ");

        duplicate.Should().Throw<SqliteException>().Which.SqliteExtendedErrorCode.Should().Be(SqliteConstraintUnique);
    }

    [Fact]
    [Trait("Req", "T-05")]
    [Trait("Req", "B-03.4")]
    public void A_cr_links_many_tasks_but_each_task_links_at_most_once_and_never_to_a_missing_cr()
    {
        using var db = new TempDatabase();
        using var writer = db.Factory.OpenWriter();
        SpikeSchema.Create(writer);
        var cr = Guid.NewGuid();
        InsertCr(writer, cr, "CR-1");
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();
        SpikeSchema.CommitTask(writer, taskA);
        SpikeSchema.CommitTask(writer, taskB);

        Link(writer, taskA, cr);
        Link(writer, taskB, cr);
        var secondLink = () => Link(writer, taskA, cr);
        var dangling = () => Link(writer, Guid.NewGuid(), Guid.NewGuid());

        SpikeSchema.Scalar(writer, "SELECT COUNT(*) FROM cr_task_links;").Should().Be(2);
        secondLink.Should().Throw<SqliteException>().Which.SqliteExtendedErrorCode.Should().Be(SqliteConstraintPrimaryKey);
        dangling.Should().Throw<SqliteException>().Which.SqliteExtendedErrorCode.Should().Be(SqliteConstraintForeignKey);
    }

    [Fact]
    [Trait("Req", "T-06")]
    public void A_failed_statement_inside_a_transaction_rolls_back_the_whole_logical_operation()
    {
        using var db = new TempDatabase();
        using var writer = db.Factory.OpenWriter();
        SpikeSchema.Create(writer);
        InsertCr(writer, Guid.NewGuid(), "CR-1");

        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            InsertCr(writer, Guid.NewGuid(), "CR-2", transaction);
            var failing = () => InsertCr(writer, Guid.NewGuid(), "cr-1", transaction);
            failing.Should().Throw<SqliteException>();
            transaction.Rollback();
        }

        SpikeSchema.Scalar(writer, "SELECT COUNT(*) FROM change_requests;").Should().Be(1);
    }

    [Fact]
    [Trait("Req", "T-07")]
    public void A_reader_transaction_sees_one_snapshot_including_its_commit_sequence_while_the_writer_continues()
    {
        using var db = new TempDatabase();
        using var writer = db.Factory.OpenWriter();
        SpikeSchema.Create(writer);
        SpikeSchema.CommitTask(writer, Guid.NewGuid());

        using var reader = db.Factory.OpenReader();
        using var snapshot = reader.BeginTransaction(deferred: true);
        var sequenceBefore = SpikeSchema.Scalar(reader, "SELECT commit_sequence FROM app_metadata;", snapshot);
        var rowsBefore = SpikeSchema.Scalar(reader, "SELECT COUNT(*) FROM tasks;", snapshot);

        for (var i = 0; i < 5; i++)
        {
            SpikeSchema.CommitTask(writer, Guid.NewGuid());
        }

        SpikeSchema.Scalar(reader, "SELECT COUNT(*) FROM tasks;", snapshot).Should().Be(rowsBefore);
        SpikeSchema.Scalar(reader, "SELECT commit_sequence FROM app_metadata;", snapshot).Should().Be(sequenceBefore);
        sequenceBefore.Should().Be(rowsBefore);
        SpikeSchema.Scalar(writer, "SELECT commit_sequence FROM app_metadata;").Should().Be(6);
    }

    [Fact]
    [Trait("Req", "T-09")]
    public async Task Online_backup_during_continuous_writes_produces_a_consistent_valid_copy()
    {
        using var db = new TempDatabase();
        using (var setup = db.Factory.OpenWriter())
        {
            SpikeSchema.Create(setup);
        }

        using var stop = new CancellationTokenSource();
        var writerTask = Task.Run(() =>
        {
            using var writer = db.Factory.OpenWriter();
            var commits = 0;
            while (!stop.IsCancellationRequested)
            {
                SpikeSchema.CommitTask(writer, Guid.NewGuid());
                commits++;
            }

            return commits;
        });

        await Task.Delay(200, TestContext.Current.CancellationToken);
        var backupFile = Path.Combine(db.Directory, "backup.db");
        using (var source = db.Factory.OpenReader())
        using (var destination = new SqliteConnection($"Data Source={backupFile};Pooling=False"))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }

        await Task.Delay(100, TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        var totalCommits = await writerTask;

        using var copy = new SqliteConnection($"Data Source={backupFile};Mode=ReadOnly;Pooling=False");
        copy.Open();
        SpikeSchema.Text(copy, "PRAGMA integrity_check;").Should().Be("ok");
        var copiedRows = SpikeSchema.Scalar(copy, "SELECT COUNT(*) FROM tasks;");
        SpikeSchema.Scalar(copy, "SELECT commit_sequence FROM app_metadata;").Should().Be(copiedRows, "the copy is one committed snapshot");
        copiedRows.Should().BeGreaterThan(0).And.BeLessThan(totalCommits);
        output.WriteLine($"Backup captured {copiedRows} of {totalCommits} commits made during the run.");
    }

    [Fact]
    [Trait("Req", "T-11")]
    public void Single_row_commits_under_full_sync_stay_well_inside_the_save_budget()
    {
        using var db = new TempDatabase();
        using var writer = db.Factory.OpenWriter();
        SpikeSchema.Create(writer);
        for (var i = 0; i < 20; i++)
        {
            SpikeSchema.CommitTask(writer, Guid.NewGuid());
        }

        var timings = new List<double>(300);
        for (var i = 0; i < 300; i++)
        {
            var started = Stopwatch.GetTimestamp();
            SpikeSchema.CommitTask(writer, Guid.NewGuid());
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        timings.Sort();
        var p50 = timings[timings.Count / 2];
        var p95 = timings[(int)(timings.Count * 0.95)];
        output.WriteLine($"synchronous=FULL single-row commit over 300 runs: p50 {p50:F2} ms, p95 {p95:F2} ms, max {timings[^1]:F2} ms.");

        // T-11 allows 200 ms from save to visible projections; the commit must be a small fraction of it.
        p95.Should().BeLessThan(50);
    }

    [Fact]
    [Trait("Req", "T-11.7")]
    public void Killing_the_writer_process_mid_stream_leaves_a_valid_database_with_every_acknowledged_commit()
    {
        using var db = new TempDatabase();
        using (var setup = db.Factory.OpenWriter())
        {
            SpikeSchema.Create(setup);
        }

        var crashWriter = Path.Combine(AppContext.BaseDirectory, "MaxPlanner.Infrastructure.Tests.CrashWriter.dll");
        File.Exists(crashWriter).Should().BeTrue("the crash-writer helper is built with the tests");
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(crashWriter);
        startInfo.ArgumentList.Add(db.Factory.DatabaseFile);

        long lastAcknowledged = 0;
        using (var child = Process.Start(startInfo)!)
        {
            while (lastAcknowledged < 250 && child.StandardOutput.ReadLine() is { } line)
            {
                lastAcknowledged = long.Parse(line, System.Globalization.CultureInfo.InvariantCulture);
            }

            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }

        SqliteConnection.ClearAllPools();
        using var reopened = db.Factory.OpenWriter();
        SpikeSchema.Text(reopened, "PRAGMA integrity_check;").Should().Be("ok");
        var sequence = SpikeSchema.Scalar(reopened, "SELECT commit_sequence FROM app_metadata;");
        var rows = SpikeSchema.Scalar(reopened, "SELECT COUNT(*) FROM tasks;");
        rows.Should().Be(sequence, "no half-applied transaction survives the kill");
        sequence.Should().BeGreaterThanOrEqualTo(lastAcknowledged, "every commit reported as durable must survive");
        output.WriteLine($"Killed after {lastAcknowledged} acknowledged commits; reopened with {sequence} committed rows.");
    }

    private static void InsertCr(SqliteConnection writer, Guid id, string number, SqliteTransaction? transaction = null)
    {
        using var command = writer.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO change_requests (id, number, number_norm, title) VALUES ($id, $number, $norm, 'CR');";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$norm", number.Trim().ToUpperInvariant());
        command.ExecuteNonQuery();
    }

    private static void Link(SqliteConnection writer, Guid taskId, Guid crId)
    {
        using var command = writer.CreateCommand();
        command.CommandText = "INSERT INTO cr_task_links (task_id, cr_id) VALUES ($task, $cr);";
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$cr", crId);
        command.ExecuteNonQuery();
    }
}
