using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// Tag pills (design §6.1): each tag keeps one palette entry chosen from its Id, so it has the same color everywhere.
/// The colors are the <c>TagPillFill{i}</c>/<c>TagPillForeground{i}</c> theme resources, applied through the
/// <c>TagPillStyle{i}</c> and <c>TagPillButtonStyle{i}</c> styles; their ThemeResource setters follow Light, Dark, and
/// High Contrast switches by themselves, also on cached pages.
/// </summary>
public static class TagPill
{
    /// <summary>The number of palette entries; the resources and styles above exist for each.</summary>
    public const int Count = 10;

    public static int IndexOf(Guid id) => PaletteHash.IndexOf(id, Count);

    /// <summary>The style of a removable tag chip (a Button) for the tag <paramref name="id"/>.</summary>
    public static Style ButtonStyle(Guid id) => Resource("TagPillButtonStyle" + IndexOf(id));

    /// <summary>A display-only pill: a ContentPresenter holding a trimmed caption.</summary>
    public static ContentPresenter Create(Guid id, string name) => new()
    {
        Style = Resource("TagPillStyle" + IndexOf(id)),
        Content = new TextBlock { Text = name, Style = Resource("TagPillTextStyle") },
    };

    internal static Style Resource(string key) => (Style)Microsoft.UI.Xaml.Application.Current.Resources[key];
}

/// <summary>
/// One row of tag pills that fits its width (Grid Tags cells, design §4.3): the pills that fit, then "+n" for the rest.
/// When even one pill and the count do not fit, the first pill is trimmed. Display only: the cell owns input, focus,
/// and the accessible name (the comma-joined names), so the strip is hidden from UI Automation.
/// </summary>
public sealed partial class TagPillStrip : Panel
{
    private const double Spacing = 4;

    private static readonly Lazy<ResourceStrings> Strings = new(() => new ResourceStrings());

    private readonly TextBlock _overflow;
    private readonly int _pillCount;
    private int _visibleCount;

    public TagPillStrip(IReadOnlyList<(Guid Id, string Name)> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _pillCount = tags.Count;
        foreach (var (id, name) in tags)
        {
            Children.Add(TagPill.Create(id, name));
        }

        _overflow = new TextBlock { Style = TagPill.Resource("TagPillOverflowTextStyle") };
        Children.Add(_overflow);

        // A transparent background lets the strip show the cell's tooltip; taps still reach the cell.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        var widths = new double[_pillCount];
        var height = 0d;
        for (var i = 0; i < _pillCount; i++)
        {
            Children[i].Measure(unbounded);
            widths[i] = Children[i].DesiredSize.Width;
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }

        var all = widths.Sum() + (Spacing * Math.Max(0, _pillCount - 1));
        if (all <= availableSize.Width || _pillCount <= 1)
        {
            _visibleCount = _pillCount;
            if (_pillCount == 1 && all > availableSize.Width)
            {
                Children[0].Measure(availableSize);
            }

            _overflow.Measure(default);
            return new Size(Math.Min(all, availableSize.Width), height);
        }

        // Keep as many leading pills as fit beside "+n"; at least the first one, trimmed if need be.
        var used = 0d;
        for (_visibleCount = _pillCount - 1; _visibleCount >= 1; _visibleCount--)
        {
            var overflowWidth = MeasureOverflow(_pillCount - _visibleCount, unbounded);
            used = widths.Take(_visibleCount).Sum() + (Spacing * _visibleCount) + overflowWidth;
            if (used <= availableSize.Width)
            {
                break;
            }
        }

        if (_visibleCount < 1)
        {
            _visibleCount = 1;
            var overflowWidth = MeasureOverflow(_pillCount - 1, unbounded);
            Children[0].Measure(new Size(Math.Max(0, availableSize.Width - Spacing - overflowWidth), availableSize.Height));
            used = availableSize.Width;
        }

        height = Math.Max(height, _overflow.DesiredSize.Height);
        return new Size(Math.Min(used, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        for (var i = 0; i < _pillCount; i++)
        {
            var pill = Children[i];
            var isShown = i < _visibleCount;
            pill.Opacity = isShown ? 1 : 0;
            if (!isShown)
            {
                pill.Arrange(default);
                continue;
            }

            var size = pill.DesiredSize;
            pill.Arrange(new Rect(x, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            x += size.Width + Spacing;
        }

        var hasOverflow = _visibleCount < _pillCount;
        _overflow.Opacity = hasOverflow ? 1 : 0;
        var overflowSize = hasOverflow ? _overflow.DesiredSize : default;
        _overflow.Arrange(new Rect(x, (finalSize.Height - overflowSize.Height) / 2, overflowSize.Width, overflowSize.Height));
        return finalSize;
    }

    private double MeasureOverflow(int hidden, Size constraint)
    {
        var text = Strings.Value.Format("TagPill_Overflow", hidden);
        if (_overflow.Text != text)
        {
            _overflow.Text = text;
        }

        _overflow.Measure(constraint);
        return _overflow.DesiredSize.Width;
    }
}
