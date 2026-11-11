using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The flowing line of a tag field (Planner's labels field): chips left to right, wrapping onto more lines, and the last
/// child (the text input) right after the last chip, taking the rest of that line. When less than
/// <see cref="InputMinWidth"/> is left, the input starts a new line of its own. Each line is as tall as its tallest child
/// and the panel reports the height of all its lines, so the content below always starts under it.
/// </summary>
public sealed partial class TokenFieldPanel : Panel
{
    private const double Spacing = 4;

    // The line each child is on and that line's height, from the last measure; arrange places by them.
    private readonly List<(int Line, double X, double Width)> _slots = [];
    private readonly List<double> _lineHeights = [];

    public double InputMinWidth { get; set; } = 140;

    protected override Size MeasureOverride(Size availableSize)
    {
        _slots.Clear();
        _lineHeights.Clear();
        var width = availableSize.Width;
        double x = 0, lineHeight = 0, widest = 0;
        var line = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var isInput = i == Children.Count - 1;
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(default);
                _slots.Add((line, x, 0));
                continue;
            }

            double slotWidth;
            if (isInput)
            {
                // The input fills what is left of the line, or a line of its own when too little is left.
                var left = double.IsInfinity(width) ? InputMinWidth : width - x;
                if (x > 0 && left < InputMinWidth)
                {
                    NewLine();
                    left = double.IsInfinity(width) ? InputMinWidth : width;
                }

                child.Measure(new Size(Math.Max(0, left), double.PositiveInfinity));
                slotWidth = Math.Max(0, left);
            }
            else
            {
                child.Measure(new Size(width, double.PositiveInfinity));
                slotWidth = Math.Min(child.DesiredSize.Width, width);
                if (x > 0 && x + slotWidth > width)
                {
                    NewLine();
                }
            }

            _slots.Add((line, x, slotWidth));
            x += slotWidth + Spacing;
            widest = Math.Max(widest, x - Spacing);
            lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);
        }

        _lineHeights.Add(lineHeight);
        var height = _lineHeights.Sum() + (Spacing * (_lineHeights.Count - 1));
        return new Size(double.IsInfinity(width) ? widest : width, height);

        void NewLine()
        {
            _lineHeights.Add(lineHeight);
            line++;
            x = 0;
            lineHeight = 0;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var top = new double[_lineHeights.Count];
        for (var l = 1; l < top.Length; l++)
        {
            top[l] = top[l - 1] + _lineHeights[l - 1] + Spacing;
        }

        for (var i = 0; i < Children.Count && i < _slots.Count; i++)
        {
            var child = Children[i];
            var (line, x, width) = _slots[i];
            if (child.Visibility == Visibility.Collapsed || line >= top.Length)
            {
                child.Arrange(default);
                continue;
            }

            // Each child is centered in its line, so a chip lines up with the input's text.
            var height = child.DesiredSize.Height;
            child.Arrange(new Rect(x, top[line] + ((_lineHeights[line] - height) / 2), width, height));
        }

        return finalSize;
    }
}
