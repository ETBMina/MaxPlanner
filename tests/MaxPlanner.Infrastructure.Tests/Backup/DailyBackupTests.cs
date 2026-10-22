using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Infrastructure.Backup;
using MaxPlanner.Infrastructure.Tests.Persistence;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Tests.Backup;

[Trait("Req", "T-09")]
public sealed class DailyBackupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_daily_backup_holds_a_consistent_database_configuration_and_checksummed_manifest()
    {
        await using var db = await TestDataset.CreateAsync();
        await db.AddTaskAsync("Card freeze");
        File.WriteAllText(db.Paths.ConfigurationFile, """{ "configurationVersion": 1 }""");

        var result = await Service(db).RunAsync(Ct);

        result.Outcome.Should().Be(DailyBackupOutcome.Created);
        var folder = result.BackupDirectory!;
        Path.GetFileName(folder).Should().Be("20261022T090000Z-daily");
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo(["data.db", "configuration.json", "manifest.json"]);

        var manifest = ReadManifest(folder);
        manifest.FormatVersion.Should().Be(1);
        manifest.AppVersion.Should().Be("9.9.9-test");
        manifest.SchemaVersion.Should().Be(db.Dataset.SchemaVersion);
        manifest.DatasetId.Should().Be(db.Dataset.Id.Value);
        manifest.Kind.Should().Be("daily");
        manifest.CreatedAtUtc.Should().Be(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
        manifest.Files.Select(f => f.Name).Should().Equal("data.db", "configuration.json");
        foreach (var file in manifest.Files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, file.Name));
            file.Length.Should().Be(bytes.Length);
            file.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        using var copy = new SqliteConnection($"Data Source={Path.Combine(folder, "data.db")};Mode=ReadOnly;Pooling=False");
        copy.Open();
        Scalar(copy, "PRAGMA integrity_check;").Should().Be("ok");
        Scalar(copy, "PRAGMA journal_mode;").Should().Be("delete", "a backup is one self-contained file");
        Scalar(copy, "SELECT COUNT(*) FROM tasks;").Should().Be("1", "the copy includes commits still in the WAL");
    }

    [Fact]
    public async Task A_second_open_on_the_same_local_day_does_nothing()
    {
        await using var db = await TestDataset.CreateAsync();
        (await Service(db).RunAsync(Ct)).Outcome.Should().Be(DailyBackupOutcome.Created);
        db.Clock.UtcNow = db.Clock.UtcNow.AddHours(5);

        var second = await Service(db).RunAsync(Ct);

        second.Outcome.Should().Be(DailyBackupOutcome.AlreadyDoneToday);
        Directory.GetDirectories(db.Paths.BackupsDirectory).Should().ContainSingle();
    }

    [Fact]
    public async Task The_day_is_the_users_local_day_not_the_utc_day()
    {
        await using var db = await TestDataset.CreateAsync();

        // 23:30 UTC on the 21st is already the 22nd in UTC+3, the same local day as "now" (09:00 UTC on the 22nd).
        db.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        CreateFakeBackup(db, "20261021T233000Z-daily");

        (await Service(db).RunAsync(Ct)).Outcome.Should().Be(DailyBackupOutcome.AlreadyDoneToday);

        db.Clock.Zone = TimeZoneInfo.Utc;
        (await Service(db).RunAsync(Ct)).Outcome.Should().Be(DailyBackupOutcome.Created);
    }

    [Fact]
    public async Task Retention_keeps_the_newest_seven_daily_backups_and_never_touches_manual_or_safety_backups()
    {
        await using var db = await TestDataset.CreateAsync();
        var olderDailies = Enumerable.Range(10, 8).Select(day => $"202610{day:00}T080000Z-daily").ToArray();
        foreach (var name in olderDailies)
        {
            CreateFakeBackup(db, name);
        }

        var kept = new[] { "20260101T080000Z-manual", "20260102T080000Z-migration-safety", "20260103T080000Z-import-safety", "notes" };
        foreach (var name in kept)
        {
            CreateFakeBackup(db, name);
        }

        var result = await Service(db).RunAsync(Ct);

        result.Outcome.Should().Be(DailyBackupOutcome.Created);
        var remaining = Directory.GetDirectories(db.Paths.BackupsDirectory).Select(Path.GetFileName).ToArray();
        remaining.Should().Contain(kept);
        remaining.Where(n => n!.EndsWith("-daily", StringComparison.Ordinal)).Should().BeEquivalentTo(
            [.. olderDailies[^6..], "20261022T090000Z-daily"]);
        result.PrunedDirectories!.Select(Path.GetFileName).Should().BeEquivalentTo(olderDailies[..2]);
    }

    [Fact]
    public async Task A_failure_is_reported_not_thrown_and_leaves_no_partial_backup()
    {
        await using var db = await TestDataset.CreateAsync();

        // A file where the backups folder should be makes the final move fail.
        File.WriteAllText(db.Paths.BackupsDirectory, "not a folder");

        var result = await Service(db).RunAsync(Ct);

        result.Outcome.Should().Be(DailyBackupOutcome.Failed);
        result.FailureKind.Should().Be(StorageFailureKind.Io);
        Directory.GetDirectories(db.Paths.TempDirectory, "backup-*").Should().BeEmpty();
    }

    private static DailyBackupService Service(TestDataset db) => new(db.Dataset, db.Clock, "9.9.9-test");

    private static void CreateFakeBackup(TestDataset db, string name)
    {
        var folder = Path.Combine(db.Paths.BackupsDirectory, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{}");
    }

    private static BackupManifest ReadManifest(string folder) =>
        JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(folder, "manifest.json")), BackupJsonContext.Default.BackupManifest)!;

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
