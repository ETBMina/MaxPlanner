using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MaxPlanner.Presentation.Board;

/// <summary>
/// The Board card avatars (design §4.2 item 6): side by side with a small gap, never overlapping, as the horizontal
/// <c>StackLayout</c> placed them. It does not virtualize, so its size depends only on its avatars, never on where the
/// card is in the bucket's viewport.
/// </summary>
/// <remarks>
/// <c>StackLayout</c> is a virtualizing layout: it realizes the avatars that meet the repeater's realization window,
/// which follows the effective viewport and changes as the bucket scrolls. Its width then changed while scrolling, the
/// wrapping dates text beside it (Subtask card footer) changed its line count, the card its height, the bucket list moved
/// the cards, and the viewports changed again: a layout cycle once a dates text was long enough to wrap near that width.
/// </remarks>
public sealed partial class CardPeopleLayout : NonVirtualizingLayout
{
    /// <summary>The gap between avatars.</summary>
    public double Spacing { get; set; } = 4;

    /// <summary>The row's size: the children's widths plus the gaps between them, and the tallest child.</summary>
    public static Size Extent(IReadOnlyList<Size> childSizes, double spacing)
    {
        ArgumentNullException.ThrowIfNull(childSizes);
        if (childSizes.Count == 0)
        {
            return default;
        }

        return new Size(childSizes.Sum(s => s.Width) + (spacing * (childSizes.Count - 1)), childSizes.Max(s => s.Height));
    }

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sizes = new List<Size>(context.Children.Count);
        foreach (var child in context.Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            sizes.Add(child.DesiredSize);
        }

        return Extent(sizes, Spacing);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        ArgumentNullException.ThrowIfNull(context);
        var x = 0d;
        foreach (var child in context.Children)
        {
            var width = child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width + Spacing;
        }

        return finalSize;
    }
}
