using System.ComponentModel;
using MaxPlanner.Presentation.Common.Controls;
using MaxPlanner.Presentation.Common.Options;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Grid;

/// <summary>Builds the display and inline editor elements of Grid cells (design §4.3 column table).</summary>
internal static class GridCellContent
{
    public const string EditorTag = "GridCellEditor";

    private const string ChevronDownGlyph = "";
    private const string CalendarGlyph = "";
    private const string PickerAffordanceTag = "GridPickerAffordance";
    private const string AffordanceStyle = "GridCellAffordanceStyle";
    private const string AffordancePointerOverStyle = "GridCellAffordancePointerOverStyle";
    private const string AffordancePressedStyle = "GridCellAffordancePressedStyle";
    private const string AffordanceGlyphStyle = "GridCellAffordanceGlyphStyle";
    private const string AffordanceGlyphPointerOverStyle = "GridCellAffordanceGlyphPointerOverStyle";

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
            _ when row.TagRefs(column.Key) is { Count: > 0 } tags => TagsCell(tags, full),
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
    /// The glyph that makes a cell look like its ComboBox (chevron) or date picker (calendar), or null for other
    /// columns. A look-alike display is used instead of a real control per row to keep virtualization cheap (T-08).
    /// </summary>
    private static string? PickerGlyph(GridColumnViewModel column) => column.PickerKind switch
    {
        GridPickerKind.DropDown => ChevronDownGlyph,
        GridPickerKind.Calendar => CalendarGlyph,
        _ => null,
    };

    /// <summary>Whether one tap opens the column's editor: a drop-down, a calendar, or a flyout (the caller checks editability).</summary>
    public static bool OpensOnTap(GridColumnViewModel column) => column.UsesFlyout || column.PickerKind != GridPickerKind.None;

    /// <summary>
    /// Whether a choice cell shows its picker affordance: while the pointer is over it, or while it has keyboard
    /// focus, so keyboard users get the same cue (hover is never the only one). Pointer and programmatic focus (the
    /// page refocusing a cell after a flyout) do not count, so glyphs never stay on cells the user is not at.
    /// </summary>
    public static bool IsPickerAffordanceShown(bool isPointerOver, FocusState focusState) =>
        isPointerOver || focusState == FocusState.Keyboard;

    /// <summary>The picker affordance square inside a display built by <see cref="CreateDisplay"/>, or null.</summary>
    public static Border? FindPickerAffordance(FrameworkElement? content)
    {
        if (content is Border { Tag: PickerAffordanceTag } affordance)
        {
            return affordance;
        }

        if (content is WinGrid grid)
        {
            foreach (var child in grid.Children)
            {
                if (FindPickerAffordance(child as FrameworkElement) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The value with a reserved 28-epx affordance column, so showing the glyph never moves or re-trims the text.
    /// The square starts hidden (the cell shows it, see <see cref="GridCell"/>) and tracks its own pointer-over and
    /// pressed looks; it never handles input, so a click or tap still reaches the cell and opens the editor.
    /// </summary>
    private static WinGrid PickerCell(TextBlock text, string glyph)
    {
        var grid = new WinGrid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = glyph, Style = Resource<Style>(AffordanceGlyphStyle) };
        var affordance = new Border { Tag = PickerAffordanceTag, Opacity = 0, Style = Resource<Style>(AffordanceStyle), Child = icon };
        AutomationProperties.SetAccessibilityView(affordance, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        affordance.PointerEntered += (_, _) => SetAffordanceLook(affordance, AffordancePointerOverStyle, isAccent: true);
        affordance.PointerPressed += (_, _) => SetAffordanceLook(affordance, AffordancePressedStyle, isAccent: true);
        affordance.PointerReleased += (_, _) => SetAffordanceLook(affordance, AffordancePointerOverStyle, isAccent: true);
        affordance.PointerExited += (_, _) => ResetPickerAffordance(affordance);
        affordance.PointerCanceled += (_, _) => ResetPickerAffordance(affordance);
        affordance.PointerCaptureLost += (_, _) => ResetPickerAffordance(affordance);
        WinGrid.SetColumn(affordance, 1);
        grid.Children.Add(text);
        grid.Children.Add(affordance);
        return grid;
    }

    /// <summary>Puts the affordance square back to its rest look (no fill, secondary glyph).</summary>
    public static void ResetPickerAffordance(Border affordance) => SetAffordanceLook(affordance, AffordanceStyle, isAccent: false);

    // Styles, not brushes, are swapped: a style's ThemeResource setters resolve per element, so the looks follow
    // the window's Light/Dark choice and High Contrast.
    private static void SetAffordanceLook(Border affordance, string styleKey, bool isAccent)
    {
        affordance.Style = Resource<Style>(styleKey);
        if (affordance.Child is FontIcon icon)
        {
            icon.Style = Resource<Style>(isAccent ? AffordanceGlyphPointerOverStyle : AffordanceGlyphStyle);
        }
    }

    private static T Resource<T>(string key) => (T)Microsoft.UI.Xaml.Application.Current.Resources[key];

    /// <summary>
    /// Owner, Designer, Developer, and People fields: one avatar, or an overlapping stack for several people, before
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

    /// <summary>
    /// Tags and custom Tags fields: one row of tag pills with "+n" for those that do not fit (design §4.3, §6.1). The
    /// cell's accessible name and the tooltip keep the comma-joined names.
    /// </summary>
    private static TagPillStrip TagsCell(IReadOnlyList<(Guid Id, string Name)> tags, string names)
    {
        var strip = new TagPillStrip(tags);
        ToolTipService.SetToolTip(strip, names);
        return strip;
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
                priority.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Application.Current.Resources["PriorityChoiceTemplate"];
                priority.ItemsSource = page.PriorityOptions;
                priority.SelectedItem = row.DraftPriority;
                priority.SelectionChanged += (_, _) =>
                {
                    if (priority.SelectedItem is PriorityOption option && row.EditingColumn == GridColumns.Priority)
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
                var clearName = ResourceStrings.Shared.Format("Grid_ClearDateName", column.Header);
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
