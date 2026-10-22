using AwesomeAssertions;
using MaxPlanner.Infrastructure.Storage;

namespace MaxPlanner.Infrastructure.Tests.Storage;

public sealed class DataDirectoryProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MaxPlanner Tëst Probe", Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Req", "T-03.6")]
    public void A_writable_application_folder_passes_and_leaves_no_probe_file_behind()
    {
        var paths = DataPaths.ForDirectory(_root);

        DataDirectoryProbe.Probe(paths).Should().Be(DataDirectoryProbeResult.Writable);
        Directory.EnumerateFiles(paths.TempDirectory).Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "T-03.6")]
    public void A_data_location_that_cannot_hold_files_is_reported_instead_of_falling_back_elsewhere()
    {
        // A file where the data folder should be makes the location unusable without needing ACL changes.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "data"), "not a folder");
        var paths = DataPaths.ForDirectory(_root);

        DataDirectoryProbe.Probe(paths).Should().Be(DataDirectoryProbeResult.NotWritable);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
