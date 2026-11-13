using Microsoft.UI.Xaml.Automation.Peers;

namespace MaxPlanner.Presentation.Grid;

/// <summary>A row's peer: a DataItem named by the row summary, with AutomationId <c>Grid_Row_{Id:N}</c> (design §8).</summary>
internal sealed partial class GridRowAutomationPeer(GridRowContainer owner) : ListViewItemAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

    protected override string GetAutomationIdCore() =>
        owner.Content is GridRowViewModel row ? $"Grid_Row_{row.Key:N}" : base.GetAutomationIdCore();

    protected override string GetNameCore() => owner.Content is GridRowViewModel row ? row.AccessibleName : base.GetNameCore();
}
