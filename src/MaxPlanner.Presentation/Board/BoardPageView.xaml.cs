using System.ComponentModel;
using MaxPlanner.Presentation.Common.Services;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace MaxPlanner.Presentation.Board;

/// <summary>
/// The Board page view. Code-behind handles view mechanics only (AGENTS §4): column widths per breakpoint,
/// drag visuals and the drop index, keyboard routing, the card menu, focus after a move, and the S-breakpoint
/// column scroll. Every move and command is run by <see cref="BoardPageViewModel"/>.
/// </summary>
public sealed partial class BoardPageView : UserControl, Shell.IPageView
{
    private static readonly ResourceStrings Strings = new();

    private readonly HashSet<FrameworkElement> _columns = [];
    private readonly Dictionary<BoardColumnViewModel, BoardCardList> _lists = [];
    private double _columnWidth = 300;
    private bool _isSmall;
    private BoardCardViewModel? _dragged;
    private BoardColumnViewModel? _dragSource;

    // Drop feedback (design §4.2): the list under the drag, its scroller, and the last list-local pointer Y.
    private BoardCardList? _feedbackList;
    private ScrollViewer? _feedbackScroller;
    private double _feedbackPointerY;
    private readonly Common.Controls.CompactSearch _search;

    // Drag edge auto-scroll: depth into the edge zone, signed by direction (-1..1), per axis.
    private const double AutoScrollZone = 56;
    private const double AutoScrollMaxStep = 18;
    private const double SmallPageThreshold = 40;
    private DispatcherQueueTimer? _autoScrollTimer;
    private double _horizontalDepth;
    private double _verticalDepth;
    private ScrollViewer? _verticalScroller;
    private double _pageProgress;

    public BoardPageView(BoardPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _search = new Common.Controls.CompactSearch(SearchBox, SearchToggle, CompactSearchRow, SearchBoxHost);
        SizeChanged += (_, e) => ApplyBreakpoint(e.NewSize.Width);
        ViewModel.FocusRequested += OnFocusRequested;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ColumnPicker.SelectedItem = ViewModel.SelectedColumn;

        // Edge auto-scroll watches drag events that column lists may already have handled.
        ColumnsScroller.AddHandler(DragOverEvent, new DragEventHandler(OnScrollerDragOver), true);
        ColumnsScroller.AddHandler(DragLeaveEvent, new DragEventHandler(OnScrollerDragLeave), true);
        ColumnsScroller.AddHandler(DropEvent, new DragEventHandler((_, _) => StopAutoScroll()), true);
        Unloaded += (_, _) =>
        {
            StopAutoScroll();
            ClearDropFeedback();
        };
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
        PriorityLevel.Level1 => "\uE8C9",
        PriorityLevel.Level2 => "\uE70E",
        PriorityLevel.Lowest => "\uE70D",
        _ => "\uE738",
    };

    /// <summary>Design §4.2: 60% opacity while the card is being moved; the "Moving" caption says it in text.</summary>
    public static double MovingOpacity(bool isMoving) => isMoving ? 0.6 : 1.0;

    /// <inheritdoc/>
    public FrameworkElement HeaderElement => HeaderRoot;

    /// <inheritdoc/>
    public FrameworkElement? ContentElement => PageContent;

    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

    /// <inheritdoc/>
    public bool FocusSearch()
    {
        _search.FocusSearch();
        return true;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

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

    private void OnListLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is BoardCardList { DataContext: BoardColumnViewModel column } list)
        {
            _lists[column] = list;
        }
    }

    private void OnListUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is BoardCardList { DataContext: BoardColumnViewModel column } list && _lists.TryGetValue(column, out var known) && known == list)
        {
            _lists.Remove(column);
        }
    }

    private void ApplyBreakpoint(double width)
    {
        // Design §4.2: L 300 epx columns, M 280, S one full readable column per viewport (no column shrinks
        // below a readable width; the column picker and the horizontal scroll reach the others).
        var padding = width >= 1008 ? 32 : width >= 640 ? 24 : 16;
        _columnWidth = width >= 1008 ? 300 : width >= 640 ? 280 : Math.Max(260, width - (2 * padding) - 12);
        HeaderRoot.Padding = width >= 1008 ? new Thickness(32, 24, 32, 12) : width >= 640 ? new Thickness(24, 16, 24, 12) : new Thickness(16, 12, 16, 8);

        // ItemsControl's default template ignores Padding, so the inset is a margin inside the scroller.
        ColumnsHost.Margin = new Thickness(padding, 0, padding - 12, 12);
        _isSmall = width < 640;
        _search.Apply(width);
        ChipsBar.IsCompact = _isSmall;
        ColumnsScroller.HorizontalSnapPointsType = _isSmall ? SnapPointsType.MandatorySingle : SnapPointsType.None;
        ColumnPickerRow.Visibility = _isSmall ? Visibility.Visible : Visibility.Collapsed;
        foreach (var column in _columns)
        {
            column.Width = _columnWidth;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BoardPageViewModel.SelectedColumn))
        {
            return;
        }

        if (!Equals(ColumnPicker.SelectedItem, ViewModel.SelectedColumn))
        {
            ColumnPicker.SelectedItem = ViewModel.SelectedColumn;
        }

        if (_isSmall && ViewModel.SelectedColumn is { } selected)
        {
            ScrollToColumn(selected);
        }
    }

    private void OnColumnPickerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColumnPicker.SelectedItem is BoardColumnViewModel column)
        {
            ViewModel.SelectedColumn = column;
        }
    }

    private void ScrollToColumn(BoardColumnViewModel column)
    {
        var index = ViewModel.Columns.IndexOf(column);
        if (index >= 0)
        {
            // Design §9: no scroll animation when Windows animations are turned off.
            ColumnsScroller.ChangeView(index * (_columnWidth + 12), null, null, disableAnimation: !Common.Services.MotionSettings.AnimationsEnabled);
        }
    }

    private void OnColumnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BoardColumnViewModel column })
        {
            _ = ViewModel.NewTaskAsync(column);
        }
    }

    // ----- Card menu (design §4.2): the ⋯ button, right-click, Shift+F10, and the Application key. -----
    private void OnCardMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BoardCardViewModel card } button)
        {
            BuildMenu(card).ShowAt(button);
        }
    }

    private void OnCardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (FindContainer(args.OriginalSource as DependencyObject) is not { Content: BoardCardViewModel card } container)
        {
            return;
        }

        args.Handled = true;
        var menu = BuildMenu(card);
        if (args.TryGetPosition(container, out var point))
        {
            menu.ShowAt(container, point);
        }
        else
        {
            menu.ShowAt(container);
        }
    }

    private MenuFlyout BuildMenu(BoardCardViewModel card)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Board_MenuOpen", "Board_MenuOpenItem", "", () => _ = ViewModel.OpenAsync(card)));
        if (card.IsSubtaskCard)
        {
            menu.Items.Add(Item("Board_MenuOpenParent", "Board_MenuOpenParentItem", null, () => _ = ViewModel.OpenParentAsync(card)));
        }

        // The card's CR link is not a tab stop inside the list, so the menu is its keyboard route (design §7.3).
        if (card.HasCrReference)
        {
            menu.Items.Add(Item("Board_MenuOpenCr", "Board_MenuOpenCrItem", null, () => _ = ViewModel.OpenCrAsync(card)));
        }

        var moveTo = new MenuFlyoutSubItem { Text = Strings.GetString("Board_MenuMoveTo") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(moveTo, "Board_MenuMoveToItem");
        foreach (var column in ViewModel.Columns)
        {
            var target = new MenuFlyoutSubItem { Text = column.Name };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(target, "Board_MenuMoveTo_" + column.StatusId.Value.ToString("N"));
            // B-07: while filtered Top/Bottom address the full column and same-column reordering is off.
            var enabled = !ViewModel.IsFiltered || ViewModel.ColumnOf(card) != column;
            target.Items.Add(Item("Board_MenuTopOfColumn", "Board_MenuTop_" + column.StatusId.Value.ToString("N"), null,
                () => _ = ViewModel.MoveToColumnAsync(card, column, BoardEdge.Top), enabled));
            target.Items.Add(Item("Board_MenuBottomOfColumn", "Board_MenuBottom_" + column.StatusId.Value.ToString("N"), null,
                () => _ = ViewModel.MoveToColumnAsync(card, column, BoardEdge.Bottom), enabled));

            moveTo.Items.Add(target);
        }

        menu.Items.Add(moveTo);
        menu.Items.Add(Item("Board_MenuMoveUp", "Board_MenuMoveUpItem", "", () => _ = ViewModel.MoveWithinColumnAsync(card, -1), ViewModel.CanMoveUp(card)));
        menu.Items.Add(Item("Board_MenuMoveDown", "Board_MenuMoveDownItem", "", () => _ = ViewModel.MoveWithinColumnAsync(card, 1), ViewModel.CanMoveDown(card)));
        if (card.IsTask)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Board_MenuCopy", "Board_MenuCopyItem", "", () => _ = ViewModel.CopyAsync(card)));
            menu.Items.Add(Item("Board_MenuAddSubtaskCard", "Board_MenuAddSubtaskCardItem", "", () => _ = ViewModel.AddSubtaskCardAsync(card)));
            menu.Items.Add(Item("Board_MenuArchive", "Board_MenuArchiveItem", "", () => _ = ViewModel.ArchiveAsync(card)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Board_MenuDelete", "Board_MenuDeleteItem", "", () => _ = ViewModel.DeleteAsync(card)));
        return menu;
    }

    private void OnCardCrLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BoardCardViewModel card })
        {
            _ = ViewModel.OpenCrAsync(card);
        }
    }

    private void OnCardParentLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BoardCardViewModel card })
        {
            _ = ViewModel.OpenParentAsync(card);
        }
    }

    // A click anywhere on the card that is not a drag or a child control's press, or UIA Invoke.
    private void OnCardInvoked(object? sender, BoardCardViewModel card) => _ = ViewModel.OpenAsync(card);

    private static MenuFlyoutItem Item(string textKey, string automationId, string? glyph, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = Strings.GetString(textKey), IsEnabled = enabled };
        if (glyph is not null)
        {
            item.Icon = new FontIcon { Glyph = glyph };
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => action();
        return item;
    }

    // ----- Keyboard (design §7.3) -----
    private void OnListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not BoardCardList { DataContext: BoardColumnViewModel column } || FindContainer(e.OriginalSource as DependencyObject) is not { Content: BoardCardViewModel card })
        {
            return;
        }

        var alt = IsDown(VirtualKey.Menu);
        var shift = IsDown(VirtualKey.Shift);
        var control = IsDown(VirtualKey.Control);
        switch (e.Key)
        {
            case VirtualKey.Left or VirtualKey.Right when alt && shift:
                e.Handled = true;
                _ = ViewModel.MoveToAdjacentColumnAsync(card, e.Key == VirtualKey.Left ? -1 : 1);
                break;
            case VirtualKey.Up or VirtualKey.Down when alt && shift:
                e.Handled = true;
                _ = ViewModel.MoveWithinColumnAsync(card, e.Key == VirtualKey.Up ? -1 : 1);
                break;
            case VirtualKey.Left or VirtualKey.Right when control:
                e.Handled = true;
                FocusAdjacentColumn(column, column.Cards.IndexOf(card), e.Key == VirtualKey.Left ? -1 : 1);
                break;
            case VirtualKey.Enter when !alt && !control && !shift:
                e.Handled = true;
                _ = ViewModel.OpenAsync(card);
                break;

            // Space opens only from the card itself, so a focused checkbox on the card keeps its toggle (§7.3).
            case VirtualKey.Space when !alt && !control && !shift && e.OriginalSource is BoardCardContainer:
                e.Handled = true;
                _ = ViewModel.OpenAsync(card);
                break;
            case VirtualKey.Delete:
                e.Handled = true;
                _ = ViewModel.DeleteAsync(card);
                break;
            case VirtualKey.D when control:
                e.Handled = true;
                _ = ViewModel.CopyAsync(card);
                break;
            default:
                break;
        }
    }

    // Design §7.3 Ctrl+Left/Right: the adjacent column at the same index, clamped.
    private void FocusAdjacentColumn(BoardColumnViewModel column, int index, int direction)
    {
        var target = ViewModel.Columns.IndexOf(column) + direction;
        if (target < 0 || target >= ViewModel.Columns.Count || !_lists.TryGetValue(ViewModel.Columns[target], out var list))
        {
            return;
        }

        if (_isSmall)
        {
            ViewModel.SelectedColumn = ViewModel.Columns[target];
        }

        var cards = ViewModel.Columns[target].Cards;
        if (cards.Count == 0)
        {
            list.Focus(FocusState.Keyboard);
            return;
        }

        FocusCard(list, cards[Math.Clamp(index, 0, cards.Count - 1)]);
    }

    private void OnFocusRequested(object? sender, BoardFocusRequest request)
    {
        if (_isSmall)
        {
            ViewModel.SelectedColumn = request.Column;
        }

        if (_lists.TryGetValue(request.Column, out var list))
        {
            FocusCard(list, request.Card);
        }
    }

    // Design §7.7: focus follows the moved card; scrolling happens only to bring it into view.
    private void FocusCard(BoardCardList list, BoardCardViewModel card)
    {
        list.ScrollIntoView(card);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (list.ContainerFromItem(card) is Control container)
            {
                container.Focus(FocusState.Keyboard);
            }
        });
    }

    // ----- Drag and drop (design §4.2 drop feedback, §9) -----
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (ViewModel.IsEditingBlocked || e.Items.FirstOrDefault() is not BoardCardViewModel card || sender is not BoardCardList { DataContext: BoardColumnViewModel column })
        {
            e.Cancel = true;
            return;
        }

        _dragged = card;
        _dragSource = column;
        card.IsMoving = true;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(card.Title);
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // A drop that did not run a move (cancelled, rejected, outside a column) leaves the card as it was.
        if (_dragged is { } card && args.DropResult == DataPackageOperation.None)
        {
            card.IsMoving = false;
        }

        _dragged = null;
        _dragSource = null;
        ClearDropFeedback();
        StopAutoScroll();
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (_dragged is null || sender is not BoardCardList { DataContext: BoardColumnViewModel column } list)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.DragUIOverride.IsCaptionVisible = true;
        if (ViewModel.IsFiltered && column == _dragSource)
        {
            // B-07: positional reordering is off while filtered; the forbidden cursor and caption say so (§9).
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.Caption = Strings.GetString("Board_ReorderOffCaption");
            ClearDropFeedback();
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = ViewModel.IsFiltered ? column.AppendCaption : column.DropCaption;
        ShowDropFeedback(list, e.GetPosition(list).Y);
    }

    private void OnListDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not BoardCardList list || !ReferenceEquals(list, _feedbackList))
        {
            return;
        }

        // Leave events also bubble up from the cards as the pointer crosses between them inside the list; only
        // leaving the list's own bounds clears its feedback (the next DragOver elsewhere also moves it).
        var point = e.GetPosition(list);
        if (point.X > 1 && point.Y > 1 && point.X < list.ActualWidth - 1 && point.Y < list.ActualHeight - 1)
        {
            return;
        }

        ClearDropFeedback();
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (_dragged is not { } card || sender is not BoardCardList { DataContext: BoardColumnViewModel column } list)
        {
            return;
        }

        // The indicator and the drop share one slot computation, so the card lands where the line was drawn.
        var index = DropSlot(list, e.GetPosition(list).Y).Index;
        ClearDropFeedback();

        // The view model's index excludes the dragged card itself, so a later slot in its own column shifts by one.
        var current = column.Cards.IndexOf(card);
        if (current >= 0 && current < index)
        {
            index--;
        }

        // The slots directly above and below the card are its own position: nothing to move, so undim it here
        // (the accepted drop means DragItemsCompleted will not).
        if (current >= 0 && current == index && !ViewModel.IsFiltered)
        {
            card.IsMoving = false;
            return;
        }

        _ = ViewModel.DropAsync(card, column, index);
    }

    /// <summary>
    /// The insert slot for a list-local pointer Y and the Y of its insertion line: before the first card whose
    /// midpoint is below the pointer, else the end (also for an empty column or the space under the last card).
    /// While filtered a cross-column drop appends to the full column (B-07), so the slot is always the end.
    /// </summary>
    private (int Index, double LineY) DropSlot(BoardCardList list, double pointerY)
    {
        var count = list.Items.Count;
        var endY = 0.0;
        var lastRealized = -1;
        for (var i = 0; i < count; i++)
        {
            // Unrealized containers lie outside the viewport and its cache, never under the pointer.
            if (list.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }

            var top = container.TransformToVisual(list).TransformPoint(default).Y;
            if (!ViewModel.IsFiltered && pointerY < top + (container.ActualHeight / 2))
            {
                return (i, top);
            }

            endY = top + container.ActualHeight;
            lastRealized = i;
        }

        // An end slot below the realized cards (a long filtered column) is pinned to the list's visible bottom.
        return (count, count > 0 && lastRealized < count - 1 ? list.ActualHeight : endY);
    }

    private void ShowDropFeedback(BoardCardList list, double pointerY)
    {
        if (!ReferenceEquals(list, _feedbackList))
        {
            ClearDropFeedback();
            _feedbackList = list;
            list.BorderThickness = new Thickness(2);

            // Edge auto-scroll moves the cards under a still pointer, so the line follows the list's own scrolling.
            _feedbackScroller = FindScroller(list);
            if (_feedbackScroller is { } scroller)
            {
                scroller.ViewChanged += OnFeedbackScrollerViewChanged;
            }
        }

        _feedbackPointerY = pointerY;
        PositionInsertionLine();
    }

    private void OnFeedbackScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => PositionInsertionLine();

    private void PositionInsertionLine()
    {
        if (_feedbackList is not { } list || InsertionLine(list) is not { RenderTransform: TranslateTransform offset } line)
        {
            return;
        }

        var lineY = DropSlot(list, _feedbackPointerY).LineY;
        offset.Y = Math.Clamp(lineY - (line.Height / 2), 0, Math.Max(0, list.ActualHeight - line.Height));
        line.Visibility = Visibility.Visible;
    }

    private void ClearDropFeedback()
    {
        if (_feedbackScroller is { } scroller)
        {
            scroller.ViewChanged -= OnFeedbackScrollerViewChanged;
        }

        if (_feedbackList is { } list)
        {
            list.BorderThickness = new Thickness(0);
            if (InsertionLine(list) is { } line)
            {
                line.Visibility = Visibility.Collapsed;
            }
        }

        _feedbackList = null;
        _feedbackScroller = null;
    }

    // The column template places the insertion line beside the list in the same Grid cell.
    private static Microsoft.UI.Xaml.Shapes.Rectangle? InsertionLine(BoardCardList list) =>
        VisualTreeHelper.GetParent(list) is Panel panel ? panel.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().FirstOrDefault() : null;

    // ----- Drag edge auto-scroll: view mechanics only; the drop and its move rules are unchanged (B-07). -----
    private void OnScrollerDragOver(object sender, DragEventArgs e)
    {
        if (_dragged is null)
        {
            return;
        }

        var point = e.GetPosition(ColumnsScroller);
        SetHorizontalDepth(EdgeDepth(point.X, ColumnsScroller.ActualWidth));

        _verticalScroller = FindAncestor<BoardCardList>(e.OriginalSource as DependencyObject) is { } list ? FindScroller(list) : null;
        _verticalDepth = _verticalScroller is { } scroller ? EdgeDepth(e.GetPosition(scroller).Y, scroller.ActualHeight) : 0;
        UpdateAutoScrollTimer();
    }

    private void OnScrollerDragLeave(object sender, DragEventArgs e)
    {
        // Leave events bubble from the column lists too; only leaving the scroller itself stops the horizontal axis.
        var point = e.GetPosition(ColumnsScroller);
        if (point.X < 0 || point.Y < 0 || point.X > ColumnsScroller.ActualWidth || point.Y > ColumnsScroller.ActualHeight)
        {
            StopAutoScroll();
            return;
        }

        _verticalDepth = 0;
        _verticalScroller = null;
        UpdateAutoScrollTimer();
    }

    /// <summary>How deep (0..1) the pointer is in the leading (negative) or trailing (positive) edge zone.</summary>
    private static double EdgeDepth(double position, double extent)
    {
        if (extent <= 2 * AutoScrollZone)
        {
            return 0;
        }

        if (position < AutoScrollZone)
        {
            return -Math.Min(1, (AutoScrollZone - position) / AutoScrollZone);
        }

        return position > extent - AutoScrollZone ? Math.Min(1, (position - (extent - AutoScrollZone)) / AutoScrollZone) : 0;
    }

    private void SetHorizontalDepth(double depth)
    {
        if (Math.Sign(depth) != Math.Sign(_horizontalDepth))
        {
            _pageProgress = 0;
        }

        _horizontalDepth = depth;
    }

    private void UpdateAutoScrollTimer()
    {
        if (_horizontalDepth == 0 && _verticalDepth == 0)
        {
            _autoScrollTimer?.Stop();
            return;
        }

        if (_autoScrollTimer is null)
        {
            _autoScrollTimer = DispatcherQueue.CreateTimer();
            _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _autoScrollTimer.IsRepeating = true;
            _autoScrollTimer.Tick += OnAutoScrollTick;
        }

        if (!_autoScrollTimer.IsRunning)
        {
            _autoScrollTimer.Start();
        }
    }

    private void OnAutoScrollTick(DispatcherQueueTimer sender, object args)
    {
        var moving = false;
        if (_horizontalDepth != 0)
        {
            var scrolled = _isSmall ? PageColumn() : ScrollBy(ColumnsScroller, _horizontalDepth * AutoScrollMaxStep, horizontal: true);
            if (scrolled)
            {
                // The lists moved under the pointer; the next DragOver picks the list now under it.
                _verticalDepth = 0;
                _verticalScroller = null;
            }

            moving |= scrolled;
        }

        if (_verticalDepth != 0 && _verticalScroller is { } scroller)
        {
            moving |= ScrollBy(scroller, _verticalDepth * AutoScrollMaxStep, horizontal: false);
        }

        if (!moving)
        {
            sender.Stop();
        }
    }

    private static bool ScrollBy(ScrollViewer scroller, double delta, bool horizontal)
    {
        var offset = horizontal ? scroller.HorizontalOffset : scroller.VerticalOffset;
        var target = Math.Clamp(offset + delta, 0, horizontal ? scroller.ScrollableWidth : scroller.ScrollableHeight);
        if (Math.Abs(target - offset) < 0.5)
        {
            return false;
        }

        // Per-tick steps without animation give a continuous scroll at the tick rate.
        return horizontal
            ? scroller.ChangeView(target, null, null, disableAnimation: true)
            : scroller.ChangeView(null, target, null, disableAnimation: true);
    }

    // At S the scroller snaps to one column per viewport (§4.2), so a free offset would snap back; dwelling in
    // the edge zone pages one column instead, sooner the deeper the pointer is.
    private bool PageColumn()
    {
        _pageProgress += Math.Abs(_horizontalDepth);
        if (_pageProgress < SmallPageThreshold)
        {
            return true;
        }

        _pageProgress = 0;
        var command = _horizontalDepth < 0 ? ViewModel.PreviousColumnCommand : ViewModel.NextColumnCommand;
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private void StopAutoScroll()
    {
        _autoScrollTimer?.Stop();
        _horizontalDepth = 0;
        _verticalDepth = 0;
        _verticalScroller = null;
        _pageProgress = 0;
    }

    private static ScrollViewer? FindScroller(DependencyObject element)
    {
        // A ListView's template places its ScrollViewer within the first few levels.
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if ((child as ScrollViewer ?? FindScroller(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static BoardCardContainer? FindContainer(DependencyObject? element) => FindAncestor<BoardCardContainer>(element);

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return element as T;
    }
}
