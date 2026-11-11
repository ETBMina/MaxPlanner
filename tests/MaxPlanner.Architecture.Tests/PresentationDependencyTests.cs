using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using AwesomeAssertions;

namespace MaxPlanner.Architecture.Tests;

// AGENTS.md layering: view models, the projection store, and the workspace context hold no WinUI types, so they
// run and are tested without XAML (ADR 0016). Code-behind, controls, and converters may use WinUI.
public sealed class PresentationDependencyTests
{
    private const string Presentation = "MaxPlanner.Presentation";
    private const string Xaml = "Microsoft.UI.Xaml";

    private static readonly string[] UiFreeNamespaces = [Presentation + ".Common.Store", Presentation + ".Workspace"];

    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(MaxPlanner.Presentation.Workspace.WorkspaceViewModel).Assembly)
        .Build();

    [Fact]
    [Trait("Req", "T-04")]
    public void View_models_store_and_workspace_types_do_not_depend_on_xaml()
    {
        var checkedTypes = Architecture.Types.Where(IsUiFree).ToArray();

        var violations = checkedTypes
            .SelectMany(t => t.Dependencies.Select(d => (Type: t.FullName, Target: d.Target.FullName)))
            .Where(d => d.Target == Xaml || d.Target.StartsWith(Xaml + ".", StringComparison.Ordinal))
            .Select(d => $"{d.Type} -> {d.Target}")
            .Distinct()
            .ToArray();

        checkedTypes.Should().Contain(t => t.Name == "WorkspaceViewModel", "the rule must see the Presentation types it guards");
        violations.Should().BeEmpty();
    }

    // Nested and compiler-generated types (lambdas, async state machines) count as part of the type that declares them.
    private static bool IsUiFree(IType type)
    {
        if (!type.FullName.StartsWith(Presentation + ".", StringComparison.Ordinal))
        {
            return false;
        }

        var outer = type.FullName.Split('+')[0];
        var name = outer[(outer.LastIndexOf('.') + 1)..].Split('`')[0];
        return name.EndsWith("ViewModel", StringComparison.Ordinal)
            || UiFreeNamespaces.Any(ns => type.Namespace.FullName == ns || type.Namespace.FullName.StartsWith(ns + ".", StringComparison.Ordinal));
    }
}
