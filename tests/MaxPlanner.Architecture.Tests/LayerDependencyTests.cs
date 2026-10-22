using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using AwesomeAssertions;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MaxPlanner.Architecture.Tests;

// T-04: Domain and Application compile without UI, storage, reactive, or file-system types.
public sealed class LayerDependencyTests
{
    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(MaxPlanner.Domain.Identifiers.TaskId).Assembly,
            typeof(MaxPlanner.Application.Common.OperationId).Assembly,
            typeof(MaxPlanner.Infrastructure.Storage.DataPaths).Assembly)
        .Build();

    private static readonly string[] ForbiddenInCore =
    [
        "Microsoft.Data.Sqlite",
        "SQLitePCL",
        "Microsoft.UI",
        "Windows.",
        "WinRT",
        "DynamicData",
        "System.Reactive",
        "Serilog",
        "System.IO.File",
        "System.IO.Directory",
        "System.Collections.ObjectModel.ObservableCollection",
    ];

    [Theory]
    [Trait("Req", "T-04")]
    [InlineData("MaxPlanner.Domain")]
    [InlineData("MaxPlanner.Application")]
    public void Core_layers_do_not_depend_on_infrastructure_or_ui_types(string layerNamespace)
    {
        var violations = Architecture.Types
            .Where(t => t.FullName.StartsWith(layerNamespace + ".", StringComparison.Ordinal))
            .SelectMany(t => t.Dependencies.Select(d => (Type: t.FullName, Target: d.Target.FullName)))
            .Where(d => ForbiddenInCore.Any(f => d.Target.StartsWith(f, StringComparison.Ordinal)))
            .Select(d => $"{d.Type} -> {d.Target}")
            .ToArray();

        violations.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "T-04")]
    public void Domain_does_not_depend_on_outer_layers()
    {
        var rule = Types().That().ResideInNamespaceMatching(@"^MaxPlanner\.Domain(\..*)?$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^MaxPlanner\.(Application|Infrastructure|Presentation|Desktop)(\..*)?$"));

        Violations(rule).Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "T-04")]
    public void Application_does_not_depend_on_infrastructure_or_presentation()
    {
        var rule = Types().That().ResideInNamespaceMatching(@"^MaxPlanner\.Application(\..*)?$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^MaxPlanner\.(Infrastructure|Presentation|Desktop)(\..*)?$"));

        Violations(rule).Should().BeEmpty();
    }

    // ArchUnitNET core is used without its xUnit adapter (ADR 0016), so results are asserted directly.
    private static string[] Violations(ArchUnitNET.Fluent.Syntax.Elements.Types.TypesShouldConjunction rule) =>
        rule.Evaluate(Architecture).Where(r => !r.Passed).Select(r => r.Description).ToArray();
}
