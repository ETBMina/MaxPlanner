using System.Collections.Specialized;
using System.ComponentModel;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// The Grid page view. Code-behind handles view mechanics only (AGENTS.md §4): the header cells (sort, resize,
/// reorder, column chooser), the keyboard and pointer state machine of the cells (design §7.4), opening flyout
/// editors, the frozen first column at S, focus and scroll (§7.7), and the error/conflict popup. The menus and the
/// column chooser are built by <see cref="GridMenus"/> and the flyout editors by <see cref="GridEditorFlyout"/>.
/// Every decision about saving and layout belongs to <see cref="GridPageViewModel"/>.
/// </summary>
public sealed partial class GridPageView : UserControl, IGridCellHost, Shell.IPageView
{
    public static readonly DependencyProperty PopupRowProperty = DependencyProperty.Register(
        nameof(PopupRow), typeof(GridRowViewModel), typeof(GridPageView), new PropertyMetadata(null));

    public static readonly DependencyProperty PopupHasConflictProperty = DependencyProperty.Register(
        nameof(PopupHasConflict), typeof(bool), typeof(GridPageView), new PropertyMetadata(false));

    public static readonly DependencyProperty PopupSavedNowTextProperty = DependencyProperty.Register(
        nameof(PopupSavedNowText), typeof(string), typeof(GridPageView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PopupDraftTextProperty = DependencyProperty.Register(
        nameof(PopupDraftText), typeof(string), typeof(GridPageView), new PropertyMetadata(string.Empty));

    private const double ResizeStep = 16;

    private readonly List<GridHeaderCell> _headerCells = [];
    private readonly List<GridColumnViewModel> _headerColumns = [];
    private readonly GridHoverTracker _hoverTracker;
    private List<GridColumnViewModel> _displayColumns = [];
    private bool _isSmall;
    private double _frozenOffset;
    private string? _draggedColumn;
    private GridEditorFlyout? _editorFlyout;
    private (GridRowViewModel Row, GridColumnViewModel Column)? _hiddenFlyoutTarget;
    private bool _isShown;
    private ScrollViewer? _rowScroller;
    private bool _isRowThumbDragging;

    public GridPageView(GridPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Table.Host = this;
        _hoverTracker = new GridHoverTracker(Table);
        ViewModel.LayoutChanged += (_, _) => RefreshLayout();
        ((INotifyCollectionChanged)ViewModel.Rows).CollectionChanged += OnRowsChanged;
        // A hidden page ignores its width of 0: switching to S and back would rebuild every row.
        PageLayout.OnWidthChanged(this, ApplyBreakpoint);

        // Runs when the dataset closes and the view is dropped; a page switch only hides the view (OnHidden).
        Unloaded += (_, _) =>
        {
            CellPopup.IsOpen = false;
            _hoverTracker.Clear();
        };
        RefreshLayout();
    }

    public event EventHandler? DisplayLayoutChanged;

    public GridPageViewModel ViewModel { get; }

    public GridRowViewModel? PopupRow
    {
        get => (GridRowViewModel?)GetValue(PopupRowProperty);
        private set => SetValue(PopupRowProperty, value);
    }

    public bool PopupHasConflict
    {
        get => (bool)GetValue(PopupHasConflictProperty);
        private set => SetValue(PopupHasConflictProperty, value);
    }

    public string PopupSavedNowText
    {
        get => (string)GetValue(PopupSavedNowTextProperty);
        private set => SetValue(PopupSavedNowTextProperty, value);
    }

    public string PopupDraftText
    {
        get => (string)GetValue(PopupDraftTextProperty);
        private set => SetValue(PopupDraftTextProperty, value);
    }

    GridPageViewModel? IGridCellHost.GridViewModel => ViewModel;

    GridTable? IGridCellHost.Table => Table;

    IReadOnlyList<GridHeaderCell> IGridCellHost.HeaderCells => _headerCells;

    IReadOnlyList<GridColumnViewModel> IGridCellHost.DisplayColumns => _displayColumns;

    double IGridCellHost.FrozenOffset => _frozenOffset;

    bool IGridCellHost.IsFirstColumnFrozen => IsFrozen;

    /// <summary>Design §4.3 S: the Title column is first and frozen while the rest scroll horizontally.</summary>
    private bool IsFrozen => _isSmall && _displayColumns.Count > 1 && _displayColumns[0].Key == GridColumns.Title;

    /// <summary>Gives the page title keyboard focus after navigation (design §7.7).</summary>
    /// <inheritdoc/>
    public FrameworkElement HeaderElement => HeaderRoot;

    /// <inheritdoc/>
    public FrameworkElement? ContentElement => PageContent;

    public void FocusTitle() => HeaderRoot.FocusTitle();

    /// <inheritdoc/>
    public bool FocusSearch()
    {
        SearchBox.FocusSearch();
        return true;
    }

    /// <inheritdoc/>
    public void OnShown()
    {
        _isShown = true;
        PageContent.OnPageShown();
        var showsPopup = PopupRow is { IsEditing: true, HasError: true };
        var flyout = _hiddenFlyoutTarget;
        _hiddenFlyoutTarget = null;
        var reopensFlyout = flyout is { } target && target.Row.EditingColumn == target.Column.Key;
        if (!showsPopup && !reopensFlyout)
        {
            return;
        }

        // The popup and the flyout are placed against their cell, which has no position until the page is laid out.
        UpdateLayout();
        if (showsPopup)
        {
            ShowPopup(PopupRow!);
        }

        if (reopensFlyout)
        {
            // Low priority: after the shell's title focus, so the editor ends with focus and is not light-dismissed.
            var (row, column) = flyout!.Value;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_isShown && row.EditingColumn == column.Key && _editorFlyout is null)
                {
                    OpenEditorFlyout(row, column);
                }
            });
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// An inline editor stays in its cell: the shell's title focus makes it lose focus, which attempts the commit as
    /// leaving the cell does (B-07); a refused draft stays with its error popup for the return. An open flyout editor
    /// is closed without commit or cancel, so its draft and edit state are kept, and it is reopened by OnShown.
    /// </remarks>
    public void OnHidden()
    {
        _isShown = false;
        CellPopup.IsOpen = false;
        _hoverTracker.Clear();
        if (_editorFlyout is { IsOpen: true } open)
        {
            _hiddenFlyoutTarget = (open.Row, open.Column);
            open.HideKeepingEdit();
        }

        PageContent.OnPageHidden();
    }

    // ----- Layout: displayed columns, header cells, frozen column -----

    private void RefreshLayout()
    {
        var visible = ViewModel.VisibleColumns.ToList();
        if (_isSmall && visible.FirstOrDefault(c => c.Key == GridColumns.Title) is { } title)
        {
            visible.Remove(title);
            visible.Insert(0, title);
        }

        _displayColumns = visible;
        RebuildHeader();
        DisplayLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildHeader()
    {
        foreach (var column in _headerColumns)
        {
            column.PropertyChanged -= OnHeaderColumnChanged;
        }

        _headerColumns.Clear();
        _headerCells.Clear();
        HeaderRow.Children.Clear();
        foreach (var column in _displayColumns)
        {
            var cell = CreateHeaderCell(column);
            _headerCells.Add(cell);
            _headerColumns.Add(column);
            column.PropertyChanged += OnHeaderColumnChanged;
            HeaderRow.Children.Add(cell);
        }

        HeaderRow.Update(_displayColumns, _frozenOffset, IsFrozen);
    }

    private GridHeaderCell CreateHeaderCell(GridColumnViewModel column)
    {
        var cell = new GridHeaderCell { Column = column.Key, Padding = new Thickness(8, 0, 0, 0), CanDrag = true, AllowDrop = true };
        var text = new TextBlock { Text = column.Header, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["TableHeaderTextStyle"] };
        var glyph = new FontIcon { FontSize = 12, Glyph = SortGlyph(column.Sort), Visibility = column.Sort == GridSortDirection.None ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
        var grip = GridHeaderGrip.Create(ResourceStrings.Shared.Format("Grid_ResizeColumnName", column.Header), delta => ViewModel.SetWidth(column.Key, column.Width + delta));
        var layout = new WinGrid { ColumnSpacing = 4 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        WinGrid.SetColumn(glyph, 1);
        WinGrid.SetColumn(grip, 2);
        layout.Children.Add(text);
        layout.Children.Add(glyph);
        layout.Children.Add(grip);
        cell.Content = layout;
        cell.Style = IsFrozen && column == _displayColumns[0] ? (Style)Microsoft.UI.Xaml.Application.Current.Resources["GridFrozenCellStyle"] : null;
        AutomationProperties.SetName(cell, column.AccessibleName);
        ToolTipService.SetToolTip(cell, column.Header);
        cell.SortRequested += (_, _) => ViewModel.CycleSort(column.Key);
        cell.KeyDown += (_, e) => OnHeaderKeyDown(column, e);
        cell.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            GridMenus.HeaderMenu(ViewModel, column, _displayColumns.IndexOf(column), _displayColumns.Count).ShowAt(cell);
        };

        // Design §4.3: drag a header onto another to move the column there.
        cell.DragStarting += (_, args) =>
        {
            _draggedColumn = column.Key;
            args.Data.SetText(column.Header);
            args.Data.RequestedOperation = DataPackageOperation.Move;
        };
        cell.DragOver += (_, args) =>
        {
            if (_draggedColumn is { } dragged && dragged != column.Key)
            {
                args.AcceptedOperation = DataPackageOperation.Move;
                args.DragUIOverride.Caption = ResourceStrings.Shared.Format("Grid_MoveColumnCaption", column.Header);
            }
        };
        cell.Drop += (_, _) =>
        {
            if (_draggedColumn is { } dragged && dragged != column.Key)
            {
                var keys = ViewModel.VisibleKeys.ToList();
                ViewModel.MoveColumn(dragged, keys.IndexOf(column.Key) - keys.IndexOf(dragged));
            }

            _draggedColumn = null;
        };
        cell.DropCompleted += (_, _) => _draggedColumn = null;
        return cell;
    }

    private void OnHeaderColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GridColumnViewModel column)
        {
            return;
        }

        var index = _headerColumns.IndexOf(column);
        if (index < 0)
        {
            return;
        }

        var cell = _headerCells[index];
        switch (e.PropertyName)
        {
            case nameof(GridColumnViewModel.Width):
                HeaderRow.InvalidateMeasure();
                break;
            case nameof(GridColumnViewModel.Sort) or nameof(GridColumnViewModel.AccessibleName) or nameof(GridColumnViewModel.Header):
                AutomationProperties.SetName(cell, column.AccessibleName);
                if (cell.Content is WinGrid { Children: [TextBlock text, FontIcon glyph, ..] })
                {
                    text.Text = column.Header;
                    glyph.Glyph = SortGlyph(column.Sort);
                    glyph.Visibility = column.Sort == GridSortDirection.None ? Visibility.Collapsed : Visibility.Visible;
                }

                break;
            default:
                break;
        }
    }

    private void OnHeaderKeyDown(GridColumnViewModel column, KeyRoutedEventArgs e)
    {
        var alt = GridInput.IsDown(VirtualKey.Menu);
        var ctrlShift = GridInput.IsDown(VirtualKey.Control) && GridInput.IsDown(VirtualKey.Shift);
        switch (e.Key)
        {
            // Design §7.4: Enter sorts; Alt+Left/Right resizes by 16 epx; Ctrl+Shift+Left/Right moves the column.
            case VirtualKey.Enter or VirtualKey.Space:
                e.Handled = true;
                ViewModel.CycleSort(column.Key);
                break;
            case VirtualKey.Left or VirtualKey.Right when alt:
                e.Handled = true;
                ViewModel.SetWidth(column.Key, column.Width + (e.Key == VirtualKey.Right ? ResizeStep : -ResizeStep));
                break;
            case VirtualKey.Left or VirtualKey.Right when ctrlShift:
                e.Handled = true;
                if (ViewModel.MoveColumn(column.Key, e.Key == VirtualKey.Right ? 1 : -1))
                {
                    FocusHeader(column.Key);
                }

                break;
            case VirtualKey.Left or VirtualKey.Right:
                e.Handled = true;
                var index = _displayColumns.IndexOf(column) + (e.Key == VirtualKey.Right ? 1 : -1);
                if (index >= 0 && index < _displayColumns.Count)
                {
                    FocusHeader(_displayColumns[index].Key);
                }

                break;
            case VirtualKey.Down when ViewModel.Rows.Count > 0:
                e.Handled = true;
                FocusCell(ViewModel.Rows[0], column.Key);
                break;
            default:
                break;
        }
    }

    private void FocusHeader(string key) => _headerCells.FirstOrDefault(h => h.Column == key)?.Focus(FocusState.Keyboard);

    private static string SortGlyph(GridSortDirection sort) => sort == GridSortDirection.Descending ? "" : "";

    // Column chooser (design §4.3): a CheckBox per column plus Reset; visibility never changes the results (B-10).
    private void OnColumnsFlyoutOpening(object? sender, object e) => ColumnsFlyout.Content = GridMenus.ColumnChooser(ViewModel, ColumnsFlyout.Hide);

    // ----- Viewport-pinned row scroll bar (mirrors the row list's own scroller, whose bar is hidden) -----

    private void OnTableLoaded(object sender, RoutedEventArgs e)
    {
        if (_rowScroller is not null || FindDescendant<ScrollViewer>(Table, _ => true) is not { } scroller)
        {
            return;
        }

        // The page owns both the list and its template scroller, so these handlers share the page's lifetime.
        _rowScroller = scroller;
        scroller.ViewChanged += (_, _) =>
        {
            // Rows move under a still pointer while scrolling; the hover returns on the next pointer move.
            _hoverTracker.Clear();
            SyncRowScrollBar();
        };
        scroller.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, (_, _) => SyncRowScrollBar());
        scroller.RegisterPropertyChangedCallback(ScrollViewer.ViewportHeightProperty, (_, _) => SyncRowScrollBar());
        HeaderBorder.SizeChanged += (_, args) => RowScrollBar.Margin = new Thickness(0, args.NewSize.Height, 0, 0);

        // The bar sits outside the table, so a wheel turned over it is forwarded: the normal wheel to the rows, and the
        // tilt wheel or Shift+wheel to the table's horizontal scroller (design §9 Mouse wheel).
        RowScrollBar.PointerWheelChanged += (_, args) =>
        {
            var properties = args.GetCurrentPoint(RowScrollBar).Properties;
            if (properties.IsHorizontalMouseWheel || (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0)
            {
                Common.WheelScrolling.ScrollByWheel(HorizontalScroller, Orientation.Horizontal, properties);
            }
            else
            {
                Common.WheelScrolling.ScrollByWheel(scroller, Orientation.Vertical, properties);
            }

            args.Handled = true;
        };
        RowScrollBar.Margin = new Thickness(0, HeaderBorder.ActualHeight, 0, 0);
        SyncRowScrollBar();
    }

    private void SyncRowScrollBar()
    {
        if (_rowScroller is not { } scroller)
        {
            return;
        }

        var scrollable = scroller.ScrollableHeight;
        RowScrollBar.Visibility = scrollable > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        RowScrollBar.Maximum = Math.Max(0, scrollable);
        RowScrollBar.ViewportSize = scroller.ViewportHeight;
        RowScrollBar.SmallChange = GridColumnLayout.RowHeight;
        RowScrollBar.LargeChange = Math.Max(GridColumnLayout.RowHeight, scroller.ViewportHeight - GridColumnLayout.RowHeight);

        // While the thumb is dragged the bar leads; echoing the trailing offsets back would make the thumb jitter.
        if (!_isRowThumbDragging)
        {
            RowScrollBar.Value = scroller.VerticalOffset;
        }
    }

    private void OnRowScrollBarScroll(object sender, ScrollEventArgs e)
    {
        _isRowThumbDragging = e.ScrollEventType == ScrollEventType.ThumbTrack;
        if (e.ScrollEventType == ScrollEventType.EndScroll)
        {
            SyncRowScrollBar();
            return;
        }

        _rowScroller?.ChangeView(null, e.NewValue, null, disableAnimation: true);
    }

    private void OnHorizontalViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _hoverTracker.Clear();
        var offset = IsFrozen ? HorizontalScroller.HorizontalOffset : 0;
        if (Math.Abs(offset - _frozenOffset) < 0.5)
        {
            return;
        }

        _frozenOffset = offset;
        HeaderRow.Update(_displayColumns, _frozenOffset, IsFrozen);
        if (Table.ItemsPanelRoot is { } panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is GridRowContainer { ContentTemplateRoot: GridRowPresenter presenter })
                {
                    presenter.InvalidateArrange();
                }
            }
        }
    }

    // ----- Cells -----

    void IGridCellHost.OnCellPreviewKeyDown(GridCell cell, KeyRoutedEventArgs e)
    {
        if (cell.Row is not { } row || row.EditingColumn != cell.Column)
        {
            return;
        }

        // Editing keys are handled while tunnelling, so a ComboBox editor cannot swallow Enter (ADR 0004 row 2).
        if (row.IsSaving && e.Key is VirtualKey.Enter or VirtualKey.Tab or VirtualKey.Escape)
        {
            e.Handled = true;
            return;
        }

        var popupOpen = FindEditor(cell) is ComboBox { IsDropDownOpen: true } or CalendarDatePicker { IsCalendarOpen: true };
        switch (e.Key)
        {
            case VirtualKey.Enter when !popupOpen:
                e.Handled = true;
                CommitAsync(cell, row, move: 0, restoreFocus: true).Forget();
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                CommitAsync(cell, row, move: GridInput.IsDown(VirtualKey.Shift) ? -1 : 1, restoreFocus: true).Forget();
                break;
            case VirtualKey.Escape when !popupOpen:
                e.Handled = true;
                var column = cell.Column;
                ViewModel.CancelEdit(row);
                ClosePopup();
                FocusCell(row, column);
                break;
            default:
                break;
        }
    }

    void IGridCellHost.OnCellKeyDown(GridCell cell, KeyRoutedEventArgs e)
    {
        if (cell.Row is not { } row || row.IsEditing)
        {
            return;
        }

        var keys = _displayColumns.Select(c => c.Key).ToList();
        var index = keys.IndexOf(cell.Column);
        var rowIndex = ViewModel.Rows.IndexOf(row);
        switch (e.Key)
        {
            case VirtualKey.F2:
                e.Handled = true;
                BeginEditAndFocus(row, cell.Column, null);
                break;
            case VirtualKey.Left when index > 0:
                e.Handled = true;
                FocusCell(row, keys[index - 1]);
                break;
            case VirtualKey.Right when index < keys.Count - 1:
                e.Handled = true;
                FocusCell(row, keys[index + 1]);
                break;
            case VirtualKey.Home:
                e.Handled = true;
                FocusCell(GridInput.IsDown(VirtualKey.Control) && ViewModel.Rows.Count > 0 ? ViewModel.Rows[0] : row, keys[0]);
                break;
            case VirtualKey.End:
                e.Handled = true;
                FocusCell(GridInput.IsDown(VirtualKey.Control) && ViewModel.Rows.Count > 0 ? ViewModel.Rows[^1] : row, keys[^1]);
                break;
            case VirtualKey.Up when rowIndex > 0:
                e.Handled = true;
                FocusCell(ViewModel.Rows[rowIndex - 1], cell.Column);
                break;
            case VirtualKey.Up when rowIndex == 0:
                e.Handled = true;
                FocusHeader(cell.Column);
                break;

            // Design §7.4: Enter on a committed cell moves down.
            case VirtualKey.Down or VirtualKey.Enter when rowIndex >= 0 && rowIndex < ViewModel.Rows.Count - 1:
                e.Handled = true;
                FocusCell(ViewModel.Rows[rowIndex + 1], cell.Column);
                break;
            case VirtualKey.PageDown or VirtualKey.PageUp when rowIndex >= 0:
                e.Handled = true;
                var page = Math.Max(1, (int)(Table.ActualHeight / GridColumnLayout.RowHeight) - 1);
                var target = Math.Clamp(rowIndex + (e.Key == VirtualKey.PageDown ? page : -page), 0, ViewModel.Rows.Count - 1);
                FocusCell(ViewModel.Rows[target], cell.Column);
                break;

            // Design §7.1: Delete… (always confirmed) and Ctrl+D Copy… for the focused record.
            case VirtualKey.Delete:
                e.Handled = true;
                ViewModel.DeleteRowAsync(row).Forget();
                break;
            case VirtualKey.D when GridInput.IsDown(VirtualKey.Control) && row.Model.Task is not null:
                e.Handled = true;
                ViewModel.CopyRowAsync(row).Forget();
                break;
            default:
                break;
        }
    }

    void IGridCellHost.OnCellCharacterReceived(GridCell cell, CharacterReceivedRoutedEventArgs e)
    {
        // Design §4.3: typing a character on a text cell starts an edit that replaces the value.
        if (cell.Row is not { } row || row.IsEditing || char.IsControl(e.Character) || !StartsByTyping(cell.Column))
        {
            return;
        }

        if (row.CanEdit(cell.Column))
        {
            e.Handled = true;
            BeginEditAndFocus(row, cell.Column, e.Character.ToString());
        }
    }

    void IGridCellHost.OnCellDoubleTapped(GridCell cell)
    {
        if (cell.Row is { IsEditing: false } row)
        {
            BeginEditAndFocus(row, cell.Column, null);
        }
    }

    void IGridCellHost.OnCellTapped(GridCell cell) => TapCellAsync(cell).Forget();

    private async Task TapCellAsync(GridCell cell)
    {
        // Same path as double-tap and F2 (BeginEdit via BeginEditAndFocus), so no second business-rule route exists.
        // Text cells keep focus-then-double-click; read-only cells and a blocked store do nothing, not even an error.
        if (cell.Row is not { IsSaving: false } row
            || row.EditingColumn == cell.Column
            || ViewModel.IsEditingBlocked
            || !row.CanEdit(cell.Column)
            || _displayColumns.FirstOrDefault(c => c.Key == cell.Column) is not { } tapped
            || !GridCellContent.OpensOnTap(tapped))
        {
            return;
        }

        try
        {
            // B-07: clicking another cell leaves the open edit, which commits it first; a refused commit keeps
            // that editor (and its error) open instead of opening the tapped one.
            foreach (var editing in ViewModel.Rows.Where(r => r.IsEditing).ToList())
            {
                if (editing.IsSaving || editing.EditingColumn is not { } open || FindCell(editing, open) is not { } openCell)
                {
                    return;
                }

                await CommitAsync(openCell, editing, move: 0, restoreFocus: false);
                if (editing.IsEditing)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            row.ShowError(ResourceStrings.Shared.GetString("Error_Unexpected"));
            ShowPopup(row);
            return;
        }

        if (row.IsEditing || row.IsSaving
            || ViewModel.IsEditingBlocked
            || !row.CanEdit(cell.Column)
            || _displayColumns.FirstOrDefault(c => c.Key == cell.Column) is not { } column
            || !GridCellContent.OpensOnTap(column))
        {
            return;
        }

        BeginEditAndFocus(row, cell.Column, null, openOnTap: true);
    }

    void IGridCellHost.BeginEditFromAutomation(GridCell cell)
    {
        if (cell.Row is { } row && row.EditingColumn != cell.Column)
        {
            BeginEditAndFocus(row, cell.Column, null);
        }
    }

    void IGridCellHost.SetValueFromAutomation(GridCell cell, string value)
    {
        if (cell.Row is not { } row)
        {
            return;
        }

        var column = cell.Column;
        if (row.EditingColumn != column && !ViewModel.BeginEdit(row, column))
        {
            throw new InvalidOperationException(row.ErrorMessage ?? ResourceStrings.Shared.GetString("Grid_ValueError_CannotEdit"));
        }

        row.SetDraftFromText(value, ViewModel.StatusOptions, ViewModel.PriorityOptions);
        CommitAsync(cell, row, move: 0, restoreFocus: true).Forget();
    }

    void IGridCellHost.OnEditorLostFocus(Control editor)
    {
        if (editor.DataContext is not GridRowViewModel row || FindAncestor<GridCell>(editor) is not { } cell)
        {
            return;
        }

        // B-07: leaving an edited cell attempts a commit, except while the outcome is pending, while a conflict
        // waits for the user's choice, while a drop-down or calendar is open, or when the same draft was just refused.
        if (row.EditingColumn != cell.Column || row.IsSaving || row.HasConflict
            || editor is ComboBox { IsDropDownOpen: true } or CalendarDatePicker { IsCalendarOpen: true })
        {
            return;
        }

        if (row.FailedDraft is { } failed && string.Equals(failed, row.DraftValue, StringComparison.Ordinal))
        {
            return;
        }

        if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused
            && (IsWithin(focused, cell) || (CellPopup.Child is { } popupContent && IsWithin(focused, popupContent)) || IsInPopup(focused)))
        {
            return;
        }

        CommitAsync(cell, row, move: 0, restoreFocus: false).Forget();
    }

    void IGridCellHost.OnRowMenuRequested(GridRowViewModel row, FrameworkElement anchor) => GridMenus.RowMenu(ViewModel, row).ShowAt(anchor);

    private static bool StartsByTyping(string column) => column is GridColumns.Title or GridColumns.Subtitle or GridColumns.Service || GridColumns.IsCustom(column);

    private void BeginEditAndFocus(GridRowViewModel row, string column, string? initialText, bool openOnTap = false)
    {
        if (!ViewModel.BeginEdit(row, column, initialText))
        {
            if (row.HasError)
            {
                ShowPopup(row);
            }

            return;
        }

        ClosePopup();
        if (_displayColumns.FirstOrDefault(c => c.Key == column) is { UsesFlyout: true } flyoutColumn)
        {
            OpenEditorFlyout(row, flyoutColumn, openedByTap: openOnTap);
            return;
        }

        FocusEditorSoon(row, column, selectAll: initialText is null, openPicker: openOnTap);
    }

    /// <summary>
    /// Commits the open editor. On success focus returns to the cell (Enter) or moves to the next editable
    /// cell (Tab), but only when the commit was started from the keyboard or UIA; leaving the cell with the
    /// pointer keeps focus where the user put it.
    /// </summary>
    private async Task CommitAsync(GridCell cell, GridRowViewModel row, int move, bool restoreFocus)
    {
        var column = row.EditingColumn ?? cell.Column;
        if (await ViewModel.CommitAsync(row))
        {
            if (ReferenceEquals(PopupRow, row))
            {
                ClosePopup();
            }

            if (restoreFocus)
            {
                if (move == 0 || !FocusNextEditable(row, column, move))
                {
                    FocusCell(row, column);
                }
            }

            return;
        }

        if (row.IsEditing && row.HasError)
        {
            ShowPopup(row);
            GridCellAutomationPeer.AnnounceError(FindCell(row, column) ?? cell, row.ErrorMessage!);
            if (restoreFocus)
            {
                FocusEditorSoon(row, column, selectAll: false);
            }
        }
    }

    // ----- Flyout editors (design §4.3: Notes with Save Ctrl+Enter / Cancel; people and tag pickers; list fields) -----

    private void OpenEditorFlyout(GridRowViewModel row, GridColumnViewModel column, bool openedByTap = false)
    {
        if (FindCell(row, column.Key) is not { } cell)
        {
            ViewModel.CancelEdit(row);
            return;
        }

        var flyout = new GridEditorFlyout(ViewModel, cell, row, column, openedByTap, FocusCell, OnEditorFlyoutClosed);
        _editorFlyout = flyout;
        flyout.Show();
    }

    private void OnEditorFlyoutClosed(GridEditorFlyout flyout)
    {
        if (ReferenceEquals(_editorFlyout, flyout))
        {
            _editorFlyout = null;
        }
    }

    // ----- Popup, focus, and helpers -----

    private void OnUseMineClick(object sender, RoutedEventArgs e)
    {
        if (PopupRow is { EditingColumn: { } column } row)
        {
            UseMineAsync(row, column).Forget();
        }
    }

    private async Task UseMineAsync(GridRowViewModel row, string column)
    {
        if (await ViewModel.UseMineAsync(row))
        {
            ClosePopup();
            FocusCell(row, column);
        }
        else if (row.HasError)
        {
            ShowPopup(row);
            FocusEditorSoon(row, column, selectAll: false);
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (PopupRow is { EditingColumn: { } column } row)
        {
            ViewModel.Reload(row);
            ClosePopup();
            FocusCell(row, column);
        }
    }

    /// <summary>
    /// Design §7.7: when a change set moves the row that holds focus, the list scrolls just enough to keep
    /// it in view (ScrollIntoView with default alignment does nothing for a row that is already visible).
    /// </summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Remove)
            || XamlRoot is null
            || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused
            || !IsWithin(focused, Table)
            || FindAncestor<GridCell>(focused)?.Row is not { } row)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => Table.ScrollIntoView(row));
    }

    private void ShowPopup(GridRowViewModel row)
    {
        if (row.EditingColumn is not { } column || FindCell(row, column) is not { } cell)
        {
            return;
        }

        // A flyout editor shows its own error inside the flyout.
        if (_editorFlyout is { IsOpen: true })
        {
            return;
        }

        if (!ReferenceEquals(PopupRow, row))
        {
            if (PopupRow is { } previous)
            {
                previous.PropertyChanged -= OnPopupRowChanged;
            }

            PopupRow = row;
            row.PropertyChanged += OnPopupRowChanged;
        }

        RefreshPopupText(row);
        CellPopup.PlacementTarget = cell;

        // A hidden page keeps the popup closed; OnShown opens it again for an edit that still has its error.
        CellPopup.IsOpen = IsLoaded && _isShown;
    }

    private void ClosePopup()
    {
        CellPopup.IsOpen = false;
        if (PopupRow is { } row)
        {
            row.PropertyChanged -= OnPopupRowChanged;
        }

        PopupRow = null;
    }

    private void OnPopupRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GridRowViewModel row)
        {
            return;
        }

        if (!row.IsEditing || (!row.HasError && !row.IsSaving))
        {
            ClosePopup();
            return;
        }

        RefreshPopupText(row);
    }

    private void RefreshPopupText(GridRowViewModel row)
    {
        PopupHasConflict = row.HasConflict;
        PopupSavedNowText = row.ConflictSavedText is { } saved ? ViewModel.FormatSavedNow(saved) : string.Empty;
        PopupDraftText = ViewModel.FormatYourDraft(row.DraftValue);
    }

    private void FocusEditorSoon(GridRowViewModel row, string column, bool selectAll, bool openPicker = false)
    {
        // The editor is created when the row's editing state changes; focus it after that layout pass.
        Table.ScrollIntoView(row);
        DispatcherQueue.TryEnqueue(() =>
        {
            Table.UpdateLayout();
            if (FindCell(row, column) is not { } cell || FindEditor(cell) is not { } editor)
            {
                return;
            }

            editor.Focus(FocusState.Keyboard);
            if (openPicker)
            {
                OpenPicker(row, editor);
            }

            if (editor is TextBox text)
            {
                if (selectAll)
                {
                    text.SelectAll();
                }
                else
                {
                    text.Select(text.Text.Length, 0);
                }
            }
        });
    }

    /// <summary>
    /// Opens a ComboBox list or calendar for a single-tap edit. The second click of a double-click lands on the
    /// popup's light-dismiss layer and would close the list just opened, so the first close inside the
    /// double-click window with the choice unchanged (and no Esc) reopens it once.
    /// </summary>
    private void OpenPicker(GridRowViewModel row, Control editor)
    {
        var openedAt = Environment.TickCount64;
        var column = row.EditingColumn;
        bool IsStrayClose() => GridInput.IsWithinDoubleClickWindow(openedAt) && row.EditingColumn == column && !row.IsSaving && !GridInput.IsDown(VirtualKey.Escape);

        switch (editor)
        {
            case ComboBox combo:
                var selected = combo.SelectedItem;
                EventHandler<object>? onDropDownClosed = null;
                onDropDownClosed = (_, _) =>
                {
                    combo.DropDownClosed -= onDropDownClosed;
                    if (IsStrayClose() && ReferenceEquals(combo.SelectedItem, selected))
                    {
                        DispatcherQueue.TryEnqueue(() => combo.IsDropDownOpen = combo.IsLoaded);
                    }
                };
                combo.DropDownClosed += onDropDownClosed;
                combo.IsDropDownOpen = true;
                break;
            case CalendarDatePicker picker:
                var date = picker.Date;
                EventHandler<object>? onCalendarClosed = null;
                onCalendarClosed = (_, _) =>
                {
                    picker.Closed -= onCalendarClosed;
                    if (IsStrayClose() && picker.Date == date)
                    {
                        DispatcherQueue.TryEnqueue(() => picker.IsCalendarOpen = picker.IsLoaded);
                    }
                };
                picker.Closed += onCalendarClosed;
                picker.IsCalendarOpen = true;
                break;
            default:
                break;
        }
    }

    private void FocusCell(GridRowViewModel row, string column)
    {
        Table.ScrollIntoView(row);
        Table.UpdateLayout();
        FindCell(row, column)?.Focus(FocusState.Keyboard);
    }

    private bool FocusNextEditable(GridRowViewModel row, string column, int direction)
    {
        var keys = _displayColumns.Select(c => c.Key).ToList();
        for (var i = keys.IndexOf(column) + direction; i >= 0 && i < keys.Count; i += direction)
        {
            if (row.CanEdit(keys[i]))
            {
                FocusCell(row, keys[i]);
                return true;
            }
        }

        return false;
    }

    private GridCell? FindCell(GridRowViewModel row, string column) =>
        Table.ContainerFromItem(row) is ListViewItem container ? GridTableAutomationPeer.FindCell(container, column) : null;

    private void ApplyBreakpoint(double width)
    {
        // T-08.1 page padding: L 32/24, M 24/16, S 16/12 (design §2.3). The header and the rows share one inset so
        // every header sits above its column's cells.
        var window = PagePadding.WindowWidth(this, width);
        var inset = PagePadding.Side(window);
        HeaderRoot.ApplyWidth(width, window);
        HeaderBorder.Padding = new Thickness(inset, 0, 0, 0);
        Table.Padding = new Thickness(inset, 0, 0, 0);

        // Design §4.3 S: only New task keeps its label inline; the CommandBar overflows the rest by itself.
        NewTaskContent.IsTextVisible = width >= 500;
        var small = width < Breakpoints.Medium;
        ChipsBar.IsCompact = small;
        if (small != _isSmall)
        {
            _isSmall = small;
            _frozenOffset = 0;
            RefreshLayout();
        }
    }

    private static Control? FindEditor(DependencyObject cell) =>
        FindDescendant<Control>(cell, c => c is TextBox or ComboBox or CalendarDatePicker or NumberBox);

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    // An element in an open popup (a ComboBox list or a calendar) belongs to the editor that opened it.
    private bool IsInPopup(DependencyObject element) =>
        XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(p => p.Child is { } child && IsWithin(element, child) && !ReferenceEquals(p, CellPopup));

    private static T? FindAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match))
            {
                return match;
            }

            if (FindDescendant(child, predicate) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // ----- Row menu (design §4.3; its items are built by GridMenus.RowMenu) -----
    private void OnRowContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as DependencyObject;
        while (element is not null and not GridRowContainer)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        if (element is not GridRowContainer { Content: GridRowViewModel row } container || row.IsEditing)
        {
            return;
        }

        args.Handled = true;
        var menu = GridMenus.RowMenu(ViewModel, row);
        if (args.TryGetPosition(container, out var point))
        {
            menu.ShowAt(container, point);
        }
        else
        {
            menu.ShowAt(container);
        }
    }
}
