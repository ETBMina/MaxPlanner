using MaxPlanner.Presentation.Common.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Grid;

/// <summary>The resize grip of a header (pointer resize, design §4.3); reuses the Calendar's cursor host.</summary>
internal static class GridHeaderGrip
{
    public static CalendarResizeHandle Create(string headerName, Action<double> onDrag)
    {
        var handle = new CalendarResizeHandle { HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetAccessibilityView(handle.Thumb, AccessibilityView.Raw);
        ToolTipService.SetToolTip(handle, headerName);
        handle.Thumb.DragDelta += (_, e) => onDrag(e.HorizontalChange);
        return handle;
    }
}
