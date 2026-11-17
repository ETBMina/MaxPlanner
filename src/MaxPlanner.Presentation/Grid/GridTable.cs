using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Grid;

/// <summary>The row list of the custom grid; its peer exposes the Grid and Table patterns (ADR 0004 row 6).</summary>
public sealed partial class GridTable : ListView
{
    internal IGridCellHost? Host { get; set; }

    protected override DependencyObject GetContainerForItemOverride() => new GridRowContainer();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is GridRowContainer;

    protected override AutomationPeer OnCreateAutomationPeer() => new GridTableAutomationPeer(this);
}
