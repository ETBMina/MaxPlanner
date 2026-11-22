using AwesomeAssertions;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Common;

public sealed class AppVersionTextTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+beb1aea0c2f1", "0.1.0")]
    [InlineData("1.2.0-beta.1+beb1aea", "1.2.0-beta.1")]
    [InlineData(" 1.0.0 ", "1.0.0")]
    public void The_splash_shows_the_version_without_build_metadata(string informationalVersion, string expected) =>
        AppVersionText.ToDisplay(informationalVersion).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+beb1aea")]
    public void A_missing_version_shows_nothing(string? informationalVersion) =>
        AppVersionText.ToDisplay(informationalVersion).Should().BeEmpty();

    [Fact]
    public void The_version_line_comes_from_the_resources()
    {
        var strings = new FakeStrings().With("Shell_StartupVersion", "Version {0}");

        AppVersionText.ToVersionLine(strings, "0.1.0+beb1aea").Should().Be("Version 0.1.0");
    }

    [Fact]
    public void No_version_line_shows_without_a_version()
    {
        var strings = new FakeStrings().With("Shell_StartupVersion", "Version {0}");

        AppVersionText.ToVersionLine(strings, null).Should().BeEmpty();
    }
}
