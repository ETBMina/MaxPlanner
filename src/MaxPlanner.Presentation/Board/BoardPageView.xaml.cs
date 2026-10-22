using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Board;

/// <summary>The Board page view. Code-behind sizes columns per breakpoint (design §4.2 S/M/L).</summary>
public sealed partial class BoardPageView : UserControl
{
    private readonly HashSet<FrameworkElement> _columns = [];
    private double _columnWidth = 300;

    public BoardPageView(BoardPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        SizeChanged += (_, e) => ApplyBreakpoint(e.NewSize.Width);
    }

    public BoardPageViewModel ViewModel { get; }

    /// <summary>The priority chip style for a rank token (design §6.1); styles use theme resources, so they follow theme changes.</summary>
    public static Style PriorityChipStyle(PriorityLevel level) =>
        (Style)Microsoft.UI.Xaml.Application.Current.Resources[level switch
        {
            PriorityLevel.Level1 => "PriorityLevel1ChipStyle",
            PriorityLevel.Level2 => "PriorityLevel2ChipStyle",
            PriorityLevel.Lowest => "PriorityLowestChipStyle",
            _ => "PriorityMidChipStyle",
        }];

    /// <summary>Important, ChevronUp, Remove (dash), ChevronDown (design §6.1).</summary>
    public static string PriorityGlyph(PriorityLevel level) => level switch
    {
        PriorityLevel.Level1 => "",
        PriorityLevel.Level2 => "",
        PriorityLevel.Lowest => "",
        _ => "",
    };

    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

    private void OnColumnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement column && _columns.Add(column))
        {
            column.Width = _columnWidth;
        }
    }

    private void OnColumnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement column)
        {
            _columns.Remove(column);
        }
    }

    private void ApplyBreakpoint(double width)
    {
        // Design §4.2: L 300 epx columns, M 280, S one full readable column per viewport (no column shrinks
        // below a readable width; the horizontal scroll reaches the others).
        var padding = width >= 1008 ? 32 : width >= 640 ? 24 : 16;
        _columnWidth = width >= 1008 ? 300 : width >= 640 ? 280 : Math.Max(260, width - (2 * padding) - 12);
        HeaderRoot.Padding = width >= 1008 ? new Thickness(32, 24, 32, 12) : width >= 640 ? new Thickness(24, 16, 24, 12) : new Thickness(16, 12, 16, 8);
        // ItemsControl's default template ignores Padding, so the inset is a margin inside the scroller.
        ColumnsHost.Margin = new Thickness(padding, 0, padding - 12, 12);
        ColumnsScroller.HorizontalSnapPointsType = width < 640 ? SnapPointsType.MandatorySingle : SnapPointsType.None;
        foreach (var column in _columns)
        {
            column.Width = _columnWidth;
        }
    }
}
