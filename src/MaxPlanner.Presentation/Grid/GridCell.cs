using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// One focusable Grid cell (ADR 0004 custom grid). Keyboard and pointer input is forwarded to the page,
/// which owns the editing state machine; the automation peer exposes GridItem/TableItem/Value/Invoke.
/// </summary>
public sealed partial class GridCell : ContentControl
{
    public static readonly DependencyProperty ColumnProperty = DependencyProperty.Register(
        nameof(Column), typeof(string), typeof(GridCell), new PropertyMetadata(string.Empty));

    private Border? _pickerAffordance;
    private bool _isPointerOver;

    public GridCell()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
        Padding = new Thickness(8, 0, 8, 0);

        // The row container handles pointer input for focus, so listen even when it marked the tap handled.
        AddHandler(TappedEvent, new TappedEventHandler((_, _) => Host?.OnCellTapped(this)), handledEventsToo: true);

        // A recycled or unloaded cell starts without hover; the page's tracker marks it again on the next pointer move.
        DataContextChanged += (_, _) => SetPointerOver(false);
        Unloaded += (_, _) => SetPointerOver(false);
    }

    /// <summary>The column key (<see cref="GridColumns"/>).</summary>
    public string Column
    {
        get => (string)GetValue(ColumnProperty);
        set => SetValue(ColumnProperty, value);
    }

    public GridRowViewModel? Row => DataContext as GridRowViewModel;

    internal IGridCellHost? Host => GridVisualTree.FindHost(this);

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

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        _pickerAffordance = GridCellContent.FindPickerAffordance(newContent as FrameworkElement);
        UpdatePickerAffordance();
    }

    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        UpdatePickerAffordance();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        UpdatePickerAffordance();
    }

    /// <summary>Whether the page's hover tracker currently marks this cell as the one under the pointer.</summary>
    internal bool IsPointerOverCell => _isPointerOver;

    /// <summary>
    /// Set only by the page's single hover tracker, so at most one cell is hovered at a time; per-cell pointer exit
    /// events are not relied on.
    /// </summary>
    internal void SetPointerOver(bool isPointerOver)
    {
        if (_isPointerOver != isPointerOver)
        {
            _isPointerOver = isPointerOver;
            UpdatePickerAffordance();
        }
    }

    private void UpdatePickerAffordance()
    {
        if (_pickerAffordance is not { } affordance)
        {
            return;
        }

        affordance.Opacity = GridCellContent.IsPickerAffordanceShown(_isPointerOver, FocusState) ? 1 : 0;
        if (!_isPointerOver)
        {
            // The square's own pointer-over or pressed look never outlives the cell's hover.
            GridCellContent.ResetPickerAffordance(affordance);
        }
    }
}
