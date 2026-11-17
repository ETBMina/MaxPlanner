using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Startup;
using MaxPlanner.Infrastructure.Backup;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Startup;
using MaxPlanner.Infrastructure.Storage;
using MaxPlanner.Infrastructure.Tests.Persistence;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Tests.Startup;

/// <summary>
/// The Infrastructure adapters behind the Application startup ports, over real files in disposable temp folders
/// (T-09, ADR 0015). The protocol itself belongs to <see cref="RestoreService"/>; these pin the mapping.
/// </summary>
[Trait("Req", "T-09")]
public sealed class StartupAdapterTests
{
    private const string AppVersion = "9.9.9-test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task Storage_reports_missing_creates_once_refuses_to_overwrite_and_reopens_the_same_dataset()
    {
        var root = TestDataset.NewRoot();
        try
        {
            var storage = new SqliteDatasetStorage(DataPaths.ForDirectory(root), new SqliteDatasetOptions());

            (await storage.OpenExistingAsync()).Outcome.Should().Be(DatasetOpenOutcome.Missing);

            var created = await storage.CreateNewAsync();
            created.Outcome.Should().Be(DatasetOpenOutcome.Opened);
            var id = created.Dataset!.Id;
            created.Dataset.SchemaVersion.Should().Be(SqliteDataset.SupportedSchemaVersion);
            await created.Dataset.DisposeAsync();

            var again = await storage.CreateNewAsync();
            again.Outcome.Should().Be(DatasetOpenOutcome.AlreadyExists);
            again.Dataset.Should().BeNull();

            var opened = await storage.OpenExistingAsync();
            opened.Outcome.Should().Be(DatasetOpenOutcome.Opened);
            opened.Dataset!.Id.Should().Be(id);
            opened.Dataset.Should().BeOfType<OpenedSqliteDataset>();
            await opened.Dataset.DisposeAsync();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Storage_maps_a_foreign_file_to_a_failed_open_with_its_typed_error_and_leaves_it_in_place()
    {
        var root = TestDataset.NewRoot();
        try
        {
            var paths = DataPaths.ForDirectory(root);
            Directory.CreateDirectory(paths.DataDirectory);
            await File.WriteAllTextAsync(paths.DatabaseFile, "not a database", Ct);

            var result = await new SqliteDatasetStorage(paths, new SqliteDatasetOptions()).OpenExistingAsync();

            result.Outcome.Should().Be(DatasetOpenOutcome.Failed);
            result.Dataset.Should().BeNull();
            result.Error.Should().NotBeNull();
            (await File.ReadAllTextAsync(paths.DatabaseFile, Ct)).Should().Be("not a database");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task A_marker_that_cannot_be_read_is_seen_at_startup_and_left_unresolved()
    {
        await using var db = await TestDataset.CreateAsync();
        var storage = new SqliteDatasetStorage(db.Paths, db.Options);
        var restores = new FileRestoreOperations(db.Paths, AppVersion);
        storage.HasRestoreMarker().Should().BeFalse();

        await File.WriteAllTextAsync(db.Paths.RestoreMarkerFile, "{ broken", Ct);

        storage.HasRestoreMarker().Should().BeTrue();
        restores.RecoverInterrupted().Should().Be(InterruptedRestoreOutcome.Unresolved);
        File.Exists(db.Paths.RestoreMarkerFile).Should().BeTrue("nothing is guessed");
    }

    [Fact]
    public async Task A_manual_backup_restores_through_the_ports_after_the_dataset_is_closed()
    {
        await using var db = await TestDataset.CreateAsync();
        await db.AddTaskAsync("Before the backup");
        var backups = new SqliteDatasetBackups(db.Dataset, db.Clock, AppVersion);
        var restores = new FileRestoreOperations(db.Paths, AppVersion);

        var backup = backups.Create(BackupKind.Manual, db.Clock.GetUtcNow());
        backup.IsSuccess.Should().BeTrue();
        Path.GetFileName(backup.Value).Should().EndWith("-manual");
        backups.List().Should().ContainSingle(b => b.Path == backup.Value && b.IsManual && b.Kind == "manual" && b.ManifestState == "Valid");
        await db.AddTaskAsync("After the backup");

        var staged = restores.Validate(backup.Value, Ct);
        staged.IsValid.Should().BeTrue();
        staged.Checks.Select(c => c.Kind).Should().Contain(["Manifest", "Checksums", "SchemaVersion", "Integrity", "ForeignKeys", "Records"]);
        staged.BackupKind.Should().Be("manual");

        await db.CloseAsync();
        restores.Swap(staged).Should().BeTrue();
        File.Exists(db.Paths.RestoreMarkerFile).Should().BeFalse();

        await db.ReopenAsync();
        db.Scalar("SELECT COUNT(*) FROM tasks;").Should().Be(1);
    }

    [Fact]
    public async Task An_invalid_backup_is_reported_by_check_and_discarding_removes_its_staging()
    {
        await using var db = await TestDataset.CreateAsync();
        var restores = new FileRestoreOperations(db.Paths, AppVersion);
        var empty = Path.Combine(db.Root, "not-a-backup");
        Directory.CreateDirectory(empty);

        var staged = restores.Validate(empty, Ct);

        staged.IsValid.Should().BeFalse();
        staged.Checks.Should().ContainSingle().Which.Should().Be(new RestoreCheckResult("Manifest", false, null));
        restores.Discard(staged);
        Directory.Exists(((StagedSqliteRestore)staged).Validation.RestoreFolder).Should().BeFalse();
    }

    [Fact]
    public async Task A_raw_safety_copy_is_written_as_a_restore_safety_backup()
    {
        await using var db = await TestDataset.CreateAsync();
        await db.CloseAsync();
        var restores = new FileRestoreOperations(db.Paths, AppVersion);

        var safety = restores.CreateRawSafetyCopy(db.Clock.GetUtcNow());

        safety.Should().NotBeNull();
        Path.GetDirectoryName(safety).Should().Be(db.Paths.BackupsDirectory);
        Path.GetFileName(safety).Should().Be("20261022T090000Z-restore-safety");
        File.Exists(Path.Combine(safety!, BackupManifest.FileName)).Should().BeTrue();
    }

    [Fact]
    public async Task A_daily_backup_is_reported_through_the_port_and_cannot_be_deleted_as_manual()
    {
        await using var db = await TestDataset.CreateAsync();
        var backups = new SqliteDatasetBackups(db.Dataset, db.Clock, AppVersion);

        var report = await backups.RunDailyAsync(Ct);

        report.Should().Be(new DailyBackupReport(false, null));
        backups.List().Should().ContainSingle(b => b.Kind == "daily" && !b.IsManual);
        backups.DeleteManual(backups.List()[0].Path).Should().BeFalse("only manual backups can be deleted");
    }

    [Fact]
    public async Task A_failed_daily_backup_is_reported_with_its_failure_kind()
    {
        await using var db = await TestDataset.CreateAsync();
        var backups = new SqliteDatasetBackups(db.Dataset, db.Clock, AppVersion);

        // A file where the backups folder should be makes the final move fail.
        await File.WriteAllTextAsync(db.Paths.BackupsDirectory, "not a folder", Ct);

        var report = await backups.RunDailyAsync(Ct);

        report.Should().Be(new DailyBackupReport(true, StorageFailureKind.Io));
    }

    [Fact]
    public void Ports_refuse_a_staged_restore_they_did_not_produce()
    {
        var restores = new FileRestoreOperations(DataPaths.ForDirectory(TestDataset.NewRoot()), AppVersion);

        var swap = () => restores.Swap(new ForeignStagedRestore());

        swap.Should().Throw<ArgumentException>();
    }

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed class ForeignStagedRestore : IStagedRestore
    {
        public bool IsValid => true;

        public IReadOnlyList<RestoreCheckResult> Checks => [];

        public DateTimeOffset? BackupCreatedAtUtc => null;

        public string? BackupKind => null;
    }
}
