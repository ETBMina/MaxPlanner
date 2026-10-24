using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Presentation.Common.Controls;
using MaxPlanner.Presentation.Common.Fields;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// The Grid page view. Code-behind handles view mechanics only (AGENTS.md §4): the header cells (sort, resize,
/// reorder, column chooser), the keyboard and pointer state machine of the cells (design §7.4), flyout editors,
/// the frozen first column at S, focus and scroll (§7.7), and the error/conflict popup. Every decision about
/// saving and layout belongs to <see cref="GridPageViewModel"/>.
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
    private static readonly Lazy<ResourceStrings> Strings = new(() => new ResourceStrings());

    private readonly List<GridHeaderCell> _headerCells = [];
    private readonly List<GridColumnViewModel> _headerColumns = [];
    private List<GridColumnViewModel> _displayColumns = [];
    private bool _isSmall;
    private double _frozenOffset;
    private string? _draggedColumn;
    private Flyout? _editorFlyout;
    private bool _flyoutCloseApproved;
    private readonly CompactSearch _search;

    public GridPageView(GridPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _search = new CompactSearch(SearchBox, SearchToggle, CompactSearchRow);
        Table.Host = this;
        ViewModel.FocusRequested += OnFocusRequested;
        ViewModel.LayoutChanged += (_, _) => RefreshLayout();
        ((INotifyCollectionChanged)ViewModel.Rows).CollectionChanged += OnRowsChanged;
        SizeChanged += (_, e) => ApplyBreakpoint(e.NewSize.Width);
        Unloaded += (_, _) => CellPopup.IsOpen = false;
        Loaded += (_, _) =>
        {
            if (PopupRow is { IsEditing: true, HasError: true } row)
            {
                ShowPopup(row);
            }
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

    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

    /// <inheritdoc/>
    public bool FocusSearch()
    {
        _search.FocusSearch();
        return true;
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
        var cell = new GridHeaderCell { Column = column.Key, Padding = new Thickness(8, 0, 0, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CanDrag = true, AllowDrop = true };
        var text = new TextBlock { Text = column.Header, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var glyph = new FontIcon { FontSize = 12, Glyph = SortGlyph(column.Sort), Visibility = column.Sort == GridSortDirection.None ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
        var grip = GridHeaderGrip.Create(Strings.Value.Format("Grid_ResizeColumnName", column.Header), delta => ViewModel.SetWidth(column.Key, column.Width + delta));
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
            HeaderMenu(column).ShowAt(cell);
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
                args.DragUIOverride.Caption = Strings.Value.Format("Grid_MoveColumnCaption", column.Header);
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
        var alt = IsDown(VirtualKey.Menu);
        var ctrlShift = IsDown(VirtualKey.Control) && IsDown(VirtualKey.Shift);
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

    private MenuFlyout HeaderMenu(GridColumnViewModel column)
    {
        var strings = Strings.Value;
        var menu = new MenuFlyout();
        void Item(string key, string id, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = strings.GetString(key), IsEnabled = enabled };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        var index = _displayColumns.IndexOf(column);
        Item("Grid_HeaderMenuSort", "Grid_HeaderMenuSortItem", () => ViewModel.CycleSort(column.Key));
        Item("Grid_HeaderMenuMoveLeft", "Grid_HeaderMenuMoveLeftItem", () => ViewModel.MoveColumn(column.Key, -1), index > 0);
        Item("Grid_HeaderMenuMoveRight", "Grid_HeaderMenuMoveRightItem", () => ViewModel.MoveColumn(column.Key, 1), index < _displayColumns.Count - 1);
        Item("Grid_HeaderMenuHide", "Grid_HeaderMenuHideItem", () => ViewModel.SetVisible(column.Key, false), _displayColumns.Count > 1);
        return menu;
    }

    private void FocusHeader(string key) => _headerCells.FirstOrDefault(h => h.Column == key)?.Focus(FocusState.Keyboard);

    private static string SortGlyph(GridSortDirection sort) => sort == GridSortDirection.Descending ? "" : "";

    // Column chooser (design §4.3): a CheckBox per column plus Reset; visibility never changes the results (B-10).
    private void OnColumnsFlyoutOpening(object? sender, object e)
    {
        var strings = Strings.Value;
        var list = new StackPanel { Spacing = 2 };
        foreach (var column in ViewModel.Columns)
        {
            var check = new CheckBox { Content = column.Header, IsChecked = column.IsVisible };
            AutomationProperties.SetAutomationId(check, "Grid_ColumnCheck_" + column.Key);
            check.Click += (_, _) =>
            {
                ViewModel.SetVisible(column.Key, check.IsChecked == true);
                check.IsChecked = column.IsVisible;
            };
            list.Children.Add(check);
        }

        var reset = new Button { Content = strings.GetString("Grid_ColumnsReset") };
        AutomationProperties.SetAutomationId(reset, "Grid_ColumnsResetButton");
        reset.Click += (_, _) =>
        {
            ViewModel.ResetLayoutCommand.Execute(null);
            ColumnsFlyout.Hide();
        };
        var heading = new TextBlock { Text = strings.GetString("Grid_ColumnsHeading"), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        var scroller = new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new StackPanel { Spacing = 8, MinWidth = 220 };
        root.Children.Add(heading);
        root.Children.Add(scroller);
        root.Children.Add(reset);
        ColumnsFlyout.Content = root;
    }

    private void OnHorizontalViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
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
                _ = CommitAsync(cell, row, move: 0, restoreFocus: true);
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                _ = CommitAsync(cell, row, move: IsDown(VirtualKey.Shift) ? -1 : 1, restoreFocus: true);
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
                FocusCell(IsDown(VirtualKey.Control) && ViewModel.Rows.Count > 0 ? ViewModel.Rows[0] : row, keys[0]);
                break;
            case VirtualKey.End:
                e.Handled = true;
                FocusCell(IsDown(VirtualKey.Control) && ViewModel.Rows.Count > 0 ? ViewModel.Rows[^1] : row, keys[^1]);
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
                _ = ViewModel.DeleteRowAsync(row);
                break;
            case VirtualKey.D when IsDown(VirtualKey.Control) && row.Model.Task is not null:
                e.Handled = true;
                _ = ViewModel.CopyRowAsync(row);
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
            throw new InvalidOperationException(row.ErrorMessage ?? "The cell can't be edited now.");
        }

        var trimmed = value.Trim();
        switch (column)
        {
            case GridColumns.Status:
                row.DraftStatus = ViewModel.StatusOptions.FirstOrDefault(o => string.Equals(o.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    ?? Refuse<StatusOption>(row, "No task status has that name.");
                break;
            case GridColumns.Priority:
                row.DraftPriority = ViewModel.PriorityOptions.FirstOrDefault(o => string.Equals(o.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    ?? Refuse<Tasks.PriorityOption>(row, "No priority has that name.");
                break;
            case GridColumns.StartDate or GridColumns.EndDate:
                if (trimmed.Length == 0)
                {
                    row.ClearDraftDate();
                }
                else if (DateOnly.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date))
                {
                    row.DraftDate = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue));
                }
                else
                {
                    Refuse<object>(row, "The value is not a date.");
                }

                break;
            case GridColumns.Designers or GridColumns.Developers or GridColumns.Tags:
                Refuse<object>(row, "Use the picker to change people or tags.");
                break;
            case GridColumns.Notes:
                row.DraftText = value;
                break;
            default:
                if (row.DraftCustom is { } custom)
                {
                    SetCustomValue(row, custom, trimmed);
                }
                else
                {
                    row.DraftText = value;
                }

                break;
        }

        _ = CommitAsync(cell, row, move: 0, restoreFocus: true);
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

        _ = CommitAsync(cell, row, move: 0, restoreFocus: false);
    }

    void IGridCellHost.OnRowMenuRequested(GridRowViewModel row, FrameworkElement anchor) => RowMenu(row).ShowAt(anchor);

    private static T Refuse<T>(GridRowViewModel row, string message)
    {
        row.ShowError(message);
        throw new ArgumentException(message);
    }

    private static void SetCustomValue(GridRowViewModel row, CustomFieldEditorViewModel custom, string value)
    {
        switch (custom.Kind)
        {
            case FieldKind.Number:
                custom.Number = value.Length == 0 ? double.NaN
                    : double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) ? number : Refuse<double>(row, "The value is not a number.");
                break;
            case FieldKind.Date:
                custom.Date = value.Length == 0 ? null
                    : DateOnly.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date) ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue))
                    : Refuse<DateTimeOffset?>(row, "The value is not a date.");
                break;
            case FieldKind.Lookup:
                custom.SelectedOption = custom.Options.FirstOrDefault(o => string.Equals(o.Label, value, StringComparison.OrdinalIgnoreCase))
                    ?? (value.Length == 0 ? custom.Options[0] : Refuse<FieldOptionChoice>(row, "No option has that name."));
                break;
            case FieldKind.People or FieldKind.Tags:
                Refuse<object>(row, "Use the picker to change people or tags.");
                break;
            default:
                custom.Text = value;
                break;
        }
    }

    private static bool StartsByTyping(string column) => column is GridColumns.Title or GridColumns.Subtitle or GridColumns.Service || GridColumns.IsCustom(column);

    private void BeginEditAndFocus(GridRowViewModel row, string column, string? initialText)
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
            OpenEditorFlyout(row, flyoutColumn);
            return;
        }

        FocusEditorSoon(row, column, selectAll: initialText is null);
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
            Announce(FindCell(row, column) ?? cell, row.ErrorMessage!);
            if (restoreFocus)
            {
                FocusEditorSoon(row, column, selectAll: false);
            }
        }
    }

    // ----- Flyout editors (design §4.3: Notes with Save Ctrl+Enter / Cancel; people and tag pickers; list fields) -----

    private void OpenEditorFlyout(GridRowViewModel row, GridColumnViewModel column)
    {
        if (FindCell(row, column.Key) is not { } cell)
        {
            ViewModel.CancelEdit(row);
            return;
        }

        var strings = Strings.Value;
        Control focus;
        FrameworkElement editor;
        switch (column.EditorKind)
        {
            case GridEditorKind.People or GridEditorKind.Tags when row.DraftTokens is { } tokens:
                var picker = new TokenPicker { ViewModel = tokens, Header = column.Header, BoxAutomationId = "Grid_CellEditor", MinWidth = 280 };
                editor = picker;
                focus = picker;
                picker.Loaded += (_, _) => picker.FocusBox();
                break;
            case GridEditorKind.Custom when row.DraftCustom is { } custom:
                (editor, focus) = CustomFieldEditors.Create(custom, "Grid", showHeader: true);
                break;
            default:
                var notes = new TextBox
                {
                    Header = column.Header,
                    Text = row.DraftText,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 96,
                    MaxHeight = 320,
                    Width = 360,
                    MaxLength = Domain.Common.DomainLimits.LongText,
                };
                ScrollViewer.SetVerticalScrollBarVisibility(notes, ScrollBarVisibility.Auto);
                AutomationProperties.SetAutomationId(notes, "Grid_CellEditor");
                notes.TextChanged += (_, _) => row.DraftText = notes.Text;
                editor = notes;
                focus = notes;
                break;
        }

        var error = new TextBlock { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ErrorCaptionTextStyle"], MaxWidth = 360 };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        AutomationProperties.SetAutomationId(error, "Grid_CellError");
        var ring = new ProgressRing { Width = 16, Height = 16, IsActive = false };
        var save = new Button { Content = strings.GetString("Grid_FlyoutSave"), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
        var cancel = new Button { Content = strings.GetString("Common_Cancel") };
        AutomationProperties.SetAutomationId(save, "Grid_FlyoutSaveButton");
        AutomationProperties.SetAutomationId(cancel, "Grid_FlyoutCancelButton");
        ToolTipService.SetToolTip(save, strings.GetString("Grid_FlyoutSaveTooltip"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ring);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(editor);
        root.Children.Add(error);
        root.Children.Add(buttons);

        void Refresh(object? sender, PropertyChangedEventArgs e)
        {
            error.Text = row.ErrorMessage ?? string.Empty;
            ring.IsActive = row.IsSaving;
            save.IsEnabled = cancel.IsEnabled = !row.IsSaving;
        }

        row.PropertyChanged += Refresh;
        var flyout = new Flyout { Content = root, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, ShouldConstrainToRootBounds = true };
        _editorFlyout = flyout;
        _flyoutCloseApproved = false;
        save.Click += (_, _) => _ = CommitFlyoutAsync(flyout, cell, row);
        cancel.Click += (_, _) => CancelFlyout(flyout, row);

        // Design §7.4: Ctrl+Enter saves (plain Enter inserts a line break in Notes); Esc cancels.
        root.PreviewKeyDown += (sender, e) =>
        {
            if (e.Key == VirtualKey.Enter && IsDown(VirtualKey.Control))
            {
                e.Handled = true;
                _ = CommitFlyoutAsync(flyout, cell, row);
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CancelFlyout(flyout, row);
            }
        };

        // Light dismiss is leaving the cell: B-07 attempts a commit; a failure keeps the flyout and its error.
        flyout.Closing += (sender, args) =>
        {
            if (_flyoutCloseApproved)
            {
                return;
            }

            args.Cancel = true;
            if (!row.IsSaving)
            {
                _ = CommitFlyoutAsync(flyout, cell, row);
            }
        };
        flyout.Closed += (_, _) =>
        {
            row.PropertyChanged -= Refresh;
            if (ReferenceEquals(_editorFlyout, flyout))
            {
                _editorFlyout = null;
            }

            cell.Focus(FocusState.Programmatic);
        };
        flyout.Opened += (_, _) => focus.Focus(FocusState.Programmatic);
        flyout.ShowAt(cell);
    }

    private async Task CommitFlyoutAsync(Flyout flyout, GridCell cell, GridRowViewModel row)
    {
        if (row.IsSaving)
        {
            return;
        }

        var column = row.EditingColumn ?? cell.Column;
        if (await ViewModel.CommitAsync(row))
        {
            _flyoutCloseApproved = true;
            flyout.Hide();
            FocusCell(row, column);
            return;
        }

        if (row.ErrorMessage is { } message)
        {
            Announce(cell, message);
        }
    }

    private void CancelFlyout(Flyout flyout, GridRowViewModel row)
    {
        if (row.IsSaving)
        {
            return;
        }

        ViewModel.CancelEdit(row);
        _flyoutCloseApproved = true;
        flyout.Hide();
    }

    // ----- Popup, focus, and helpers -----

    private void OnUseMineClick(object sender, RoutedEventArgs e)
    {
        if (PopupRow is { EditingColumn: { } column } row)
        {
            _ = UseMineAsync(row, column);
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

    private void OnFocusRequested(object? sender, GridFocusRequest request)
    {
        if (request.BeginEdit)
        {
            FocusEditorSoon(request.Row, request.Column, selectAll: true);
        }
        else
        {
            FocusCell(request.Row, request.Column);
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
        CellPopup.IsOpen = IsLoaded;
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

    private static void Announce(UIElement element, string message)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionAborted, AutomationNotificationProcessing.ImportantMostRecent, message, "GridCellError");
    }

    private void FocusEditorSoon(GridRowViewModel row, string column, bool selectAll)
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
        // T-08.1 page padding: L 32/24, M 24/16, S 16/12 (design §2.3).
        HeaderRoot.Padding = width >= 1008 ? new Thickness(32, 24, 32, 12) : width >= 640 ? new Thickness(24, 16, 24, 12) : new Thickness(16, 12, 16, 8);

        // The header and the rows share one inset so every header sits above its column's cells.
        var inset = width >= 1008 ? 32 : width >= 640 ? 24 : 16;
        HeaderBorder.Padding = new Thickness(inset, 0, 0, 0);
        Table.Padding = new Thickness(inset, 0, 0, 0);

        // Design §4.3 S: only New task keeps its label inline; the CommandBar overflows the rest by itself.
        NewTaskText.Visibility = width >= 500 ? Visibility.Visible : Visibility.Collapsed;
        var small = width < 640;
        _search.Apply(width);
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

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

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

    // ----- Row menu (design §4.3): Open, Open CR, Copy…, Link to CR…, Add Subtask card, Archive/Restore, Delete…. -----
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
        var menu = RowMenu(row);
        if (args.TryGetPosition(container, out var point))
        {
            menu.ShowAt(container, point);
        }
        else
        {
            menu.ShowAt(container);
        }
    }

    private MenuFlyout RowMenu(GridRowViewModel row)
    {
        var strings = Strings.Value;
        var menu = new MenuFlyout();
        void Item(string key, string id, Action action)
        {
            var item = new MenuFlyoutItem { Text = strings.GetString(key) };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Item("Board_MenuOpen", "Grid_RowMenuOpenItem", () => _ = ViewModel.OpenRowAsync(row));
        if (row.Model.Cr.ChangeRequestId is not null)
        {
            Item("Board_MenuOpenCr", "Grid_RowMenuOpenCrItem", () => _ = ViewModel.OpenCrAsync(row));
        }

        if (row.Model.Task is { } task)
        {
            Item("Board_MenuCopy", "Grid_RowMenuCopyItem", () => _ = ViewModel.CopyRowAsync(row));

            // B-03.6/B-05: Link to CR… is offered for every unlinked task, archived ones included.
            if (task.Cr.ChangeRequestId is null)
            {
                Item("Grid_MenuLinkToCr", "Grid_RowMenuLinkToCrItem", () => _ = ViewModel.LinkRowAsync(row));
            }

            if (task.IsArchived)
            {
                Item("Grid_MenuRestore", "Grid_RowMenuRestoreItem", () => _ = ViewModel.RestoreRowAsync(row));
            }
            else
            {
                Item("Board_MenuAddSubtaskCard", "Grid_RowMenuAddSubtaskCardItem", () => _ = ViewModel.AddSubtaskCardAsync(row));
                Item("Board_MenuArchive", "Grid_RowMenuArchiveItem", () => _ = ViewModel.ArchiveRowAsync(row));
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Board_MenuDelete", "Grid_RowMenuDeleteItem", () => _ = ViewModel.DeleteRowAsync(row));
        return menu;
    }
}
