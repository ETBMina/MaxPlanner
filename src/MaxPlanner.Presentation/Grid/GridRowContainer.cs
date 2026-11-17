using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Grid;

/// <summary>A Grid row container whose peer is a UIA DataItem (a table row) instead of a list item.</summary>
public sealed partial class GridRowContainer : ListViewItem
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GridRowAutomationPeer(this);
}
