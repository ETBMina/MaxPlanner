using AwesomeAssertions;
using MaxPlanner.Application.PullRequests;

namespace MaxPlanner.Application.Tests.PullRequests;

/// <summary>ADR 0031: which collection URLs go to the Debug-only simulator and show the "Simulator" label.</summary>
[Trait("Req", "B-18")]
public sealed class AzureDevOpsSimulatorRuleTests
{
    [Theory]
    [InlineData("https://simulator/coll", true)]
    [InlineData("https://SIMULATOR/coll/", true)]
    [InlineData("  https://simulator  ", true)]
    [InlineData("https://simulator.example.test/coll", false)]
    [InlineData("https://ado.example.test/simulator", false)]
    [InlineData("http://simulator/coll", false)]
    [InlineData("https://simulator/coll/_git/repo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Simulator_url_is_a_usable_collection_url_on_the_simulator_host(string? url, bool expected)
    {
        AzureDevOpsSimulator.IsSimulatorUrl(url).Should().Be(expected);
        AzureDevOpsSimulator.IsInUse(url).Should().Be(expected && AzureDevOpsSimulator.IsBuiltIn);
    }

    [Fact]
    public void Simulator_url_passes_the_collection_url_rules()
    {
        CollectionUrls.Check("https://simulator/coll").Should().Be(CollectionUrlProblem.None);
    }

    [Fact]
    public void Simulator_is_built_in_only_in_debug()
    {
#if DEBUG
        AzureDevOpsSimulator.IsBuiltIn.Should().BeTrue();
#else
        AzureDevOpsSimulator.IsBuiltIn.Should().BeFalse();
#endif
    }

    [Fact]
    public void Simulator_host_is_matched_on_the_parsed_address()
    {
        AzureDevOpsSimulator.IsSimulatorHost(new Uri("https://simulator/coll")).Should().BeTrue();
        AzureDevOpsSimulator.IsSimulatorHost(new Uri("https://ado.example.test/coll")).Should().BeFalse();
        AzureDevOpsSimulator.IsSimulatorHost(null).Should().BeFalse();
    }
}
