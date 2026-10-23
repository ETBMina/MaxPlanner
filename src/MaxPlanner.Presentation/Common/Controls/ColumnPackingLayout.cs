using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// A virtualizing multi-column layout for cards of different heights (design §4.5 Checklists groups). Each item
/// goes to the currently shortest column, in item order, so the first item (General to-dos) is always at the
/// top left. Only items whose estimated position meets the realization window are created and measured.
/// </summary>
/// <remarks>
/// Phase 2 used <c>UniformGridLayout</c>, which sizes every item like the first one and clipped taller groups
/// (implementation-plan 2g). Positions depend on the measured heights of earlier items; an item never measured
/// yet uses the average measured height, so positions below the viewport can shift as cards are realized.
/// </remarks>
public sealed partial class ColumnPackingLayout : VirtualizingLayout
{
    private const double FallbackEstimate = 240;

    public double MinColumnWidth { get; set; } = 380;

    public int MaxColumns { get; set; } = 3;

    public double ColumnSpacing { get; set; } = 12;

    public double RowSpacing { get; set; } = 12;

    protected override void InitializeForContextCore(VirtualizingLayoutContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.LayoutState = new PackingState();
    }

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.LayoutState = null;
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = State(context);
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MinColumnWidth;
        var columns = Math.Clamp((int)((width + ColumnSpacing) / (MinColumnWidth + ColumnSpacing)), 1, Math.Max(1, MaxColumns));
        var columnWidth = Math.Max(0, (width - (ColumnSpacing * (columns - 1))) / columns);
        if (columns != state.Columns || Math.Abs(columnWidth - state.ColumnWidth) > 0.5)
        {
            // Heights depend on the width, so measured heights from another width are only estimates now.
            state.Columns = columns;
            state.ColumnWidth = columnWidth;
            for (var i = 0; i < state.Heights.Count; i++)
            {
                state.Heights[i] = null;
            }
        }

        var count = context.ItemCount;
        state.Resize(count);
        var realization = context.RealizationRect;
        if (realization.Height <= 0)
        {
            // Before the first viewport is known, realize what fits on a typical screen.
            realization = new Rect(0, 0, width, 1200);
        }

        var bottoms = new double[columns];
        var realized = new HashSet<int>();
        var estimate = state.Estimate(FallbackEstimate);
        for (var index = 0; index < count; index++)
        {
            var column = Shortest(bottoms);
            var top = bottoms[column];
            var height = state.Heights[index] ?? estimate;
            if (top < realization.Bottom && top + height > realization.Top)
            {
                var element = context.GetOrCreateElementAt(index);
                element.Measure(new Size(columnWidth, double.PositiveInfinity));
                height = element.DesiredSize.Height;
                state.Heights[index] = height;
                realized.Add(index);
            }

            state.Bounds[index] = new Rect(column * (columnWidth + ColumnSpacing), top, columnWidth, height);
            bottoms[column] = top + height + RowSpacing;
        }

        foreach (var index in state.Realized.Where(i => !realized.Contains(i) && i < count))
        {
            context.RecycleElement(context.GetOrCreateElementAt(index));
        }

        state.Realized = realized;
        var extent = count == 0 ? 0 : Math.Max(0, bottoms.Max() - RowSpacing);
        return new Size(width, extent);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = State(context);
        foreach (var index in state.Realized)
        {
            if (index < context.ItemCount)
            {
                context.GetOrCreateElementAt(index).Arrange(state.Bounds[index]);
            }
        }

        return finalSize;
    }

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var state = State(context);
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add when args.NewStartingIndex >= 0 && args.NewStartingIndex <= state.Heights.Count:
                state.Heights.InsertRange(args.NewStartingIndex, Enumerable.Repeat<double?>(null, args.NewItems?.Count ?? 1));
                state.Realized = [.. state.Realized.Select(i => i >= args.NewStartingIndex ? i + (args.NewItems?.Count ?? 1) : i)];
                break;
            case NotifyCollectionChangedAction.Remove when args.OldStartingIndex >= 0 && args.OldStartingIndex < state.Heights.Count:
                var removed = Math.Min(args.OldItems?.Count ?? 1, state.Heights.Count - args.OldStartingIndex);
                state.Heights.RemoveRange(args.OldStartingIndex, removed);
                state.Realized = [.. state.Realized
                    .Where(i => i < args.OldStartingIndex || i >= args.OldStartingIndex + removed)
                    .Select(i => i >= args.OldStartingIndex + removed ? i - removed : i)];
                break;
            default:
                // Move, Replace, and Reset: the repeater re-creates what it needs; measured heights are re-learned.
                state.Heights.Clear();
                state.Realized = [];
                break;
        }

        InvalidateMeasure();
    }

    private static int Shortest(double[] bottoms)
    {
        var best = 0;
        for (var i = 1; i < bottoms.Length; i++)
        {
            if (bottoms[i] < bottoms[best] - 0.5)
            {
                best = i;
            }
        }

        return best;
    }

    private static PackingState State(VirtualizingLayoutContext context) =>
        context.LayoutState as PackingState ?? throw new InvalidOperationException("The layout was not initialized for this context.");

    /// <summary>Per-repeater state: measured heights by index, the arranged bounds, and the realized indexes.</summary>
    private sealed class PackingState
    {
        public int Columns { get; set; }

        public double ColumnWidth { get; set; }

        public List<double?> Heights { get; } = [];

        public List<Rect> Bounds { get; } = [];

        public HashSet<int> Realized { get; set; } = [];

        public void Resize(int count)
        {
            while (Heights.Count < count)
            {
                Heights.Add(null);
            }

            if (Heights.Count > count)
            {
                Heights.RemoveRange(count, Heights.Count - count);
            }

            while (Bounds.Count < count)
            {
                Bounds.Add(default);
            }

            if (Bounds.Count > count)
            {
                Bounds.RemoveRange(count, Bounds.Count - count);
            }
        }

        public double Estimate(double fallback)
        {
            var known = Heights.Where(h => h is not null).Select(h => h!.Value).ToList();
            return known.Count == 0 ? fallback : known.Average();
        }
    }
}
