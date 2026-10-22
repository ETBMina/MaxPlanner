using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace GridGate;

/// <summary>
/// ADR 0004 option 2 comparison: a virtualized ListView whose cells are focusable ContentControls.
/// Editing is entirely app-owned, so asynchronous commit needs no control internals.
/// </summary>
public sealed partial class FallbackWindow : Window
{
    private const int RowCount = 10_000;
    private static readonly string[] s_columnOrder = ["Kind", "CrNumber", "Squad", "Owner", "Title", "Service", "Status", "StartDate"];

    // The comparison implements editors for these two columns only (a text and a lookup editor).
    private static readonly HashSet<string> s_editableColumns = [GridColumnKeys.Title, GridColumnKeys.Status];

    private readonly ISaveService _saveService;
    private readonly ObservableCollection<TaskRowViewModel> _rows;
    private readonly List<string> _eventLog = [];
    private TaskRowViewModel? _saveInFlight;

    public FallbackWindow(ISaveService saveService)
    {
        _saveService = saveService;
        InitializeComponent();
        Title = "Grid gate fallback - ListView";

        _rows = new ObservableCollection<TaskRowViewModel>(
            SampleData.Create(RowCount)
                .Select(record => new TaskRowViewModel(record))
                .OrderByDescending(r => r.Committed.CreatedAtUtc)
                .ThenBy(r => r.Id));
        List.ItemsSource = _rows;
        List.ContainerContentChanging += OnContainerContentChanging;

        RootGrid.Loaded += (_, _) =>
        {
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            AppWindow.Resize(new SizeInt32((int)(1440 * scale), (int)(860 * scale)));
            Log($"Loaded {_rows.Count:N0} rows.");

            // Spike harness: give keyboard focus to an editable cell so posted keys have a target without mouse input.
            DispatcherQueue.TryEnqueue(() => FocusCell(_rows[4], GridColumnKeys.Title));
        };
    }

    private void OnCellDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TryGetCell(sender, out var cell, out var row, out var column) && row.EditingColumn is null)
        {
            BeginEdit(cell, row, column, "double-click");
            e.Handled = true;
        }
    }

    /// <summary>Editing keys are handled while tunnelling so the ComboBox editor cannot swallow Enter.</summary>
    private void OnCellPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!TryGetCell(sender, out var cell, out var row, out var column) || row.EditingColumn != column)
        {
            return;
        }

        if (_saveInFlight is not null && e.Key is VirtualKey.Enter or VirtualKey.Tab or VirtualKey.Escape)
        {
            e.Handled = true;
            return;
        }

        var dropDownOpen = FindEditor(cell) is ComboBox { IsDropDownOpen: true };
        switch (e.Key)
        {
            case VirtualKey.Enter when !dropDownOpen:
                e.Handled = true;
                _ = CommitAsync(cell, row, column, 0);
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                _ = CommitAsync(cell, row, column, IsKeyDown(VirtualKey.Shift) ? -1 : 1);
                break;
            case VirtualKey.Escape when !dropDownOpen:
                e.Handled = true;
                row.ResetDraft(column);
                row.ClearError();
                row.EditingColumn = null;
                cell.Focus(FocusState.Keyboard);
                Log($"Cancel {column}: draft restored.");
                break;
            default:
                break;
        }
    }

    private void OnCellKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!TryGetCell(sender, out var cell, out var row, out var column) || row.EditingColumn is not null)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.F2 when CanEdit(row, column):
                e.Handled = true;
                BeginEdit(cell, row, column, "F2");
                break;
            case VirtualKey.Left or VirtualKey.Right:
                e.Handled = true;
                FocusCell(row, NextColumn(column, e.Key == VirtualKey.Right ? 1 : -1, _ => true));
                break;
            case VirtualKey.Up or VirtualKey.Down:
                e.Handled = true;
                var index = _rows.IndexOf(row) + (e.Key == VirtualKey.Down ? 1 : -1);
                if (index >= 0 && index < _rows.Count)
                {
                    FocusCell(_rows[index], column);
                }

                break;
            default:
                break;
        }
    }

    private void BeginEdit(ContentControl cell, TaskRowViewModel row, string column, string trigger)
    {
        if (!CanEdit(row, column) || _saveInFlight is not null)
        {
            return;
        }

        row.ResetDraft(column);
        row.ClearError();
        row.EditingColumn = column;
        Log($"BeginEdit {column} row {row.Committed.Title} via {trigger}.");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FindEditor(cell) is { } editor)
            {
                editor.Focus(FocusState.Keyboard);
                (editor as TextBox)?.SelectAll();
            }
        });
    }

    private async Task CommitAsync(ContentControl cell, TaskRowViewModel row, string column, int move)
    {
        if (!row.IsDraftDirty(column))
        {
            row.EditingColumn = null;
            cell.Focus(FocusState.Keyboard);
            MoveAfterCommit(row, column, move);
            return;
        }

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
        try
        {
            var result = await _saveService.SaveAsync(row.Id, column, value);
            if (!result.IsSuccess)
            {
                row.FailSave(result.ErrorMessage ?? "Couldn't save.");
                ReportFailure(row.ErrorMessage!, cell);
                return;
            }

            row.CompleteSave(column, value);
            row.EditingColumn = null;
            SetStatus(InfoBarSeverity.Success, $"Saved {column}.");
            Log($"Saved {column} = '{value}'; editor closed with public APIs only.");

            // The container may have been recycled during the await; focus by row identity, not by the old element.
            FocusCell(row, column);
            MoveAfterCommit(row, column, move);
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

    private void MoveAfterCommit(TaskRowViewModel row, string column, int move)
    {
        if (move == 0)
        {
            return;
        }

        var next = NextColumn(column, move, c => CanEdit(row, c));
        if (next != column)
        {
            FocusCell(row, next);
            Log($"Moved to {next}.");
        }
    }

    private static bool CanEdit(TaskRowViewModel row, string column) =>
        s_editableColumns.Contains(column) && row.CanEdit(column);

    private static string NextColumn(string column, int direction, Func<string, bool> accept)
    {
        var index = Array.IndexOf(s_columnOrder, column);
        for (var i = index + direction; i >= 0 && i < s_columnOrder.Length; i += direction)
        {
            if (accept(s_columnOrder[i]))
            {
                return s_columnOrder[i];
            }
        }

        return column;
    }

    private void FocusCell(TaskRowViewModel row, string column)
    {
        List.ScrollIntoView(row);
        List.UpdateLayout();
        if (List.ContainerFromItem(row) is ListViewItem container
            && FindDescendant<ContentControl>(container, c => c.Tag as string == column) is { } cell)
        {
            cell.Focus(FocusState.Keyboard);
        }
    }

    private void ReportFailure(string message, ContentControl cell)
    {
        SetStatus(InfoBarSeverity.Error, message);
        Log($"Save failed: {message} (editor kept open, draft intact).");
        FindEditor(cell)?.Focus(FocusState.Programmatic);
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not TaskRowViewModel row || args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        AutomationProperties.SetAutomationId(container, $"Grid_Row_{row.Id:N}");
        AutomationProperties.SetName(container, row.AccessibleSummary);
        if (!ApplyCellAutomationIds(container, row))
        {
            DispatcherQueue.TryEnqueue(() => ApplyCellAutomationIds(container, row));
        }
    }

    private static bool ApplyCellAutomationIds(ListViewItem container, TaskRowViewModel row)
    {
        if (!ReferenceEquals(container.Content, row))
        {
            return false;
        }

        var any = false;
        foreach (var cell in FindAll<ContentControl>(container).Where(c => c.Tag is string))
        {
            AutomationProperties.SetAutomationId(cell, $"Grid_Cell_{row.Id:N}_{cell.Tag}");
            any = true;
        }

        return any;
    }

    private void OnMeasure(object sender, RoutedEventArgs e)
    {
        var message = ScrollProbe.Measure(List, _rows);
        SetStatus(InfoBarSeverity.Informational, message);
        Log(message);
    }

    private async void OnScrollStress(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus(InfoBarSeverity.Informational, "Scroll stress running...");
            await ScrollProbe.RunAsync(List, _rows, cycles: 5, Log);
            SetStatus(InfoBarSeverity.Success, "Scroll stress finished; see the event log.");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(InfoBarSeverity.Error, ex.Message);
        }
    }

    private static bool TryGetCell(object sender, out ContentControl cell, out TaskRowViewModel row, out string column)
    {
        cell = (sender as ContentControl)!;
        row = (cell?.DataContext as TaskRowViewModel)!;
        column = (cell?.Tag as string)!;
        return cell is not null && row is not null && column is not null;
    }

    private static Control? FindEditor(DependencyObject cell) =>
        FindDescendant<Control>(cell, c => AutomationProperties.GetAutomationId(c) == "Grid_CellEditor" && c.Visibility == Visibility.Visible);

    private void SetStatus(InfoBarSeverity severity, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = message;
    }

    private void Log(string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} {message}");
        _eventLog.Insert(0, line);
        if (_eventLog.Count > 40)
        {
            _eventLog.RemoveAt(_eventLog.Count - 1);
        }

        EventLogBox.Text = string.Join(Environment.NewLine, _eventLog);

        // Spike evidence only: the product logs under data/ through its logging service.
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "gridgate-fallback-events.log"), line + Environment.NewLine);
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject =>
        FindAll<T>(root).FirstOrDefault(predicate);

    private static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindAll<T>(child))
            {
                yield return nested;
            }
        }
    }
}
