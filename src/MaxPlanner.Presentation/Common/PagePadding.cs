using Microsoft.UI.Xaml;

namespace MaxPlanner.Presentation.Common;

/// <summary>
/// The page padding of design §2.3 that the page views share. It follows the window, not the page: only content
/// layout (columns, bucket widths, wrapping) follows the page width.
/// </summary>
internal static class PagePadding
{
    /// <summary>
    /// The top padding of a page header: L 24, M 16, S 12 epx. L, M and S are the window's size classes (design §2.2:
    /// the NavigationView's Left, LeftCompact and LeftMinimal), not the page's, so opening or closing the inline Left
    /// pane, which only narrows the page, never moves the header and the content below it vertically.
    /// </summary>
    public static double Top(double windowWidth) => windowWidth >= 1008 ? 24 : windowWidth >= 640 ? 16 : 12;

    /// <summary>
    /// The left and right padding of a page, header and content alike: L 32, M 24, S 16 epx, by the window's size
    /// class for the same reason as <see cref="Top"/>: the inline pane opening or closing never moves the page's edges.
    /// </summary>
    public static double Side(double windowWidth) => windowWidth >= 1008 ? 32 : windowWidth >= 640 ? 24 : 16;

    /// <summary>The width of <paramref name="page"/>'s window, or <paramref name="pageWidth"/> before it is in one.</summary>
    public static double WindowWidth(FrameworkElement page, double pageWidth) =>
        page.XamlRoot is { Size.Width: > 0 } root ? root.Size.Width : pageWidth;
}
