using System.Xml.Linq;
using AwesomeAssertions;

namespace MaxPlanner.Architecture.Tests;

// T-04 / T-11.8: the allowed project-reference matrix is checked from the .csproj files
// themselves, so it also covers the WinUI projects this net10.0 test cannot load.
public sealed class ProjectGraphTests
{
    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new()
    {
        ["MaxPlanner.Domain"] = [],
        ["MaxPlanner.Application"] = ["MaxPlanner.Domain"],
        ["MaxPlanner.Infrastructure"] = ["MaxPlanner.Application", "MaxPlanner.Domain"],
        ["MaxPlanner.Presentation"] = ["MaxPlanner.Application", "MaxPlanner.Domain"],
        ["MaxPlanner.Desktop"] = ["MaxPlanner.Presentation", "MaxPlanner.Application", "MaxPlanner.Infrastructure"],
    };

    [Theory]
    [Trait("Req", "T-04")]
    [MemberData(nameof(SourceProjects))]
    public void Project_references_follow_the_clean_architecture_matrix(string projectName)
    {
        var references = ReadProjectReferences(projectName);

        references.Should().BeSubsetOf(AllowedProjectReferences[projectName]);
    }

    [Fact]
    [Trait("Req", "T-04")]
    public void Domain_has_no_package_references()
    {
        ReadPackageReferences("MaxPlanner.Domain").Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "T-04")]
    public void Application_references_only_logging_abstractions()
    {
        ReadPackageReferences("MaxPlanner.Application")
            .Should().BeSubsetOf(["Microsoft.Extensions.Logging.Abstractions"]);
    }

    [Fact]
    [Trait("Req", "T-04")]
    public void Presentation_does_not_reference_storage_packages()
    {
        ReadPackageReferences("MaxPlanner.Presentation")
            .Should().NotContain(p => p.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal)
                                      || p.StartsWith("SQLitePCLRaw", StringComparison.Ordinal));
    }

    // ADR 0004 and ADR 0021: these packages are pinned only for the Phase 0 spikes.
    [Theory]
    [Trait("Req", "T-08")]
    [MemberData(nameof(SourceProjects))]
    public void Source_projects_do_not_reference_packages_rejected_in_phase_0(string projectName)
    {
        ReadPackageReferences(projectName)
            .Should().NotContain(p => p.StartsWith("CommunityToolkit.WinUI", StringComparison.Ordinal)
                                      || p == "WinUI.TableView"
                                      || p == "Microsoft.WindowsAppSDK");
    }

    public static TheoryData<string> SourceProjects() => [.. AllowedProjectReferences.Keys];

    private static string[] ReadProjectReferences(string projectName) =>
        LoadProject(projectName)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!))
            .ToArray();

    private static string[] ReadPackageReferences(string projectName) =>
        LoadProject(projectName)
            .Descendants("PackageReference")
            .Select(e => (string)e.Attribute("Include")!)
            .ToArray();

    private static XDocument LoadProject(string projectName) =>
        XDocument.Load(Path.Combine(RepositoryRoot.Value, "src", projectName, projectName + ".csproj"));

    private static readonly Lazy<string> RepositoryRoot = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MaxPlanner.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (MaxPlanner.slnx) not found.");
    });
}
