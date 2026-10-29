using System.ComponentModel;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Presentation.Calendar;
using MaxPlanner.Presentation.Common.Controls;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Grid;

/// <summary>What the Grid's cells, rows, and headers ask of the page that hosts them. Implemented by the Grid page view.</summary>
internal interface IGridCellHost
{
    /// <summary>Raised when the displayed columns, their order, or the frozen-column offset changed.</summary>
    event EventHandler? DisplayLayoutChanged;

    GridPageViewModel? GridViewModel { get; }

    GridTable? Table { get; }

    IReadOnlyList<GridHeaderCell> HeaderCells { get; }

    /// <summary>The visible columns in display order (at S the Title column comes first and is frozen, design §4.3).</summary>
    IReadOnlyList<GridColumnViewModel> DisplayColumns { get; }

    /// <summary>The horizontal offset applied to the frozen first column so it stays in view; 0 when none is frozen.</summary>
    double FrozenOffset { get; }

    bool IsFirstColumnFrozen { get; }

    void OnCellKeyDown(GridCell cell, KeyRoutedEventArgs e);

    void OnCellPreviewKeyDown(GridCell cell, KeyRoutedEventArgs e);

    void OnCellCharacterReceived(GridCell cell, CharacterReceivedRoutedEventArgs e);

    void OnCellDoubleTapped(GridCell cell);

    /// <summary>A single tap on a cell: choice and flyout cells open their editor at once (design §4.3); other cells ignore it.</summary>
    void OnCellTapped(GridCell cell);

    /// <summary>UIA Invoke on a cell: opens its editor, like F2.</summary>
    void BeginEditFromAutomation(GridCell cell);

    /// <summary>UIA ValuePattern.SetValue on a cell: edits and commits through the same command path as typing.</summary>
    void SetValueFromAutomation(GridCell cell, string value);

    /// <summary>An inline cell editor lost focus: B-07 leaving an edited cell attempts a commit.</summary>
    void OnEditorLostFocus(Control editor);

    /// <summary>The row ⋯ button (design §4.3 row menu).</summary>
    void OnRowMenuRequested(GridRowViewModel row, FrameworkElement anchor);
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

        // The row container handles pointer input for focus, so listen even when it marked the tap handled.
        AddHandler(TappedEvent, new TappedEventHandler((_, _) => Host?.OnCellTapped(this)), handledEventsToo: true);
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
}

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

/// <summary>
/// Lays out one row's cells (or the header cells) side by side with the widths of the displayed columns, so the
/// header and every realized row share one layout. The frozen first column is translated by the horizontal
/// scroll offset and drawn above the others, which keeps it in view at S without a second scroll region.
/// </summary>
internal static class GridColumnLayout
{
    public const double RowHeight = 44;

    public static Size Measure(UIElementCollection children, IReadOnlyList<GridColumnViewModel> columns, double height)
    {
        var width = 0d;
        for (var i = 0; i < children.Count; i++)
        {
            var columnWidth = i < columns.Count ? columns[i].Width : 0;
            children[i].Measure(new Size(columnWidth, height));
            width += columnWidth;
        }

        return new Size(width, height);
    }

    public static void Arrange(UIElementCollection children, IReadOnlyList<GridColumnViewModel> columns, double height, double frozenOffset, bool isFrozen)
    {
        var x = 0d;
        for (var i = 0; i < children.Count; i++)
        {
            var columnWidth = i < columns.Count ? columns[i].Width : 0;
            children[i].Arrange(new Rect(x, 0, columnWidth, height));
            if (i == 0)
            {
                children[i].RenderTransform = isFrozen && frozenOffset > 0 ? new TranslateTransform { X = frozenOffset } : null;
                Canvas.SetZIndex(children[i], isFrozen ? 1 : 0);
            }

            x += columnWidth;
        }
    }
}

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

/// <summary>The header cells, laid out with the same widths and frozen column as the rows.</summary>
public sealed partial class GridHeaderPanel : Panel
{
    private IReadOnlyList<GridColumnViewModel> _columns = [];
    private double _frozenOffset;
    private bool _isFrozen;

    internal void Update(IReadOnlyList<GridColumnViewModel> columns, double frozenOffset, bool isFrozen)
    {
        _columns = columns;
        _frozenOffset = frozenOffset;
        _isFrozen = isFrozen;
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize) => GridColumnLayout.Measure(Children, _columns, 40);

    protected override Size ArrangeOverride(Size finalSize)
    {
        GridColumnLayout.Arrange(Children, _columns, 40, _frozenOffset, _isFrozen);
        return new Size(finalSize.Width, 40);
    }
}

/// <summary>Builds the display and inline editor elements of Grid cells (design §4.3 column table).</summary>
internal static class GridCellContent
{
    public const string EditorTag = "GridCellEditor";

    private const string ChevronDownGlyph = "";
    private const string CalendarGlyph = "";

    private static Style CellText => (Style)Microsoft.UI.Xaml.Application.Current.Resources["GridCellTextStyle"];

    private static Style SecondaryCellText => (Style)Microsoft.UI.Xaml.Application.Current.Resources["GridCellSecondaryTextStyle"];

    public static FrameworkElement CreateDisplay(GridRowViewModel row, GridColumnViewModel column, IGridCellHost host)
    {
        var text = new TextBlock { Style = row.IsArchived ? SecondaryCellText : CellText, Text = row.DisplayValue(column.Key) };
        var full = row.FullValue(column.Key);
        if (full.Length > 0)
        {
            ToolTipService.SetToolTip(text, full);
        }

        FrameworkElement content = column.Key switch
        {
            GridColumns.Kind => KindCell(row, text, host),
            GridColumns.Title when row.IsTitleLocked => LockedTitle(row, text),
            GridColumns.Archived when row.IsArchived => ArchivedChip(row.DisplayValue(column.Key)),
            _ when row.CanEdit(column.Key) && PickerGlyph(column) is { } glyph => PickerCell(text, glyph),
            _ when row.PeopleNames(column.Key) is { Count: > 0 } people => PeopleCell(text, people),
            _ => text,
        };

        // A flyout editor keeps the cell's display; the ring shows the command is running (ADR 0004 row 3).
        if (row.IsSaving && row.EditingColumn == column.Key)
        {
            var grid = new WinGrid { ColumnSpacing = 4 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ring = new ProgressRing { Width = 12, Height = 12, IsActive = true };
            WinGrid.SetColumn(ring, 1);
            grid.Children.Add(content);
            grid.Children.Add(ring);
            return grid;
        }

        return content;
    }

    /// <summary>
    /// The glyph that makes a cell look like its ComboBox (chevron) or date picker (calendar) at rest, or null for
    /// other columns. A look-alike display is used instead of a real control per row to keep virtualization cheap (T-08).
    /// </summary>
    private static string? PickerGlyph(GridColumnViewModel column) => column.EditorKind switch
    {
        GridEditorKind.Status or GridEditorKind.Priority => ChevronDownGlyph,
        GridEditorKind.Date => CalendarGlyph,
        GridEditorKind.Custom when column.Field?.Kind == FieldKind.Lookup => ChevronDownGlyph,
        GridEditorKind.Custom when column.Field?.Kind == FieldKind.Date => CalendarGlyph,
        _ => null,
    };

    /// <summary>Whether one tap opens the column's editor: a drop-down, a calendar, or a flyout (the caller checks editability).</summary>
    public static bool OpensOnTap(GridColumnViewModel column) => column.UsesFlyout || PickerGlyph(column) is not null;

    private static WinGrid PickerCell(TextBlock text, string glyph)
    {
        var grid = new WinGrid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = glyph, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["GridCellAffordanceGlyphStyle"] };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        WinGrid.SetColumn(icon, 1);
        grid.Children.Add(text);
        grid.Children.Add(icon);
        return grid;
    }

    /// <summary>
    /// Owner, Designers, Developers, and People fields: one avatar, or an overlapping stack for several people, before
    /// the names. The names stay as text (trimmed, full list in the tooltip), so the avatars only add a visual cue.
    /// </summary>
    private static WinGrid PeopleCell(TextBlock text, IReadOnlyList<string> people)
    {
        var grid = new WinGrid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement avatars = people.Count == 1
            ? new PersonAvatar { DisplayName = people[0], Diameter = PersonAvatar.SmallDiameter }
            : new PersonAvatarGroup { Names = people, Diameter = PersonAvatar.SmallDiameter };
        WinGrid.SetColumn(text, 1);
        grid.Children.Add(avatars);
        grid.Children.Add(text);
        return grid;
    }

    public static FrameworkElement CreateEditor(GridRowViewModel row, GridColumnViewModel column, IGridCellHost host)
    {
        var page = host.GridViewModel!;
        Control editor;
        FrameworkElement root;
        switch (column.EditorKind)
        {
            case GridEditorKind.Status:
                var status = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
                status.ItemsSource = page.StatusOptions;
                status.SelectedItem = row.DraftStatus;
                status.SelectionChanged += (_, _) =>
                {
                    if (status.SelectedItem is StatusOption option && row.EditingColumn == GridColumns.Status)
                    {
                        row.DraftStatus = option;
                    }
                };
                editor = status;
                root = status;
                break;
            case GridEditorKind.Priority:
                var priority = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
                priority.ItemsSource = page.PriorityOptions;
                priority.SelectedItem = row.DraftPriority;
                priority.SelectionChanged += (_, _) =>
                {
                    if (priority.SelectedItem is Tasks.PriorityOption option && row.EditingColumn == GridColumns.Priority)
                    {
                        row.DraftPriority = option;
                    }
                };
                editor = priority;
                root = priority;
                break;
            case GridEditorKind.Date:
                var picker = new CalendarDatePicker { Date = row.DraftDate, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
                picker.DateChanged += (_, args) => row.DraftDate = args.NewDate;
                var clear = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, IsTabStop = false };
                var clearName = new ResourceStrings().Format("Grid_ClearDateName", column.Header);
                AutomationProperties.SetName(clear, clearName);
                ToolTipService.SetToolTip(clear, clearName);
                clear.Click += (_, _) =>
                {
                    row.ClearDraftDate();
                    picker.Date = null;
                    picker.Focus(FocusState.Programmatic);
                };
                var dates = new WinGrid { ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
                dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                dates.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                WinGrid.SetColumn(clear, 1);
                dates.Children.Add(picker);
                dates.Children.Add(clear);
                editor = picker;
                root = dates;
                break;
            case GridEditorKind.Custom when row.DraftCustom is { } custom:
                (root, editor) = Common.Fields.CustomFieldEditors.Create(custom, "Grid", showHeader: false);
                break;
            default:
                var box = new TextBox { Text = row.DraftText, MaxLength = 250, VerticalAlignment = VerticalAlignment.Center };
                box.TextChanged += (_, _) => row.DraftText = box.Text;
                FollowRow(box, row, name =>
                {
                    if (name == nameof(GridRowViewModel.DraftText) && box.Text != row.DraftText)
                    {
                        box.Text = row.DraftText;
                    }
                    else if (name == nameof(GridRowViewModel.IsSaving))
                    {
                        box.IsReadOnly = row.IsSaving;
                    }
                });
                editor = box;
                root = box;
                break;
        }

        AutomationProperties.SetAutomationId(editor, "Grid_CellEditor");
        AutomationProperties.SetName(editor, column.Header);
        editor.LostFocus += (_, _) => host.OnEditorLostFocus(editor);

        var ringHost = new WinGrid { ColumnSpacing = 4, Tag = EditorTag };
        ringHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ringHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var progress = new ProgressRing { Width = 12, Height = 12, IsActive = row.IsSaving, Visibility = row.IsSaving ? Visibility.Visible : Visibility.Collapsed };
        FollowRow(progress, row, name =>
        {
            if (name == nameof(GridRowViewModel.IsSaving))
            {
                progress.IsActive = row.IsSaving;
                progress.Visibility = row.IsSaving ? Visibility.Visible : Visibility.Collapsed;
            }
        });
        WinGrid.SetColumn(progress, 1);
        ringHost.Children.Add(root);
        ringHost.Children.Add(progress);
        return ringHost;
    }

    /// <summary>
    /// Follows the row only while <paramref name="element"/> is in the tree: rows outlive their editors, so a
    /// handler left behind would keep every closed editor alive.
    /// </summary>
    private static void FollowRow(FrameworkElement element, GridRowViewModel row, Action<string?> onChanged)
    {
        void Handler(object? sender, PropertyChangedEventArgs e) => onChanged(e.PropertyName);
        row.PropertyChanged += Handler;
        element.Unloaded += (_, _) => row.PropertyChanged -= Handler;
    }

    private static WinGrid KindCell(GridRowViewModel row, TextBlock text, IGridCellHost host)
    {
        var grid = new WinGrid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new FontIcon { FontSize = 14, Glyph = row.KindGlyph };
        AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
        WinGrid.SetColumn(text, 1);

        // Design §4.3 row menu; keyboard users reach the same menu with Shift+F10 on any cell.
        var menu = new Button
        {
            Padding = new Thickness(4, 2, 4, 2),
            IsTabStop = false,
            Content = new FontIcon { FontSize = 12, Glyph = "" },
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["SubtleButtonStyle"],
        };
        AutomationProperties.SetAutomationId(menu, "Grid_RowMenuButton_" + row.Key.ToString("N"));
        AutomationProperties.SetName(menu, row.RowMenuName);
        ToolTipService.SetToolTip(menu, row.RowMenuName);
        menu.Click += (_, _) => host.OnRowMenuRequested(row, menu);
        WinGrid.SetColumn(menu, 2);
        grid.Children.Add(glyph);
        grid.Children.Add(text);
        grid.Children.Add(menu);
        return grid;
    }

    private static StackPanel LockedTitle(GridRowViewModel row, TextBlock text)
    {
        // B-02.3: the lock glyph and its tooltip say the title comes from the CR (or the parent task).
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var glyph = new FontIcon { FontSize = 12, Glyph = "" };
        AutomationProperties.SetName(glyph, row.TitleLockText);
        ToolTipService.SetToolTip(glyph, row.TitleLockText);
        panel.Children.Add(glyph);
        panel.Children.Add(text);
        return panel;
    }

    private static Border ArchivedChip(string label)
    {
        // B-05: archived rows carry an "Archived" chip (text, never color alone, P3).
        var chip = new Border { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ArchivedChipStyle"] };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var glyph = new FontIcon { Glyph = "", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ChipGlyphStyle"] };
        AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
        content.Children.Add(glyph);
        content.Children.Add(new TextBlock { Text = label, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ChipTextStyle"] });
        chip.Child = content;
        return chip;
    }
}

internal static class GridVisualTree
{
    public static IGridCellHost? FindHost(DependencyObject start)
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

internal sealed partial class GridHeaderCellAutomationPeer(GridHeaderCell owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
    public void Invoke() => owner.RequestSort();

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.HeaderItem;

    protected override string GetAutomationIdCore() => "Grid_Header_" + owner.Column;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);
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

    private static readonly Lazy<string> CellTypeName = new(() => new ResourceStrings().GetString("Grid_CellControlType"));
}

/// <summary>The resize grip of a header (pointer resize, design §4.3); reuses the Calendar's cursor host.</summary>
internal static class GridHeaderGrip
{
    public static CalendarResizeHandle Create(string headerName, Action<double> onDrag)
    {
        var handle = new CalendarResizeHandle { HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetAccessibilityView(handle.Thumb, AccessibilityView.Raw);
        ToolTipService.SetToolTip(handle, headerName);
        handle.Thumb.DragDelta += (_, e) => onDrag(e.HorizontalChange);
        return handle;
    }
}
