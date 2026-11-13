using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// A column header of the custom grid (design §4.3, §7.4): a focusable UIA HeaderItem named "Sort by Title, currently
/// ascending". Enter, Space, or a click sorts; Alt+Left/Right resizes by 16 epx; Ctrl+Shift+Left/Right moves the
/// column. The page handles each action.
/// </summary>
public sealed partial class GridHeaderCell : ContentControl
{
    public static readonly DependencyProperty ColumnProperty = DependencyProperty.Register(
        nameof(Column), typeof(string), typeof(GridHeaderCell), new PropertyMetadata(string.Empty));

    public GridHeaderCell()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
    }

    /// <summary>Raised for Enter, Space, a tap, or UIA Invoke.</summary>
    public event EventHandler? SortRequested;

    public string Column
    {
        get => (string)GetValue(ColumnProperty);
        set => SetValue(ColumnProperty, value);
    }

    internal void RequestSort() => SortRequested?.Invoke(this, EventArgs.Empty);

    protected override AutomationPeer OnCreateAutomationPeer() => new GridHeaderCellAutomationPeer(this);

    protected override void OnTapped(TappedRoutedEventArgs e)
    {
        e.Handled = true;
        Focus(FocusState.Pointer);
        RequestSort();
    }
}
