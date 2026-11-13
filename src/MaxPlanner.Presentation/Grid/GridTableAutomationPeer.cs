using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MaxPlanner.Presentation.Grid;

internal sealed partial class GridTableAutomationPeer(GridTable owner) : ListViewAutomationPeer(owner), IGridProvider, ITableProvider
{
    public int RowCount => owner.Items.Count;

    public int ColumnCount => owner.Host?.DisplayColumns.Count ?? 0;

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

        var key = owner.Host!.DisplayColumns[column].Key;
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
