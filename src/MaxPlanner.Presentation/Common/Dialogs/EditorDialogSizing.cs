namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// The one width rule for the record editors (design §5.0): the width depends only on the window, never on the
/// content, so every editor is the same size in the same window. L (window ≥ 1008 epx) gets 840 epx, M gets
/// 640 epx, and the dialog always leaves <see cref="WindowMargin"/> on each side so it never overflows.
/// </summary>
public static class EditorDialogSizing
{
    public const double LargeWidth = 840;
    public const double MediumWidth = 640;
    public const double MinimumWidth = 320;
    public const double WindowMargin = 24;
    public const double LargeBreakpoint = 1008;
    public const double SmallBreakpoint = 640;
    public const double ShortWindowHeight = 600;

    /// <summary>The ContentDialog's horizontal padding (24 epx on each side in the default template).</summary>
    public const double DialogHorizontalPadding = 48;

    /// <summary>The dialog's outer width for a window of <paramref name="windowWidth"/> epx.</summary>
    public static double DialogWidth(double windowWidth)
    {
        var target = windowWidth >= LargeBreakpoint ? LargeWidth : MediumWidth;
        return Math.Max(MinimumWidth, Math.Min(target, windowWidth - (2 * WindowMargin)));
    }

    /// <summary>The fixed width of the dialog's content (the dialog width less its padding).</summary>
    public static double ContentWidth(double windowWidth) => DialogWidth(windowWidth) - DialogHorizontalPadding;

    /// <summary>S or a short window gets a full-height dialog so Save and Cancel stay reachable (T-08.1).</summary>
    public static bool IsFullSize(double windowWidth, double windowHeight) =>
        windowWidth < SmallBreakpoint || windowHeight < ShortWindowHeight;
}
