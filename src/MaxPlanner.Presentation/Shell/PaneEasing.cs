namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// The easing of the stock SplitView pane transitions, <c>KeySpline="0.0,0.35 0.15,1.0"</c>: a cubic Bézier from (0,0)
/// to (1,1) with control points (0, 0.35) and (0.15, 1), so <see cref="NavigationPaneSlide"/> keeps the stock feel.
/// </summary>
internal static class PaneEasing
{
    private const double X1 = 0.0;
    private const double Y1 = 0.35;
    private const double X2 = 0.15;
    private const double Y2 = 1.0;

    /// <summary>Eased progress for a time fraction in [0, 1]; monotonic, 0 at 0 and 1 at 1.</summary>
    public static double Ease(double time)
    {
        if (time <= 0)
        {
            return 0;
        }

        if (time >= 1)
        {
            return 1;
        }

        // x(s) is monotonic for control x values in [0, 1], so bisection finds the curve parameter for this time.
        double low = 0, high = 1, s = time;
        for (var i = 0; i < 30; i++)
        {
            s = (low + high) / 2;
            if (Bezier(s, X1, X2) < time)
            {
                low = s;
            }
            else
            {
                high = s;
            }
        }

        return Bezier(s, Y1, Y2);
    }

    private static double Bezier(double s, double p1, double p2)
    {
        var inverse = 1 - s;
        return (3 * inverse * inverse * s * p1) + (3 * inverse * s * s * p2) + (s * s * s);
    }
}
