using System.Xml.Linq;
using AwesomeAssertions;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// P-01: the Board bucket lists and the Grid row list keep a small realization cache. The default ItemsStackPanel
/// (CacheLength 4) realized about five viewports of cards in every bucket and of rows in the Grid, which pushed the idle
/// working set over budget on the 10k-task fixture.
/// </summary>
public sealed class XamlListCacheLengthTests
{
    [Theory]
    [InlineData("BoardPageView.xaml", "BoardCardList", "1")]
    [InlineData("GridPageView.xaml", "GridTable", "2")]
    public void List_uses_an_items_stack_panel_with_a_small_cache(string file, string list, string cacheLength)
    {
        var path = XamlBindingRulesTests.XamlFiles().Single(f => Path.GetFileName(f) == file);

        var panels = XDocument.Load(path).Descendants()
            .Where(e => e.Name.LocalName == list)
            .Select(e => e.Elements().SingleOrDefault(p => p.Name.LocalName == $"{list}.ItemsPanel")
                ?.Descendants().SingleOrDefault(p => p.Name.LocalName == "ItemsStackPanel"))
            .ToList();

        panels.Should().ContainSingle();
        panels[0].Should().NotBeNull($"{list} sets its ItemsPanel to an ItemsStackPanel");
        ((string?)panels[0]!.Attribute("CacheLength")).Should().Be(cacheLength);
    }
}
