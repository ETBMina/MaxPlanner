using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WinUI.TableView;

namespace GridGate;

public sealed partial class MainWindow : Window
{
    private const int RowCount = 10_000;
    private const double MinRestoredWidth = 48;
    private const double MaxRestoredWidth = 800;

    private readonly ISaveService _saveService;
    private readonly List<TaskRowViewModel> _allRows;
    private readonly List<string> _eventLog = [];
    private ObservableCollection<TaskRowViewModel> _rows;
    private string? _defaultLayout;
    private string? _capturedLayout;
    private string? _sortKey;
    private SortDirection? _sortDirection;
    private NavigationIntent _pendingIntent;
    private TaskRowViewModel? _saveInFlight;
    private bool _isClosingAfterSave;

    private enum NavigationIntent
    {
        None,
        Next,
        Previous,
    }

    public MainWindow(ISaveService saveService)
    {
        _saveService = saveService;
        InitializeComponent();
        Title = "Grid gate spike - WinUI.TableView 1.5.1";

        _allRows = SampleData.Create(RowCount).Select(record => new TaskRowViewModel(record)).ToList();
        _rows = new ObservableCollection<TaskRowViewModel>(SortRows(_allRows, null, null));
        Table.ItemsSource = _rows;

        Table.BeginningEdit += OnBeginningEdit;
        Table.PreparingCellForEdit += OnPreparingCellForEdit;
        Table.CellEditEnding += OnCellEditEnding;
        Table.CellEditEnded += OnCellEditEnded;
        Table.Sorting += OnSorting;
        Table.ClearSorting += OnClearSorting;
        Table.ColumnReordered += OnColumnReordered;
        Table.Columns.ColumnPropertyChanged += OnColumnPropertyChanged;
        Table.ContainerContentChanging += OnContainerContentChanging;
        Table.PreviewKeyDown += OnTablePreviewKeyDown;
        Table.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _pendingIntent = NavigationIntent.None), handledEventsToo: true);
        Table.Loaded += OnTableLoaded;

        RootGrid.Loaded += (_, _) =>
        {
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            AppWindow.Resize(new SizeInt32((int)(1440 * scale), (int)(860 * scale)));
        };
    }

    private void OnTableLoaded(object sender, RoutedEventArgs e)
    {
        ApplyHeaderAutomationIds();
        _defaultLayout = SerializeLayout();
        Log($"Loaded {_rows.Count:N0} rows; default layout captured.");
    }

    // ---- Editing (criteria 2-4) ----

    private void OnTablePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // While a save is uncertain, Enter/Tab/Esc must not commit again, cancel, or move (B-07, AGENTS.md integrity rules).
        if (_saveInFlight is not null && e.Key is VirtualKey.Enter or VirtualKey.Tab or VirtualKey.Escape)
        {
            e.Handled = true;
            Log($"{e.Key} ignored: save in progress.");
            return;
        }

        _pendingIntent = e.Key == VirtualKey.Tab
            ? (IsKeyDown(VirtualKey.Shift) ? NavigationIntent.Previous : NavigationIntent.Next)
            : NavigationIntent.None;
    }

    private void OnBeginningEdit(object? sender, TableViewBeginningEditEventArgs e)
    {
        if (e.DataItem is not TaskRowViewModel row || e.Column.Tag is not string column)
        {
            return;
        }

        if (!row.CanEdit(column) || _saveInFlight is not null)
        {
            e.Cancel = true;
            Log($"BeginningEdit cancelled: {column} is read-only for {row.KindText} {row.Committed.Title}.");
            return;
        }

        Log($"BeginningEdit {column} row {row.Committed.Title} via {e.EditingArgs?.GetType().Name ?? "code"}.");
    }

    private void OnPreparingCellForEdit(object? sender, TableViewPreparingCellForEditEventArgs e)
    {
        if (e.DataItem is not TaskRowViewModel row || e.Column.Tag is not string column)
        {
            return;
        }

        row.ResetDraft(column);
        row.ClearError();
        var cell = e.Cell;

        // The editing template is not in the visual tree until the next layout pass.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FindDescendant<Control>(cell, c => AutomationProperties.GetAutomationId(c) == "Grid_CellEditor") is not { } editor)
            {
                return;
            }

            editor.Focus(FocusState.Keyboard);
            if (editor is TextBox textBox)
            {
                textBox.SelectAll();
                if (column == GridColumnKeys.Notes)
                {
                    AttachNotesHandlers(textBox, cell, row);
                }
            }
        });
    }

    private void AttachNotesHandlers(TextBox notesBox, TableViewCell cell, TaskRowViewModel row)
    {
        notesBox.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter && IsKeyDown(VirtualKey.Control))
            {
                args.Handled = true;
                StartCommit(row, GridColumnKeys.Notes, cell, NavigationIntent.None);
            }
        };

        if (FindDescendant<Button>(cell, b => AutomationProperties.GetAutomationId(b) == "Grid_NotesSaveButton") is { } saveButton)
        {
            saveButton.Click += (_, _) => StartCommit(row, GridColumnKeys.Notes, cell, NavigationIntent.None);
        }
    }

    private void OnCellEditEnding(object? sender, TableViewCellEditEndingEventArgs e)
    {
        var intent = _pendingIntent;
        _pendingIntent = NavigationIntent.None;
        if (_isClosingAfterSave || e.DataItem is not TaskRowViewModel row || e.Column.Tag is not string column)
        {
            return;
        }

        if (row.IsSaving)
        {
            e.Cancel = true;
            return;
        }

        if (e.EditAction == TableViewEditAction.Cancel)
        {
            row.ResetDraft(column);
            row.ClearError();
            Log($"CellEditEnding Cancel {column}: draft restored to committed value.");
            return;
        }

        if (!row.IsDraftDirty(column))
        {
            row.ClearError();
            Log($"CellEditEnding Commit {column}: unchanged, editor closes.");
            return;
        }

        // CellEditEnding is synchronous (CancelEventArgs, no deferral). Keep the editor open, save
        // asynchronously, and close it ourselves only after the save succeeds.
        e.Cancel = true;
        Log($"CellEditEnding Commit {column}: cancelled to keep editor open; saving (intent {intent}).");
        StartCommit(row, column, e.Cell, intent);
    }

    private void OnCellEditEnded(object? sender, TableViewCellEditEndedEventArgs e) =>
        Log($"CellEditEnded {e.Column.Tag} {e.EditAction}.");

    private void StartCommit(TaskRowViewModel row, string column, TableViewCell cell, NavigationIntent intent)
    {
        if (_saveInFlight is not null)
        {
            return;
        }

        _ = CommitAsync(row, column, cell, intent);
    }

    private async Task CommitAsync(TaskRowViewModel row, string column, TableViewCell cell, NavigationIntent intent)
    {
        if (row.Validate(column) is { } validationError)
        {
            row.FailSave(validationError);
            ReportFailure(validationError, cell);
            return;
        }

        var value = row.GetDraftValue(column);
        _saveInFlight = row;
        row.BeginSave();
        SetStatus(InfoBarSeverity.Informational, $"Saving {column}...");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Continuation resumes on the UI thread (captured DispatcherQueue context).
            var result = await _saveService.SaveAsync(row.Id, column, value);
            if (!result.IsSuccess)
            {
                row.FailSave(result.ErrorMessage ?? "Couldn't save.");
                ReportFailure(row.ErrorMessage!, cell);
                return;
            }

            row.CompleteSave(column, value);
            SetStatus(InfoBarSeverity.Success, $"Saved {column} in {stopwatch.ElapsedMilliseconds} ms.");
            Log($"Saved {column} = '{value}' after {stopwatch.ElapsedMilliseconds} ms.");

            if (!CloseEditorAfterSave(row, cell))
            {
                return;
            }

            if (intent != NavigationIntent.None)
            {
                MoveToAdjacentEditableCell(cell.Slot, intent == NavigationIntent.Next ? 1 : -1);
            }
        }
#pragma warning disable CA1031 // Spike: surface any unexpected failure in the UI instead of losing it in a discarded task.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            row.FailSave(ex.Message);
            ReportFailure(ex.Message, cell);
        }
        finally
        {
            _saveInFlight = null;
        }
    }

    private bool CloseEditorAfterSave(TaskRowViewModel row, TableViewCell cell)
    {
        // A recycled container now shows another row; never close an editor that is not this row's.
        if (!ReferenceEquals(cell.Row?.Content, row))
        {
            Log("Saved, but the editing container was recycled; nothing to close.");
            return false;
        }

        if (!InternalCloseToggle.IsOn)
        {
            Log("Public API only: editor left open showing the saved value; press Enter to close.");
            return false;
        }

        _isClosingAfterSave = true;
        try
        {
            var closed = TableViewInternals.EndCellEditing(Table, TableViewEditAction.Commit, cell);
            if (closed)
            {
                TableViewInternals.SetIsEditing(Table, false);
            }

            cell.Focus(FocusState.Programmatic);
            Log($"Editor closed via internal EndCellEditing: {closed}; IsEditing={TableViewInternals.GetIsEditing(Table)}.");
            return closed;
        }
        catch (MissingMethodException ex)
        {
            Log($"Internal TableView API missing ({ex.Message}); editor left open.");
            return false;
        }
        finally
        {
            _isClosingAfterSave = false;
        }
    }

    private void ReportFailure(string message, TableViewCell cell)
    {
        SetStatus(InfoBarSeverity.Error, message);
        Log($"Save failed: {message} (editor kept open, draft intact).");
        if (FindDescendant<Control>(cell, c => AutomationProperties.GetAutomationId(c) == "Grid_CellEditor") is { } editor)
        {
            editor.Focus(FocusState.Programmatic);
        }
    }

    private void MoveToAdjacentEditableCell(TableViewCellSlot from, int direction)
    {
        var columns = Table.Columns.VisibleColumns;
        var rowIndex = from.Row;
        var columnIndex = from.Column;
        for (var step = 0; step < (columns.Count * 2) + 2; step++)
        {
            columnIndex += direction;
            if (columnIndex >= columns.Count)
            {
                columnIndex = 0;
                rowIndex++;
            }
            else if (columnIndex < 0)
            {
                columnIndex = columns.Count - 1;
                rowIndex--;
            }

            if (rowIndex < 0 || rowIndex >= _rows.Count)
            {
                return;
            }

            if (columns[columnIndex].Tag is string key && _rows[rowIndex].CanEdit(key))
            {
                Table.CurrentCellSlot = new TableViewCellSlot(rowIndex, columnIndex);
                Log($"Moved to row {rowIndex} column {key}.");
                return;
            }
        }
    }

    // ---- Sorting and layout persistence (criterion 7) ----

    private void OnSorting(object? sender, TableViewSortingEventArgs e)
    {
        // Sorting stays product-controlled (typed keys, CreatedAt/ID tie-breakers, never touches Board order).
        e.Handled = true;
        var next = e.Column.SortDirection switch
        {
            null => SortDirection.Ascending,
            SortDirection.Ascending => SortDirection.Descending,
            _ => (SortDirection?)null,
        };
        ApplySort(e.Column.Tag as string, next);
    }

    private void OnClearSorting(object? sender, TableViewClearSortingEventArgs e)
    {
        e.Handled = true;
        ApplySort(null, null);
    }

    private void ApplySort(string? key, SortDirection? direction)
    {
        _sortKey = direction is null ? null : key;
        _sortDirection = direction;
        foreach (var column in Table.Columns)
        {
            column.SortDirection = column.Tag as string == _sortKey ? _sortDirection : null;
        }

        _rows = new ObservableCollection<TaskRowViewModel>(SortRows(_allRows, _sortKey, _sortDirection));
        Table.ItemsSource = _rows;
        Log($"Sort: {_sortKey ?? "default (CreatedAt desc, Id)"} {_sortDirection}.");
    }

    private static IEnumerable<TaskRowViewModel> SortRows(IEnumerable<TaskRowViewModel> rows, string? key, SortDirection? direction)
    {
        IOrderedEnumerable<TaskRowViewModel> ordered = key is null || direction is null
            ? rows.OrderByDescending(r => r.Committed.CreatedAtUtc)
            : direction == SortDirection.Ascending
                ? rows.OrderBy(r => r.GetSortKey(key), Comparer<IComparable?>.Create(CompareKeys))
                : rows.OrderByDescending(r => r.GetSortKey(key), Comparer<IComparable?>.Create(CompareKeys));
        return ordered.ThenByDescending(r => r.Committed.CreatedAtUtc).ThenBy(r => r.Id);
    }

    private static int CompareKeys(IComparable? left, IComparable? right) =>
        left is null ? (right is null ? 0 : -1) : right is null ? 1 : left.CompareTo(right);

    private void OnColumnReordered(object? sender, TableViewColumnReorderedEventArgs e) =>
        Log($"ColumnReordered {e.Column.Tag} -> {e.Index}; Columns index now {Table.Columns.IndexOf(e.Column)}.");

    private void OnColumnPropertyChanged(object? sender, TableViewColumnPropertyChangedEventArgs e) =>
        Log($"ColumnPropertyChanged {e.Column.Tag}.{e.PropertyName} (index {e.Index}).");

    private string SerializeLayout()
    {
        var layout = Table.Columns
            .Select((column, index) => new ColumnLayout(
                (string)column.Tag,
                index,
                // Declared pixel width is current immediately; ActualWidth lags until the next layout pass.
                Math.Round(column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth, 1),
                column.Visibility == Visibility.Visible,
                column.SortDirection?.ToString()))
            .ToList();
        return JsonSerializer.Serialize(layout, LayoutJsonContext.Default.ListColumnLayout);
    }

    private void RestoreLayout(string json)
    {
        var layout = JsonSerializer.Deserialize(json, LayoutJsonContext.Default.ListColumnLayout) ?? [];
        foreach (var entry in layout.OrderBy(l => l.DisplayIndex))
        {
            // Unknown keys (e.g. a removed custom field) are skipped rather than breaking the layout (B-07).
            var column = Table.Columns.FirstOrDefault(c => c.Tag as string == entry.Key);
            if (column is null)
            {
                continue;
            }

            var target = Math.Clamp(entry.DisplayIndex, 0, Table.Columns.Count - 1);
            var current = Table.Columns.IndexOf(column);
            if (current != target)
            {
                Table.Columns.Move(current, target);
            }

            column.Width = new GridLength(Math.Clamp(entry.Width, MinRestoredWidth, MaxRestoredWidth));
            column.Visibility = entry.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        var sorted = layout.FirstOrDefault(l => l.Sort is not null);
        ApplySort(sorted?.Key, sorted?.Sort is { } s ? Enum.Parse<SortDirection>(s) : null);
        ApplyHeaderAutomationIds();
    }

    private void OnApplySampleLayout(object sender, RoutedEventArgs e)
    {
        // Simulates user changes: move Notes to position 1, widen Title, hide Tags, sort Priority descending.
        var notes = Table.Columns.First(c => c.Tag as string == GridColumnKeys.Notes);
        Table.Columns.Move(Table.Columns.IndexOf(notes), 1);
        Table.Columns.First(c => c.Tag as string == GridColumnKeys.Title).Width = new GridLength(360);
        Table.Columns.First(c => c.Tag as string == GridColumnKeys.Tags).Visibility = Visibility.Collapsed;
        ApplySort(GridColumnKeys.Priority, SortDirection.Descending);
        ApplyHeaderAutomationIds();
    }

    private void OnCaptureLayout(object sender, RoutedEventArgs e)
    {
        _capturedLayout = SerializeLayout();
        LayoutBox.Text = _capturedLayout;
        Log("Layout captured.");
    }

    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        if (_defaultLayout is not null)
        {
            RestoreLayout(_defaultLayout);
            LayoutBox.Text = SerializeLayout();
            Log("Layout reset to default.");
        }
    }

    private void OnRestoreLayout(object sender, RoutedEventArgs e)
    {
        if (_capturedLayout is not null)
        {
            RestoreLayout(_capturedLayout);
            LayoutBox.Text = SerializeLayout();
            Log("Captured layout restored.");
        }
    }

    // ---- UI Automation identities (criterion 6) ----

    private void ApplyHeaderAutomationIds()
    {
        foreach (var column in Table.Columns)
        {
            if (column.HeaderControl is { } header && column.Tag is string key)
            {
                AutomationProperties.SetAutomationId(header, $"Grid_Header_{key}");
            }
        }
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not TaskRowViewModel row || args.ItemContainer is not TableViewRow container)
        {
            return;
        }

        AutomationProperties.SetAutomationId(container, $"Grid_Row_{row.Id:N}");
        AutomationProperties.SetName(container, row.AccessibleSummary);
        if (!ApplyCellAutomationIds(container, row))
        {
            // Cells are created when the row presenter is templated; retry once after that layout pass.
            DispatcherQueue.TryEnqueue(() => ApplyCellAutomationIds(container, row));
        }
    }

    private static bool ApplyCellAutomationIds(TableViewRow container, TaskRowViewModel row)
    {
        if (!ReferenceEquals(container.Content, row) || container.Cells.Count == 0)
        {
            return false;
        }

        foreach (var cell in container.Cells)
        {
            if (cell.Column?.Tag is string key)
            {
                AutomationProperties.SetAutomationId(cell, $"Grid_Cell_{row.Id:N}_{key}");
            }
        }

        return true;
    }

    // ---- Theme and measurement (criteria 5, 8) ----

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        RootGrid.RequestedTheme = ThemeCombo.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void OnMeasure(object sender, RoutedEventArgs e)
    {
        var message = ScrollProbe.Measure(Table, _rows);
        SetStatus(InfoBarSeverity.Informational, message);
        Log(message);
    }

    private async void OnScrollStress(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus(InfoBarSeverity.Informational, "Scroll stress running...");
            await ScrollProbe.RunAsync(Table, _rows, cycles: 5, Log);
            SetStatus(InfoBarSeverity.Success, "Scroll stress finished; see the event log.");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(InfoBarSeverity.Error, ex.Message);
        }
    }

    // ---- Helpers ----

    private void SetStatus(InfoBarSeverity severity, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = message;
    }

    private void Log(string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} {message}");
        _eventLog.Insert(0, line);

        // Spike evidence only: the product logs under data/ through its logging service.
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "gridgate-events.log"), line + Environment.NewLine);
        if (_eventLog.Count > 40)
        {
            _eventLog.RemoveAt(_eventLog.Count - 1);
        }

        EventLogBox.Text = string.Join(Environment.NewLine, _eventLog);
        Debug.WriteLine(message);
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

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
