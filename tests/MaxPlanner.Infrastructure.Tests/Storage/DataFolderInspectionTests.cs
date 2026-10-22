using AwesomeAssertions;
using MaxPlanner.Infrastructure.Storage;

namespace MaxPlanner.Infrastructure.Tests.Storage;

[Trait("Req", "T-09")]
[Trait("Req", "AC-15")]
public sealed class DataFolderInspectionTests : IDisposable
{
    private readonly DataPaths _paths = DataPaths.ForDirectory(Path.Combine(Path.GetTempPath(), "MaxPlanner Tëst Inspect", Guid.NewGuid().ToString("N")));

    [Fact]
    public void A_folder_holding_only_what_startup_itself_creates_is_a_first_run()
    {
        // The writability probe, the lock, and logging create these before the inspection matters.
        Directory.CreateDirectory(_paths.TempDirectory);
        Directory.CreateDirectory(_paths.LogsDirectory);
        Directory.CreateDirectory(_paths.BackupsDirectory);
        File.WriteAllText(_paths.LockFile, string.Empty);

        DataFolderInspection.Inspect(_paths).Should().Be(new DataFolderInspection(HasRestoreMarker: false, HasPriorUse: false));
    }

    [Fact]
    public void A_missing_folder_is_a_first_run()
    {
        DataFolderInspection.Inspect(_paths).HasPriorUse.Should().BeFalse();
    }

    [Theory]
    [InlineData("configuration.json")]
    [InlineData("configuration.last-good.json")]
    [InlineData("data.db-wal")]
    [InlineData("data.db-shm")]
    [InlineData("data.db-journal")]
    [InlineData(@"backups\20261001T080000Z-manual\manifest.json")]
    public void Any_trace_of_an_earlier_dataset_counts_as_prior_use(string relativePath)
    {
        var file = Path.Combine(_paths.DataDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}");

        DataFolderInspection.Inspect(_paths).HasPriorUse.Should().BeTrue();
    }

    [Fact]
    public void A_restore_marker_is_reported_and_counts_as_prior_use()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        File.WriteAllText(_paths.RestoreMarkerFile, "{}");

        DataFolderInspection.Inspect(_paths).Should().Be(new DataFolderInspection(HasRestoreMarker: true, HasPriorUse: true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.ApplicationDirectory))
        {
            Directory.Delete(_paths.ApplicationDirectory, recursive: true);
        }
    }
}
