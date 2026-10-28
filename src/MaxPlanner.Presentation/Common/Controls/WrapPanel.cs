using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// Lays children left to right and wraps them onto new lines (filter chips, design §2.6). ADR 0021 keeps the
/// Community Toolkit out of release 1, so this replaces its WrapPanel. Not virtualized: use it for short lists only.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(8d, (d, _) => ((WrapPanel)d).InvalidateMeasure()));

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines), typeof(int), typeof(WrapPanel), new PropertyMetadata(0, (d, _) => ((WrapPanel)d).InvalidateMeasure()));

    public static readonly DependencyProperty WrapWidthProperty = DependencyProperty.Register(
        nameof(WrapWidth), typeof(double), typeof(WrapPanel), new PropertyMetadata(0d, (d, _) => ((WrapPanel)d).InvalidateMeasure()));

    // Layout rounding can hand a content-sized panel a width a fraction below its desired width; without this
    // tolerance the last child of an exactly-fitting line would wrap onto a new line.
    private const double WrapTolerance = 0.5;

    private bool _isSingleLine;

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>
    /// When above zero and the children would need more lines than this, they are laid out on one line instead, for
    /// a horizontally scrolling host (design §2.3: the chips row at S scrolls when it would exceed two rows).
    /// </summary>
    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    /// <summary>
    /// The width to wrap at when the host measures with unlimited width (a horizontally scrolling ScrollViewer passes
    /// infinity); zero uses the available width.
    /// </summary>
    public double WrapWidth
    {
        get => (double)GetValue(WrapWidthProperty);
        set => SetValue(WrapWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = WrapWidth > 0 ? WrapWidth : availableSize.Width;
        var lines = 1;
        double singleLineWidth = 0, singleLineHeight = 0;
        double lineWidth = 0, lineHeight = 0, totalWidth = 0, totalHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            var size = child.DesiredSize;
            singleLineWidth += (singleLineWidth > 0 ? Spacing : 0) + size.Width;
            singleLineHeight = Math.Max(singleLineHeight, size.Height);
            if (lineWidth > 0 && lineWidth + Spacing + size.Width > width + WrapTolerance)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + Spacing;
                lineWidth = 0;
                lineHeight = 0;
                lines++;
            }

            lineWidth += (lineWidth > 0 ? Spacing : 0) + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        _isSingleLine = MaxLines > 0 && lines > MaxLines;
        if (_isSingleLine)
        {
            return new Size(singleLineWidth, singleLineHeight);
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;
        return new Size(double.IsInfinity(width) ? totalWidth : Math.Min(width, totalWidth), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var wrapWidth = _isSingleLine ? double.PositiveInfinity : WrapWidth > 0 ? Math.Min(WrapWidth, finalSize.Width) : finalSize.Width;
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > wrapWidth + WrapTolerance)
            {
                x = 0;
                y += lineHeight + Spacing;
                lineHeight = 0;
            }

            child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
            x += size.Width + Spacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return finalSize;
    }
}

/// <summary>
/// A horizontal bar track for the Dashboard charts (ADR 0011): its single child gets <see cref="Fraction"/> of the
/// width, at least 2 epx when the fraction is above zero, and nothing at zero (design §4.1).
/// </summary>
public sealed partial class BarTrack : Panel
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(BarTrack), new PropertyMetadata(0d, (d, _) => ((BarTrack)d).InvalidateArrange()));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var fraction = Math.Clamp(Fraction, 0, 1);
        var width = fraction <= 0 ? 0 : Math.Max(2, finalSize.Width * fraction);
        foreach (var child in Children)
        {
            child.Arrange(new Rect(0, 0, width, finalSize.Height));
        }

        return finalSize;
    }
}
