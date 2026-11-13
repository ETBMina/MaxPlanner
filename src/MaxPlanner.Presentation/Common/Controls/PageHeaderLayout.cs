using Microsoft.UI.Xaml;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The layout rules of <see cref="PageHeader"/> (design §2.3) as pure functions. Spacing that has nothing to separate is
/// 0, so an absent subtitle, action group or row never adds a gap.
/// </summary>
public static class PageHeaderLayout
{
    /// <summary>The gap between the title and the subtitle.</summary>
    public const double SubtitleSpacing = 2;

    /// <summary>The gap between the title block and the rows below it, and between those rows.</summary>
    public const double RowSpacing = 8;

    /// <summary>The gap between the title block and the actions beside it.</summary>
    public const double ActionsSpacing = 12;

    /// <summary>
    /// Whether the header shows a subtitle line: a page asks for one, or names its resource. A bound subtitle cannot
    /// decide it by its text, because x:Bind passes a null text as an empty one and an empty line still has a height.
    /// </summary>
    public static bool ShowsSubtitle(bool hasSubtitle, string? subtitleUid) => hasSubtitle || !string.IsNullOrEmpty(subtitleUid);

    /// <summary>The subtitle text: the resource of <paramref name="subtitleUid"/> when set, otherwise the bound text.</summary>
    public static string SubtitleText(string? subtitleUid, string? subtitle, Func<string, string> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        return string.IsNullOrEmpty(subtitleUid) ? subtitle ?? string.Empty : lookup(subtitleUid);
    }

    public static double TitleSpacing(bool showsSubtitle) => showsSubtitle ? SubtitleSpacing : 0;

    public static double BodySpacing(bool hasBody) => hasBody ? RowSpacing : 0;

    public static double ColumnSpacing(bool hasActions) => hasActions ? ActionsSpacing : 0;

    /// <summary>
    /// The header padding (design §2.3, T-08.1): the window's side and top insets (<see cref="PagePadding"/>), and a
    /// bottom of 12 epx from the M page width, 8 below it.
    /// </summary>
    public static Thickness Padding(double pageWidth, double windowWidth)
    {
        var side = PagePadding.Side(windowWidth);
        return new Thickness(side, PagePadding.Top(windowWidth), side, pageWidth >= Breakpoints.Medium ? 12 : 8);
    }
}
