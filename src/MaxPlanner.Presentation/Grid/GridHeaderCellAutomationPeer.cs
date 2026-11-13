using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace MaxPlanner.Presentation.Grid;

internal sealed partial class GridHeaderCellAutomationPeer(GridHeaderCell owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
    public void Invoke() => owner.RequestSort();

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.HeaderItem;

    protected override string GetAutomationIdCore() => "Grid_Header_" + owner.Column;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);
}
