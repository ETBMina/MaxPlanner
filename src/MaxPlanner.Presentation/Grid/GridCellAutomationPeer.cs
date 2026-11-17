using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// A cell's UIA peer: GridItem/TableItem place it in the table with its column header; Value reads the
/// committed value (or the draft while editing) and sets a value through the page's commit path; Invoke
/// opens the editor like F2. The AutomationId is <c>Grid_Cell_{RowId:N}_{Column}</c> (design §8.1).
/// </summary>
internal sealed partial class GridCellAutomationPeer(GridCell owner) : FrameworkElementAutomationPeer(owner),
    IGridItemProvider, ITableItemProvider, IValueProvider, IInvokeProvider
{
    public int Row => owner.Row is { } row && owner.Host?.GridViewModel is { } page ? page.Rows.IndexOf(row) : -1;

    public int Column => owner.Host?.DisplayColumns is { } columns ? IndexOf(columns, owner.Column) : -1;

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

    /// <summary>Tells assistive technology that an edit of the cell was refused, with the error the cell shows.</summary>
    internal static void AnnounceError(UIElement element, string message)
    {
        var peer = FromElement(element) ?? CreatePeerForElement(element);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionAborted, AutomationNotificationProcessing.ImportantMostRecent, message, "GridCellError");
    }

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

    private static int IndexOf(IReadOnlyList<GridColumnViewModel> columns, string key)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private static readonly Lazy<string> CellTypeName = new(() => ResourceStrings.Shared.GetString("Grid_CellControlType"));
}
