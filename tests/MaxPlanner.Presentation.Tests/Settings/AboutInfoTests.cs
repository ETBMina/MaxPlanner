using AwesomeAssertions;
using MaxPlanner.Presentation.Settings;

namespace MaxPlanner.Presentation.Tests.Settings;

public sealed class AboutInfoTests
{
    [Fact]
    public void The_GitHub_link_shows_the_repository_address_without_the_scheme()
    {
        AboutInfo.GitHubUri.Should().Be(new Uri("https://github.com/ETBMina/MaxPlanner"));
        AboutInfo.GitHubText.Should().Be("github.com/ETBMina/MaxPlanner");
    }

    [Theory]
    [InlineData("https://github.com/ETBMina/MaxPlanner/", "github.com/ETBMina/MaxPlanner")]
    [InlineData("https://example.com/", "example.com")]
    public void Link_text_drops_a_trailing_slash(string address, string expected) =>
        AboutInfo.ToLinkText(new Uri(address)).Should().Be(expected);

    [Fact]
    public void The_license_is_PolyForm_Internal_Use_and_its_files_are_resolved_beside_the_app()
    {
        AboutInfo.LicenseName.Should().Be("PolyForm Internal Use 1.0.0");
        AboutInfo.LicensePath.Should().Be(Path.Combine(AppContext.BaseDirectory, "LICENSE"));
        AboutInfo.NoticesPath.Should().Be(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));
        AboutInfo.FilePath(@"C:\Apps\MaxPlanner", "LICENSE").Should().Be(Path.Combine(@"C:\Apps\MaxPlanner", "LICENSE"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_null_or_empty_path_offers_no_link(string? path) => AboutInfo.CanOpenFile(path).Should().BeFalse();

    [Fact]
    public void A_link_is_offered_only_for_a_file_that_exists()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MaxPlannerAbout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var present = AboutInfo.FilePath(folder, "LICENSE");
            File.WriteAllText(present, "license");

            AboutInfo.CanOpenFile(present).Should().BeTrue();
            AboutInfo.CanOpenFile(AboutInfo.FilePath(folder, "THIRD-PARTY-NOTICES.txt")).Should().BeFalse();
            AboutInfo.CanOpenFile(folder).Should().BeFalse("a folder is not a document");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
