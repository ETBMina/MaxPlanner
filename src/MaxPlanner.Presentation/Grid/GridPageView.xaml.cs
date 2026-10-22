using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// The Grid page view. Code-behind handles view mechanics only (AGENTS.md §4): the keyboard and pointer
/// state machine of the cells (design §7.4), focus and scroll (§7.7), and the error/conflict popup. Every
/// decision about saving belongs to <see cref="GridPageViewModel"/>.
/// </summary>
public sealed partial class GridPageView : UserControl, IGridCellHost
{
    public static readonly DependencyProperty PopupRowProperty = DependencyProperty.Register(
        nameof(PopupRow), typeof(GridRowViewModel), typeof(GridPageView), new PropertyMetadata(null));

    public static readonly DependencyProperty PopupHasConflictProperty = DependencyProperty.Register(
        nameof(PopupHasConflict), typeof(bool), typeof(GridPageView), new PropertyMetadata(false));

    public static readonly DependencyProperty PopupSavedNowTextProperty = DependencyProperty.Register(
        nameof(PopupSavedNowText), typeof(string), typeof(GridPageView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PopupDraftTextProperty = DependencyProperty.Register(
        nameof(PopupDraftText), typeof(string), typeof(GridPageView), new PropertyMetadata(string.Empty));

    private readonly List<GridHeaderCell> _headerCells;

    public GridPageView(GridPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Table.Host = this;
        _headerCells = [.. HeaderRow.Children.OfType<GridHeaderCell>()];
        ViewModel.FocusRequested += OnFocusRequested;
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
    }

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

    /// <summary>Gives the page title keyboard focus after navigation (design §7.7).</summary>
    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

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

        var dropDownOpen = FindEditor(cell) is ComboBox { IsDropDownOpen: true };
        switch (e.Key)
        {
            case VirtualKey.Enter when !dropDownOpen:
                e.Handled = true;
                _ = CommitAsync(cell, row, move: 0, restoreFocus: true);
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                _ = CommitAsync(cell, row, move: IsShiftDown() ? -1 : 1, restoreFocus: true);
                break;
            case VirtualKey.Escape when !dropDownOpen:
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

        var columns = GridColumns.All;
        var index = IndexOf(cell.Column);
        var rowIndex = ViewModel.Rows.IndexOf(row);
        switch (e.Key)
        {
            case VirtualKey.F2:
                e.Handled = true;
                BeginEditAndFocus(row, cell.Column, null);
                break;
            case VirtualKey.Left when index > 0:
                e.Handled = true;
                FocusCell(row, columns[index - 1]);
                break;
            case VirtualKey.Right when index < columns.Count - 1:
                e.Handled = true;
                FocusCell(row, columns[index + 1]);
                break;
            case VirtualKey.Home:
                e.Handled = true;
                FocusCell(row, columns[0]);
                break;
            case VirtualKey.End:
                e.Handled = true;
                FocusCell(row, columns[^1]);
                break;
            case VirtualKey.Up when rowIndex > 0:
                e.Handled = true;
                FocusCell(ViewModel.Rows[rowIndex - 1], cell.Column);
                break;

            // Design §7.4: Enter on a committed cell moves down.
            case VirtualKey.Down or VirtualKey.Enter when rowIndex >= 0 && rowIndex < ViewModel.Rows.Count - 1:
                e.Handled = true;
                FocusCell(ViewModel.Rows[rowIndex + 1], cell.Column);
                break;
            default:
                break;
        }
    }

    void IGridCellHost.OnCellCharacterReceived(GridCell cell, CharacterReceivedRoutedEventArgs e)
    {
        // Design §4.3: typing a character on a text cell starts an edit that replaces the value.
        if (cell.Row is not { } row || row.IsEditing || char.IsControl(e.Character) || cell.Column == GridColumns.Status)
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

        if (column == GridColumns.Status)
        {
            var option = ViewModel.StatusOptions.FirstOrDefault(o => string.Equals(o.Name, value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (option is null)
            {
                ViewModel.CancelEdit(row);
                throw new ArgumentException("No task status has that name.", nameof(value));
            }

            row.DraftStatus = option;
        }
        else
        {
            row.DraftText = value;
        }

        _ = CommitAsync(cell, row, move: 0, restoreFocus: true);
    }

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

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not Control editor || editor.DataContext is not GridRowViewModel row || FindAncestor<GridCell>(editor) is not { } cell)
        {
            return;
        }

        // B-07: leaving an edited cell attempts a commit, except while the outcome is pending, while a
        // conflict waits for the user's choice, or when the same draft was just refused.
        if (row.EditingColumn != cell.Column || row.IsSaving || row.HasConflict || editor is ComboBox { IsDropDownOpen: true })
        {
            return;
        }

        if (row.FailedDraft is { } failed && string.Equals(failed, row.DraftValue, StringComparison.Ordinal))
        {
            return;
        }

        if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused
            && (IsWithin(focused, cell) || (CellPopup.Child is { } popupContent && IsWithin(focused, popupContent))))
        {
            return;
        }

        _ = CommitAsync(cell, row, move: 0, restoreFocus: false);
    }

    private void OnStatusEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo && combo.DataContext is GridRowViewModel row)
        {
            combo.ItemsSource = ViewModel.StatusOptions;
            combo.SelectedItem = row.DraftStatus;
        }
    }

    private void OnStatusEditorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: StatusOption option } combo
            && combo.DataContext is GridRowViewModel { EditingColumn: GridColumns.Status } row)
        {
            row.DraftStatus = option;
        }
    }

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
        // The editor is created by x:Load when the row's editing state changes; focus it after that layout pass.
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
        var columns = GridColumns.All;
        for (var i = IndexOf(column) + direction; i >= 0 && i < columns.Count; i += direction)
        {
            if (row.CanEdit(columns[i]))
            {
                FocusCell(row, columns[i]);
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
        HeaderRow.Padding = new Thickness(inset, 0, 0, 0);
        Table.Padding = new Thickness(inset, 0, 0, 0);
    }

    private static Control? FindEditor(DependencyObject cell) => FindDescendant<Control>(cell, c => c is TextBox or ComboBox);

    private static int IndexOf(string column)
    {
        var columns = GridColumns.All;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i] == column)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

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
}
