namespace MaxPlanner.Presentation.Board;

/// <summary>
/// The Board's breakpoint layout for a page width (design §4.2): L (≥ 1008 epx) 300 epx buckets, M (≥ 640) 280,
/// S one full readable bucket per viewport, never narrower than 260. Two widths with an equal layout need no layout
/// change, so the view applies a layout only when it differs from the one it last applied.
/// </summary>
internal readonly record struct BoardLayout(double BucketWidth, double SidePadding, double HeaderTop, double HeaderBottom, bool IsSmall)
{
    private const double ExpandedWidth = 1008;
    private const double CompactWidth = 640;
    private const double MinimumSmallBucketWidth = 260;

    // The bucket's right margin in BucketTemplate; the S bucket fills the viewport less the side padding and it.
    private const double BucketSpacing = 12;

    public static BoardLayout ForWidth(double width) => width >= ExpandedWidth
        ? new(300, 32, 24, 12, IsSmall: false)
        : width >= CompactWidth
            ? new(280, 24, 16, 12, IsSmall: false)
            : new(Math.Max(MinimumSmallBucketWidth, width - (2 * 16) - BucketSpacing), 16, 12, 8, IsSmall: true);
}
