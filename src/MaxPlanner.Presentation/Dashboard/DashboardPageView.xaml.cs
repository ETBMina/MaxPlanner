using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Dashboard;

/// <summary>
/// The Dashboard view (design §4.1). Code-behind arranges the L/M/S layouts, forwards list-row clicks, and keeps the
/// scroll position across drill-down and Back (AC-20), because the retained view is removed from the tree meanwhile.
/// </summary>
public sealed partial class DashboardPageView : UserControl
{
    public DashboardPageView(DashboardPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        SizeChanged += (_, e) => ApplyBreakpoint(e.NewSize.Width);
        // Restored after layout, when the content has its height again.
        Loaded += (_, _) =>
        {
            var offset = ViewModel.ScrollOffset;
            DispatcherQueue.TryEnqueue(() => PageScroller.ChangeView(null, offset, null, disableAnimation: true));
        };
        PageScroller.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate && IsLoaded)
            {
                ViewModel.ScrollOffset = PageScroller.VerticalOffset;
            }
        };
    }

    public DashboardPageViewModel ViewModel { get; }

    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

    private void ApplyBreakpoint(double width)
    {
        // T-08.1 page padding: L 32/24, M 24/16, S 16/12 (design §2.3).
        var inset = width >= 1008 ? 32 : width >= 640 ? 24 : 16;
        HeaderRoot.Padding = width >= 1008 ? new Thickness(32, 24, 32, 12) : width >= 640 ? new Thickness(24, 16, 24, 12) : new Thickness(16, 12, 16, 8);
        ContentRoot.Padding = new Thickness(inset, 0, inset, 24);

        // Design §4.1: cards 3–6 per row at L, 2 columns at M, 1 at S.
        CardsLayout.MinItemWidth = width >= 1008 ? 220 : width >= 640 ? Math.Max(220, (width - (2 * inset) - 12) / 2) : Math.Max(200, width - (2 * inset));

        var wide = width >= 1008;
        Place(StatusChartView, 0, 0, 1);
        Place(SquadChartView, wide ? 0 : 1, wide ? 1 : 0, 1);
        Place(ReviewChartView, wide ? 1 : 2, 0, 1);
        Place(OverdueSection, wide ? 1 : 3, wide ? 1 : 0, 1);
        Place(UnlinkedSection, wide ? 2 : 4, 0, 2);
        if (!wide)
        {
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(StatusChartView, 2);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(SquadChartView, 2);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(ReviewChartView, 2);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(OverdueSection, 2);
        }
    }

    private static void Place(FrameworkElement element, int row, int column, int span)
    {
        Microsoft.UI.Xaml.Controls.Grid.SetRow(element, row);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(element, column);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(element, span);
    }

    private void OnOverdueRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverdueRowViewModel row })
        {
            ViewModel.OpenOverdue(row);
        }
    }

    private void OnUnlinkedRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UnlinkedRowViewModel row })
        {
            ViewModel.OpenUnlinked(row);
        }
    }
}
