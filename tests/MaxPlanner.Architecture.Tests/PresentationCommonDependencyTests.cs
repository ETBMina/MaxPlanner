using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using AwesomeAssertions;

namespace MaxPlanner.Architecture.Tests;

// Common holds what the features share, so it must not reach sideways into a feature. Workspace and Shell compose the
// features and are not features themselves, so Common may still use them.
public sealed class PresentationCommonDependencyTests
{
    private const string Presentation = "MaxPlanner.Presentation";
    private const string Common = Presentation + ".Common";

    private static readonly string[] FeatureNamespaces =
    [
        .. new[]
        {
            "Board", "Grid", "Calendar", "Checklists", "Dashboard", "Settings", "Tasks", "ChangeRequests", "SubtaskCards",
            "Reviews",
        }.Select(feature => Presentation + "." + feature),
    ];

    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(MaxPlanner.Presentation.Workspace.WorkspaceViewModel).Assembly)
        .Build();

    [Fact]
    public void Common_types_do_not_depend_on_feature_types()
    {
        var commonTypes = Architecture.Types.Where(t => IsIn(t.Namespace.FullName, Common)).ToArray();

        var violations = commonTypes
            .SelectMany(t => t.Dependencies.Select(d => (Type: t.FullName, Target: d.Target)))
            .Where(d => FeatureNamespaces.Any(ns => IsIn(d.Target.Namespace.FullName, ns)))
            .Select(d => $"{d.Type} -> {d.Target.FullName}")
            .Distinct()
            .ToArray();

        commonTypes.Should().Contain(t => t.Name == "TokenPicker", "the rule must see the Common types it guards");
        violations.Should().BeEmpty();
    }

    private static bool IsIn(string ns, string root) =>
        ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal);
}
