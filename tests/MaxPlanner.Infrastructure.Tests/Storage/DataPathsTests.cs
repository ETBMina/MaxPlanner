using AwesomeAssertions;
using MaxPlanner.Infrastructure.Storage;

namespace MaxPlanner.Infrastructure.Tests.Storage;

public sealed class DataPathsTests
{
    [Fact]
    [Trait("Req", "T-03")]
    public void All_app_owned_files_live_under_the_data_folder_beside_the_application()
    {
        var root = Path.Combine(Path.GetTempPath(), "Max Planner Tëst", Guid.NewGuid().ToString("N"));

        var paths = DataPaths.ForDirectory(root);

        paths.DataDirectory.Should().Be(Path.Combine(root, "data"));
        new[] { paths.DatabaseFile, paths.ConfigurationFile, paths.BackupsDirectory, paths.LogsDirectory, paths.TempDirectory, paths.LockFile }
            .Should().AllSatisfy(p => p.Should().StartWith(paths.DataDirectory + Path.DirectorySeparatorChar));
    }

    [Fact]
    [Trait("Req", "T-03")]
    public void Relative_roots_resolve_to_absolute_paths_so_the_working_directory_never_matters()
    {
        var paths = DataPaths.ForDirectory("relative-app-folder");

        Path.IsPathFullyQualified(paths.ApplicationDirectory).Should().BeTrue();
    }

    [Fact]
    [Trait("Req", "T-03")]
    public void Application_paths_come_from_the_executable_folder()
    {
        DataPaths.ForApplication().ApplicationDirectory
            .Should().Be(Path.GetFullPath(AppContext.BaseDirectory));
    }
}
