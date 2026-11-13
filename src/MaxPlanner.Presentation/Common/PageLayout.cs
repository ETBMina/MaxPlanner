using Microsoft.UI.Xaml;

namespace MaxPlanner.Presentation.Common;

/// <summary>Breakpoint plumbing that the page views share (design §2.2).</summary>
internal static class PageLayout
{
    /// <summary>
    /// Calls <paramref name="apply"/> with <paramref name="page"/>'s new width whenever its size changes. A collapsed
    /// (hidden) page reports width 0 and has nothing to adapt to, so that change is ignored; the page applies its real
    /// width when it is shown again.
    /// </summary>
    public static void OnWidthChanged(FrameworkElement page, Action<double> apply)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(apply);
        page.SizeChanged += (_, e) =>
        {
            if (IsLayoutWidth(e.NewSize.Width))
            {
                apply(e.NewSize.Width);
            }
        };
    }

    /// <summary>Whether a page width is one to lay out for: a hidden page's width of 0 is not.</summary>
    public static bool IsLayoutWidth(double width) => width > 0;
}
