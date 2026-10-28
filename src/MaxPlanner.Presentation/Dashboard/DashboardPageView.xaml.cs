using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Dashboard;

/// <summary>
/// The Dashboard view (design §4.1). Code-behind arranges the L/M/S layouts, forwards list-row clicks, and records the
/// scroll position for drill-down and Back (AC-20). The retained view is only collapsed while another page is shown,
/// so its ScrollViewer keeps the offset itself; the recorded offset restores it when the view is loaded.
/// </summary>
public sealed partial class DashboardPageView : UserControl, Shell.IPageView
{
    private bool _isCardFitPending;

    public DashboardPageView(DashboardPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            // A collapsed (hidden) page has no width to adapt to; it applies the real width when shown again.
            if (e.NewSize.Width > 0)
            {
                ApplyBreakpoint(e.NewSize.Width);
            }
        };

        // A caption or badge that changes can change a card's height. The view and the page ViewModel share the
        // dataset's lifetime, so these subscriptions need no removal.
        foreach (var card in ViewModel.Cards)
        {
            card.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(DashboardCardViewModel.Caption) or nameof(DashboardCardViewModel.HasBadge))
                {
                    ScheduleCardHeightFit();
                }
            };
        }

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

    /// <inheritdoc/>
    public FrameworkElement HeaderElement => HeaderRoot;

    /// <inheritdoc/>
    public FrameworkElement? ContentElement => PageContent;

    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

    /// <inheritdoc/>
    public bool FocusSearch() => false;

    /// <inheritdoc/>
    public void OnShown() => PageContent.OnPageShown();

    /// <inheritdoc/>
    public void OnHidden() => PageContent.OnPageHidden();

    /// <summary>The card's last gap: a share of the spare height above the review links, or nothing without links.</summary>
    public static GridLength TrailingGap(bool hasLinks) => hasLinks ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    private void OnCardPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args) => ScheduleCardHeightFit();

    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
        {
            ScheduleCardHeightFit();
        }
    }

    private void ScheduleCardHeightFit()
    {
        if (!_isCardFitPending)
        {
            _isCardFitPending = true;
            DispatcherQueue.TryEnqueue(FitCardHeight);
        }
    }

    /// <summary>
    /// UniformGridLayout gives every card the desired height of the first one, so a card with a longer (wrapping)
    /// caption, a "Needs attention" badge or secondary links was clipped. The tallest realized card, measured at the
    /// card width the layout will use, becomes the minimum height of all of them; text scaling is included.
    /// </summary>
    private void FitCardHeight()
    {
        _isCardFitPending = false;
        var available = CardsRepeater.ActualWidth;
        if (available <= 0 || CardsRepeater.ItemsSourceView is not { } items)
        {
            return;
        }

        var spacing = CardsLayout.MinColumnSpacing;
        var columns = Math.Max(1, Math.Floor((available + spacing) / (CardsLayout.MinItemWidth + spacing)));
        var cardWidth = Math.Max(CardsLayout.MinItemWidth, (available - (spacing * (columns - 1))) / columns);

        var tallest = 0.0;
        for (var i = 0; i < items.Count; i++)
        {
            if (CardsRepeater.TryGetElement(i) is FrameworkElement card)
            {
                card.Measure(new Windows.Foundation.Size(cardWidth, double.PositiveInfinity));
                tallest = Math.Max(tallest, card.DesiredSize.Height);
            }
        }

        // Only grow or shrink on a real change, so the re-layout this causes cannot loop.
        if (tallest > 0 && Math.Abs(CardsLayout.MinItemHeight - tallest) > 0.5)
        {
            CardsLayout.MinItemHeight = tallest;
        }
    }

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
