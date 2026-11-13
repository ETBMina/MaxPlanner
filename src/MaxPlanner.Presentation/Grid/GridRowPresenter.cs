using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// The cells of one Grid row, created in code for the displayed columns (the column set is configurable, B-07).
/// It follows its row ViewModel: display values refresh in place, and the editing cell switches to its typed
/// inline editor; flyout editors are opened by the page. Subscriptions live only while the row is loaded, so
/// recycled containers never keep a row alive.
/// </summary>
public sealed partial class GridRowPresenter : Panel
{
    private readonly List<GridColumnViewModel> _columns = [];
    private IGridCellHost? _host;
    private GridRowViewModel? _row;
    private bool _isLoaded;

    public GridRowPresenter()
    {
        Height = GridColumnLayout.RowHeight;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, args) => SetRow(args.NewValue as GridRowViewModel);
    }

    protected override Size MeasureOverride(Size availableSize) => GridColumnLayout.Measure(Children, _columns, GridColumnLayout.RowHeight);

    protected override Size ArrangeOverride(Size finalSize)
    {
        GridColumnLayout.Arrange(Children, _columns, GridColumnLayout.RowHeight, _host?.FrozenOffset ?? 0, _host?.IsFirstColumnFrozen == true);
        return new Size(finalSize.Width, GridColumnLayout.RowHeight);
    }

    private void Attach()
    {
        _isLoaded = true;
        _host = GridVisualTree.FindHost(this);
        if (_host is not null)
        {
            _host.DisplayLayoutChanged += OnDisplayLayoutChanged;
        }

        // Rebuild creates and fills every cell, so the row is attached without first refilling the cells it replaces.
        SetRow(DataContext as GridRowViewModel, force: true, refresh: false);
        Rebuild();
    }

    private void Detach()
    {
        _isLoaded = false;
        if (_host is not null)
        {
            _host.DisplayLayoutChanged -= OnDisplayLayoutChanged;
        }

        UnsubscribeColumns();
        SetRow(null);
    }

    private void SetRow(GridRowViewModel? row, bool force = false, bool refresh = true)
    {
        if (ReferenceEquals(row, _row) && !force)
        {
            return;
        }

        if (_row is not null)
        {
            _row.PropertyChanged -= OnRowPropertyChanged;
            _row.DisplayChanged -= OnRowDisplayChanged;
        }

        _row = _isLoaded ? row : null;
        if (_row is not null)
        {
            _row.PropertyChanged += OnRowPropertyChanged;
            _row.DisplayChanged += OnRowDisplayChanged;
        }

        if (refresh)
        {
            RefreshAll();
        }
    }

    private void OnDisplayLayoutChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        UnsubscribeColumns();
        Children.Clear();
        if (_host is null)
        {
            return;
        }

        _columns.AddRange(_host.DisplayColumns);
        foreach (var column in _columns)
        {
            column.PropertyChanged += OnColumnPropertyChanged;
            Children.Add(new GridCell { Column = column.Key });
        }

        RefreshAll();
        InvalidateMeasure();
    }

    private void UnsubscribeColumns()
    {
        foreach (var column in _columns)
        {
            column.PropertyChanged -= OnColumnPropertyChanged;
        }

        _columns.Clear();
    }

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GridColumnViewModel.Width))
        {
            InvalidateMeasure();
        }
    }

    private void OnRowDisplayChanged(object? sender, EventArgs e) => RefreshAll();

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GridRowViewModel.EditingColumn) or nameof(GridRowViewModel.IsSaving))
        {
            RefreshAll();
        }
    }

    private void RefreshAll()
    {
        for (var i = 0; i < Children.Count && i < _columns.Count; i++)
        {
            if (Children[i] is GridCell cell)
            {
                Refresh(cell, _columns[i], i == 0 && _host?.IsFirstColumnFrozen == true);
            }
        }
    }

    private void Refresh(GridCell cell, GridColumnViewModel column, bool isFrozen)
    {
        cell.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[isFrozen ? "GridFrozenCellStyle" : "GridCellStyle"];
        if (_row is not { } row || _host is not { } host)
        {
            cell.Content = null;
            return;
        }

        var isInlineEditing = row.EditingColumn == column.Key && !column.UsesFlyout;
        if (isInlineEditing)
        {
            // An open editor is kept across refreshes so typing, focus, and the draft are never reset.
            if (cell.Content is not FrameworkElement { Tag: GridCellContent.EditorTag })
            {
                cell.Content = GridCellContent.CreateEditor(row, column, host);
            }

            return;
        }

        cell.Content = GridCellContent.CreateDisplay(row, column, host);
        AutomationProperties.SetName(cell, row.FullValue(column.Key));
    }
}
