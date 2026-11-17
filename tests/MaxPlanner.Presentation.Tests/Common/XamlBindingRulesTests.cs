using System.Xml.Linq;
using AwesomeAssertions;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// XAML rules the compiler does not check. An x:Bind on a Grid attached property of an x:Load element is applied from a
/// deferred value that starts at 0 when the element loads: Grid.ColumnSpan 0 kept every Board card title from loading
/// (batch 3C), and a Grid.Row or Grid.Column 0 would misplace the element. Bind the layout on an always-loaded parent instead.
/// </summary>
public sealed class XamlBindingRulesTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void No_x_load_element_binds_a_grid_attached_property()
    {
        var offenders = XamlFiles().SelectMany(file => Offenders(XDocument.Load(file)).Select(o => $"{Path.GetFileName(file)}: {o}")).ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void The_rule_finds_the_board_card_title_shape_that_broke()
    {
        const string broken = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <TextBlock x:Name="TaskTitle" x:Load="{x:Bind IsTask, Mode=OneWay}" Grid.ColumnSpan="{x:Bind Span(HasIdRow), Mode=OneWay}" />
            </Grid>
            """;

        Offenders(XDocument.Parse(broken)).Should().Equal("TaskTitle Grid.ColumnSpan");
    }

    // "TaskTitle Grid.ColumnSpan" for each x:Bind on a Grid attached property of an element that has x:Load.
    private static IEnumerable<string> Offenders(XDocument document) =>
        document.Descendants()
            .Where(e => e.Attribute(Xaml + "Load") is not null)
            .SelectMany(e => e.Attributes()
                .Where(a => a.Name.Namespace == XNamespace.None
                    && a.Name.LocalName.StartsWith("Grid.", StringComparison.Ordinal)
                    && a.Value.StartsWith("{x:Bind", StringComparison.Ordinal))
                .Select(a => $"{(string?)e.Attribute(Xaml + "Name") ?? e.Name.LocalName} {a.Name.LocalName}"));

    internal static IEnumerable<string> XamlFiles()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MaxPlanner.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root (MaxPlanner.slnx) not found.");
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
