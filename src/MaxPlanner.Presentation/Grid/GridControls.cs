using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MaxPlanner.Presentation.Grid;

/// <summary>What a <see cref="GridCell"/> asks of the page that hosts it. Implemented by the Grid page view.</summary>
internal interface IGridCellHost
{
    GridPageViewModel? GridViewModel { get; }

    GridTable? Table { get; }

    IReadOnlyList<GridHeaderCell> HeaderCells { get; }

    void OnCellKeyDown(GridCell cell, KeyRoutedEventArgs e);

    void OnCellPreviewKeyDown(GridCell cell, KeyRoutedEventArgs e);

    void OnCellCharacterReceived(GridCell cell, CharacterReceivedRoutedEventArgs e);

    void OnCellDoubleTapped(GridCell cell);

    /// <summary>UIA Invoke on a cell: opens its editor, like F2.</summary>
    void BeginEditFromAutomation(GridCell cell);

    /// <summary>UIA ValuePattern.SetValue on a cell: edits and commits through the same command path as typing.</summary>
    void SetValueFromAutomation(GridCell cell, string value);
}

/// <summary>
/// One focusable Grid cell (ADR 0004 custom grid). Keyboard and pointer input is forwarded to the page,
/// which owns the editing state machine; the automation peer exposes GridItem/TableItem/Value/Invoke.
/// </summary>
public sealed partial class GridCell : ContentControl
{
    public static readonly DependencyProperty ColumnProperty = DependencyProperty.Register(
        nameof(Column), typeof(string), typeof(GridCell), new PropertyMetadata(string.Empty));

    public GridCell()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        Padding = new Thickness(8, 0, 8, 0);
    }

    /// <summary>The column key (<see cref="GridColumns"/>).</summary>
    public string Column
    {
        get => (string)GetValue(ColumnProperty);
        set => SetValue(ColumnProperty, value);
    }

    public GridRowViewModel? Row => DataContext as GridRowViewModel;

    internal IGridCellHost? Host => FindHost(this);

    protected override AutomationPeer OnCreateAutomationPeer() => new GridCellAutomationPeer(this);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        Host?.OnCellKeyDown(this, e);
        if (!e.Handled)
        {
            base.OnKeyDown(e);
        }
    }

    protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
    {
        Host?.OnCellPreviewKeyDown(this, e);
        if (!e.Handled)
        {
            base.OnPreviewKeyDown(e);
        }
    }

    protected override void OnCharacterReceived(CharacterReceivedRoutedEventArgs e)
    {
        Host?.OnCellCharacterReceived(this, e);
        if (!e.Handled)
        {
            base.OnCharacterReceived(e);
        }
    }

    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        Host?.OnCellDoubleTapped(this);
        e.Handled = true;
    }

    private static IGridCellHost? FindHost(DependencyObject start)
    {
        for (var current = VisualTreeHelper.GetParent(start); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is IGridCellHost host)
            {
                return host;
            }
        }

        return null;
    }
}

/// <summary>A column header of the custom grid: a UIA HeaderItem that the table and its cells reference.</summary>
public sealed partial class GridHeaderCell : ContentControl
{
    public static readonly DependencyProperty ColumnProperty = DependencyProperty.Register(
        nameof(Column), typeof(string), typeof(GridHeaderCell), new PropertyMetadata(string.Empty));

    public GridHeaderCell()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
    }

    public string Column
    {
        get => (string)GetValue(ColumnProperty);
        set => SetValue(ColumnProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GridHeaderCellAutomationPeer(this);
}

/// <summary>The row list of the custom grid; its peer exposes the Grid and Table patterns (ADR 0004 row 6).</summary>
public sealed partial class GridTable : ListView
{
    internal IGridCellHost? Host { get; set; }

    protected override DependencyObject GetContainerForItemOverride() => new GridRowContainer();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is GridRowContainer;

    protected override AutomationPeer OnCreateAutomationPeer() => new GridTableAutomationPeer(this);
}

/// <summary>A Grid row container whose peer is a UIA DataItem (a table row) instead of a list item.</summary>
public sealed partial class GridRowContainer : ListViewItem
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GridRowAutomationPeer(this);
}

/// <summary>A row's peer: a DataItem named by the row summary, with AutomationId <c>Grid_Row_{Id:N}</c> (design §8).</summary>
internal sealed partial class GridRowAutomationPeer(GridRowContainer owner) : ListViewItemAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

    protected override string GetAutomationIdCore() =>
        owner.Content is GridRowViewModel row ? $"Grid_Row_{row.Key:N}" : base.GetAutomationIdCore();

    protected override string GetNameCore() => owner.Content is GridRowViewModel row ? row.AccessibleName : base.GetNameCore();
}

internal sealed partial class GridTableAutomationPeer(GridTable owner) : ListViewAutomationPeer(owner), IGridProvider, ITableProvider
{
    public int RowCount => owner.Items.Count;

    public int ColumnCount => GridColumns.All.Count;

    public RowOrColumnMajor RowOrColumnMajor => RowOrColumnMajor.RowMajor;

    /// <summary>
    /// A cell of a realized row. Rows are virtualized (T-08), so a row scrolled far out of view has no
    /// element; clients scroll it into view first (ScrollItem) and ask again.
    /// </summary>
    public IRawElementProviderSimple? GetItem(int row, int column)
    {
        if (row < 0 || row >= RowCount || column < 0 || column >= ColumnCount)
        {
            throw new ArgumentOutOfRangeException(row < 0 || row >= RowCount ? nameof(row) : nameof(column));
        }

        if (owner.ContainerFromIndex(row) is not ListViewItem container)
        {
            return null;
        }

        var key = GridColumns.All[column];
        var cell = FindCell(container, key);
        return cell is null ? null : ProviderFromPeer(CreatePeerForElement(cell));
    }

    public IRawElementProviderSimple[] GetColumnHeaders() =>
        owner.Host is { } host ? [.. host.HeaderCells.Select(h => ProviderFromPeer(CreatePeerForElement(h)))] : [];

    public IRawElementProviderSimple[] GetRowHeaders() => [];

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.Grid or PatternInterface.Table ? this : base.GetPatternCore(patternInterface);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataGrid;

    internal static GridCell? FindCell(DependencyObject root, string column)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is GridCell cell && cell.Column == column)
            {
                return cell;
            }

            if (FindCell(child, column) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

internal sealed partial class GridHeaderCellAutomationPeer(GridHeaderCell owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.HeaderItem;

    protected override string GetAutomationIdCore() => "Grid_Header_" + owner.Column;

    protected override bool IsKeyboardFocusableCore() => false;
}

/// <summary>
/// A cell's UIA peer: GridItem/TableItem place it in the table with its column header; Value reads the
/// committed value (or the draft while editing) and sets a value through the page's commit path; Invoke
/// opens the editor like F2. The AutomationId is <c>Grid_Cell_{RowId:N}_{Column}</c> (design §8.1).
/// </summary>
internal sealed partial class GridCellAutomationPeer(GridCell owner) : FrameworkElementAutomationPeer(owner),
    IGridItemProvider, ITableItemProvider, IValueProvider, IInvokeProvider
{
    public int Row => owner.Row is { } row && owner.Host?.GridViewModel is { } page ? page.Rows.IndexOf(row) : -1;

    public int Column => owner.Column is { } key ? IndexOf(key) : -1;

    public int RowSpan => 1;

    public int ColumnSpan => 1;

    public IRawElementProviderSimple? ContainingGrid =>
        owner.Host?.Table is { } table ? ProviderFromPeer(CreatePeerForElement(table)) : null;

    public bool IsReadOnly => owner.Row is not { } row || !row.CanEdit(owner.Column) || owner.Host?.GridViewModel?.IsEditingBlocked != false;

    public string Value => owner.Row is not { } row ? string.Empty
        : row.EditingColumn == owner.Column ? row.DraftValue
        : row.DisplayValue(owner.Column);

    public IRawElementProviderSimple[] GetColumnHeaderItems() =>
        owner.Host?.HeaderCells.FirstOrDefault(h => h.Column == owner.Column) is { } header ? [ProviderFromPeer(CreatePeerForElement(header))] : [];

    public IRawElementProviderSimple[] GetRowHeaderItems() => [];

    public void SetValue(string value)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The cell is read-only.");
        }

        owner.Host?.SetValueFromAutomation(owner, value ?? string.Empty);
    }

    public void Invoke()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The cell is read-only.");
        }

        owner.Host?.BeginEditFromAutomation(owner);
    }

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.GridItem or PatternInterface.TableItem or PatternInterface.Value => this,
        PatternInterface.Invoke when owner.Row is { } row && row.CanEdit(owner.Column) => this,
        _ => base.GetPatternCore(patternInterface),
    };

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

    protected override string GetLocalizedControlTypeCore() => CellTypeName.Value;

    protected override string GetAutomationIdCore() =>
        owner.Row is { } row ? $"Grid_Cell_{row.Key:N}_{owner.Column}" : base.GetAutomationIdCore();

    protected override string GetNameCore()
    {
        var explicitName = base.GetNameCore();
        return !string.IsNullOrEmpty(explicitName) ? explicitName : Value;
    }

    private static int IndexOf(string key)
    {
        for (var i = 0; i < GridColumns.All.Count; i++)
        {
            if (GridColumns.All[i] == key)
            {
                return i;
            }
        }

        return -1;
    }

    private static readonly Lazy<string> CellTypeName = new(() => new Common.Services.ResourceStrings().GetString("Grid_CellControlType"));
}
