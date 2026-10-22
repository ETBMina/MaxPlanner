using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Desktop.SmokeTests;

/// <summary>
/// Runs the real app (unpackaged, from a copied build output) against arranged data folders and checks the
/// outcome through its exit code (ADR 0003), its own log, and the files it leaves behind. UI Automation
/// evidence for the same states is recorded separately (docs/evidence/phase-1).
/// </summary>
[Trait("Category", "Smoke")]
[Trait("Req", "T-09")]
public sealed class StartupSmokeTests(PortableAppCopy app) : IClassFixture<PortableAppCopy>
{
    [Fact]
    [Trait("Req", "AC-01")]
    [Trait("Req", "T-06")]
    public void First_run_creates_the_dataset_preferences_and_one_daily_backup_and_closes_cleanly()
    {
        app.ResetData();

        var first = app.LaunchAndClose();

        first.Outcome.Should().Contain("Startup ready:");
        first.Log.Should().Contain("First run: no dataset");
        first.ExitCode.Should().Be(0);
        File.Exists(app.DatabaseFile).Should().BeTrue();
        File.Exists(Path.Combine(app.DataDirectory, "configuration.json")).Should().BeTrue();
        DailyBackups().Should().ContainSingle();
        File.Exists(Path.Combine(DailyBackups().Single(), "manifest.json")).Should().BeTrue();

        // T-06.6: a clean shutdown checkpoints and closes the WAL.
        File.Exists(app.DatabaseFile + "-wal").Should().BeFalse();
        File.Exists(app.DatabaseFile + "-shm").Should().BeFalse();

        var second = app.LaunchAndClose();

        second.Outcome.Should().Contain("Startup ready:");
        second.Log.Should().NotContain("First run");
        second.Log.Should().Contain("Daily backup skipped");
        second.ExitCode.Should().Be(0);
        DailyBackups().Should().ContainSingle("the second open on the same local day takes no new daily backup");
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public void A_damaged_database_shows_recovery_and_is_left_unchanged()
    {
        app.ResetData();
        Directory.CreateDirectory(app.DataDirectory);
        File.WriteAllBytes(app.DatabaseFile, [.. Enumerable.Range(0, 8192).Select(i => (byte)(i * 31))]);
        var before = Hash(app.DatabaseFile);

        var result = app.LaunchAndClose();

        result.Outcome.Should().Contain("Startup recovery: Corrupt");
        result.ExitCode.Should().Be(5);
        Hash(app.DatabaseFile).Should().Be(before);
        Directory.Exists(app.BackupsDirectory).Should().BeFalse("no backup is taken of a dataset that did not open");
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public void A_missing_database_in_a_used_folder_shows_recovery_instead_of_creating_one()
    {
        app.ResetData();
        var earlierBackup = Path.Combine(app.BackupsDirectory, "20261001T080000Z-manual");
        Directory.CreateDirectory(earlierBackup);
        File.WriteAllText(Path.Combine(earlierBackup, "manifest.json"), "{}");

        var result = app.LaunchAndClose();

        result.Outcome.Should().Contain("Startup recovery: DatabaseMissing");
        result.ExitCode.Should().Be(5);
        File.Exists(app.DatabaseFile).Should().BeFalse("an empty database is never substituted automatically");
        Directory.GetDirectories(app.BackupsDirectory).Should().Equal(earlierBackup);
    }

    [Fact]
    [Trait("Req", "T-03")]
    public void A_dataset_from_a_newer_version_is_refused_with_exit_code_4()
    {
        app.ResetData();
        Directory.CreateDirectory(app.DataDirectory);
        using (var connection = new SqliteConnection($"Data Source={app.DatabaseFile};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE future (id INTEGER PRIMARY KEY); PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var before = Hash(app.DatabaseFile);

        var result = app.LaunchAndClose();

        result.Outcome.Should().Contain("Startup recovery: UnsupportedSchema");
        result.ExitCode.Should().Be(4);
        Hash(app.DatabaseFile).Should().Be(before);
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public void An_interrupted_restore_marker_keeps_the_dataset_closed()
    {
        app.ResetData();
        app.LaunchAndClose().ExitCode.Should().Be(0);
        File.WriteAllText(Path.Combine(app.DataDirectory, "restore.pending.json"), """{"phase":"swapping"}""");
        var before = Hash(app.DatabaseFile);

        var result = app.LaunchAndClose();

        result.Outcome.Should().Contain("Startup recovery: InterruptedRestore");
        result.ExitCode.Should().Be(5);
        Hash(app.DatabaseFile).Should().Be(before);
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public void Unreadable_preferences_are_reset_without_blocking_the_dataset()
    {
        app.ResetData();
        app.LaunchAndClose().ExitCode.Should().Be(0);
        var configuration = Path.Combine(app.DataDirectory, "configuration.json");
        File.WriteAllText(configuration, "{ \"configurationVersion\": 1, \"theme\": ");

        var result = app.LaunchAndClose();

        result.Outcome.Should().Contain("Startup ready:");
        result.Log.Should().Contain("Preferences were reset to defaults");
        result.ExitCode.Should().Be(0);
        File.ReadAllText(configuration).Should().Contain("\"configurationVersion\": 1").And.Contain("\"theme\": \"System\"");
        File.Exists(Path.Combine(app.DataDirectory, "configuration.unreadable.json")).Should().BeTrue();
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private string[] DailyBackups() =>
        Directory.Exists(app.BackupsDirectory) ? Directory.GetDirectories(app.BackupsDirectory, "*-daily") : [];
}
