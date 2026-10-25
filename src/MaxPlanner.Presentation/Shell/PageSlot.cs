using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// The shell's holder of one retained page view in the page host (design §2.2). The shell shows and hides the slot,
/// never the view, so a page that was preloaded stays in place and keeps its realized tree when it is first shown.
/// </summary>
/// <remarks>
/// T-11/AC-17: a collapsed element is not measured, so a preload that only added the view collapsed would leave its
/// templates, rows and cards to the first visit. While <see cref="IsPreloading"/> the slot is laid out and rendered
/// fully transparent behind the current page: the view is disabled, so it cannot take focus and its keyboard
/// accelerators do not run; it is not hit-test visible; and the slot's peer reports no children, so UI Automation
/// does not see it (AccessibilityView="Raw" would hide only the view itself, not its descendants).
/// </remarks>
public sealed partial class PageSlot : WinGrid
{
    internal PageSlot(UIElement page)
    {
        Page = page;
        Visibility = Visibility.Collapsed;
        Children.Add(page);
    }

    public UIElement Page { get; }

    /// <summary>The slot is being laid out invisibly for a preload and is not the current page.</summary>
    public bool IsPreloading { get; private set; }

    /// <summary>Lays the page out on the next frame without showing it or exposing it to input and UI Automation.</summary>
    internal void BeginPreload()
    {
        IsPreloading = true;
        if (Page is Control control)
        {
            control.IsEnabled = false;
        }

        IsHitTestVisible = false;
        Opacity = 0;
        Visibility = Visibility.Visible;
    }

    /// <summary>Collapses the preloaded page and gives it back its input and UI Automation; no-op when not preloading.</summary>
    internal void EndPreload()
    {
        if (!IsPreloading)
        {
            return;
        }

        IsPreloading = false;
        Visibility = Visibility.Collapsed;
        Opacity = 1;
        IsHitTestVisible = true;
        if (Page is Control control)
        {
            control.IsEnabled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PageSlotAutomationPeer(this);
}

/// <summary>Like a plain panel the slot is not a control or content element; while preloading it hides its page.</summary>
internal sealed partial class PageSlotAutomationPeer(PageSlot owner) : FrameworkElementAutomationPeer(owner)
{
    protected override IList<AutomationPeer> GetChildrenCore() =>
        ((PageSlot)Owner).IsPreloading ? [] : base.GetChildrenCore();

    protected override bool IsControlElementCore() => false;

    protected override bool IsContentElementCore() => false;
}
