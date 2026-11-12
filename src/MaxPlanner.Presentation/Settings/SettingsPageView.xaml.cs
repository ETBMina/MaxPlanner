using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MaxPlanner.Presentation.Settings;

/// <summary>
/// The Settings page view. Code-behind handles view mechanics only (AGENTS.md §4): row menus, focus for inline
/// rename, Enter/Esc keys, and the S/M/L layout. Every change is made by <see cref="SettingsPageViewModel"/>.
/// </summary>
public sealed partial class SettingsPageView : UserControl, Shell.IPageView
{
    /// <summary>The row whose grip received the last pointer press; only that row may start a drag.</summary>
    private LookupRowViewModel? _gripPressedRow;

    /// <summary>The dragged row's index before the ListView moved it; -1 when no drag is in progress.</summary>
    private int _dragFromIndex = -1;

    public SettingsPageView(SettingsPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            // A collapsed (hidden) page has no width to adapt to; it applies the real width when shown again.
            if (e.NewSize.Width > 0)
            {
                ApplyBreakpoint(e.NewSize.Width);
            }
        };
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        AttachDataPage();
    }

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public SettingsPageViewModel ViewModel { get; }

    /// <summary>Gives the page title keyboard focus after navigation (design §7.7).</summary>
    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

    /// <inheritdoc/>
    public FrameworkElement HeaderElement => HeaderRoot;

    /// <inheritdoc/>
    public FrameworkElement? ContentElement => PageContent;

    /// <inheritdoc/>
    public bool FocusSearch() => false;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsPageViewModel.DataPage))
        {
            AttachDataPage();
        }
    }

    // The Data page view is created once, with the retained ViewModel, the first time the page is opened.
    private void AttachDataPage()
    {
        if (DataPageHost.Content is null && ViewModel.DataPage is { } page)
        {
            DataPageHost.Content = new DataPage.DataPageView(page);
        }
    }

    private void ApplyBreakpoint(double width)
    {
        // T-08.1/design §4.6: L caps cards at 1000 epx aligned left; M and S use the full width, and at S the
        // card's action wraps below its text.
        var window = PagePadding.WindowWidth(this, width);
        var padding = PagePadding.Side(window);
        HeaderRoot.Padding = new Thickness(padding, Math.Max(16, PagePadding.Top(window)), padding, 12);
        CapWrapper.Padding = new Thickness(padding, 0, padding, 32);
        ErrorBar.Margin = new Thickness(padding, 0, padding, 8);
        CardsPanel.HorizontalAlignment = width >= 1008 ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        var wrap = width < 640;
        foreach (var card in FindDescendants<SettingsCard>(CardsPanel))
        {
            card.IsWrapped = wrap;
        }
    }

    // ----- Lookup sections -----
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LookupSectionViewModel section })
        {
            ViewModel.AddAsync(section).Forget();
        }
    }

    private void OnAddBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is FrameworkElement { DataContext: LookupSectionViewModel section })
        {
            e.Handled = true;
            ViewModel.AddAsync(section).Forget();
        }
    }

    private void OnCopyIdFormatKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ViewModel.CommitCopyIdFormat();
        }
    }

    private void OnCopyIdFormatLostFocus(object sender, RoutedEventArgs e) => ViewModel.CommitCopyIdFormat();

    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LookupRowViewModel row } box)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                CommitRenameAsync(row, box).Forget();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                row.CancelRename();
                FocusMenuButton(box, row);
                break;
            default:
                break;
        }
    }

    private void OnRenameLostFocus(object sender, RoutedEventArgs e)
    {
        // Leaving the box attempts the rename; a refused name keeps the box and its error (as in the Grid).
        if (sender is FrameworkElement { DataContext: LookupRowViewModel { IsRenaming: true, IsBusy: false, RenameError: null } row })
        {
            ViewModel.CommitRenameAsync(row).Forget();
        }
    }

    private async Task CommitRenameAsync(LookupRowViewModel row, FrameworkElement box)
    {
        if (await ViewModel.CommitRenameAsync(row).ConfigureAwait(true))
        {
            FocusMenuButton(box, row);
        }
    }

    // ----- Drag reorder of ordered lookup rows (design 4.6, 9): native ListView reorder, grip-only. -----

    /// <summary>Remembers whether the last press on a row landed on its grip; a drag from anywhere else is cancelled.</summary>
    private void OnLookupRowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _gripPressedRow = sender is FrameworkElement { DataContext: LookupRowViewModel row } && IsOnGrip(e.OriginalSource as DependencyObject, (DependencyObject)sender)
            ? row
            : null;
    }

    private static bool IsOnGrip(DependencyObject? element, DependencyObject row)
    {
        for (; element is not null && element != row; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { Name: "LookupDragGrip" })
            {
                return true;
            }
        }

        return false;
    }

    private void OnLookupDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items is not [LookupRowViewModel { IsOrdered: true, IsBusy: false } row] || !ReferenceEquals(row, _gripPressedRow)
            || sender is not ListView { ItemsSource: IList<LookupRowViewModel> rows })
        {
            e.Cancel = true;
            return;
        }

        _dragFromIndex = rows.IndexOf(row);
    }

    /// <summary>The ListView has already moved the row; commit that move (or restore the committed order on refusal).</summary>
    private void OnLookupDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var from = _dragFromIndex;
        _dragFromIndex = -1;
        _gripPressedRow = null;
        if (from >= 0 && args.Items is [LookupRowViewModel row])
        {
            ViewModel.CommitDraggedRowAsync(row, from).Forget();
        }
    }

    private void OnLookupRowMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LookupRowViewModel row } button)
        {
            return;
        }

        var strings = ResourceStrings.Shared;
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string key, string id, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = strings.GetString(key), IsEnabled = enabled };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        void Flag(string key, string id, bool value, StatusFlagKind flag)
        {
            var item = new ToggleMenuFlyoutItem { Text = strings.GetString(key), IsChecked = value };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => ViewModel.SetFlagAsync(row, flag, !value).Forget();
            menu.Items.Add(item);
        }

        Item("Settings_MenuRename", "Settings_RowMenuRenameItem", () => BeginRename(button, row));
        if (row.Kind is LookupKind.TaskStatus or LookupKind.CrStatus or LookupKind.Priority or LookupKind.Person && !row.IsDefault)
        {
            Item(row.Kind == LookupKind.Person ? "Settings_MenuMakeDefaultOwner" : "Settings_MenuSetDefault", "Settings_RowMenuSetDefaultItem", () => ViewModel.SetAsDefaultAsync(row).Forget());
        }

        if (row.Kind is LookupKind.TaskStatus or LookupKind.CrStatus)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Flag("Settings_MenuFlagCompleted", "Settings_RowMenuCompletedItem", row.IsCompleted, StatusFlagKind.Completed);
            if (row.Kind == LookupKind.TaskStatus)
            {
                Flag("Settings_MenuFlagSetsStartDate", "Settings_RowMenuSetsStartDateItem", row.SetsStartDate, StatusFlagKind.SetsStartDate);
            }

            Flag("Settings_MenuFlagBacklog", "Settings_RowMenuBacklogItem", row.IsBacklog, StatusFlagKind.Backlog);
        }

        if (row.Kind is not (LookupKind.Person or LookupKind.Tag))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Item("Settings_MenuMoveUp", "Settings_RowMenuMoveUpItem", () => ViewModel.MoveAsync(row, -1).Forget(), row.CanMoveUp);
            Item("Settings_MenuMoveDown", "Settings_RowMenuMoveDownItem", () => ViewModel.MoveAsync(row, 1).Forget(), row.CanMoveDown);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Settings_MenuRemove", "Settings_RowMenuRemoveItem", () => ViewModel.RemoveAsync(row).Forget());
        menu.ShowAt(button);
    }

    private void BeginRename(FrameworkElement anchor, LookupRowViewModel row)
    {
        row.BeginRename();
        var container = FindAncestor<Microsoft.UI.Xaml.Controls.Grid>(anchor);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (container is not null && FindDescendants<TextBox>(container).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == row.RenameAutomationId) is { } box)
            {
                box.Focus(FocusState.Keyboard);
                box.SelectAll();
            }
        });
    }

    private static void FocusMenuButton(FrameworkElement inside, LookupRowViewModel row)
    {
        for (var current = VisualTreeHelper.GetParent(inside); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement element && AutomationProperties.GetAutomationId(element) == row.AutomationId)
            {
                FindDescendants<Button>(element).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == row.MenuAutomationId)?.Focus(FocusState.Keyboard);
                return;
            }
        }
    }

    // ----- Custom fields -----
    private void OnAddFieldClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FieldSectionViewModel section })
        {
            ViewModel.AddFieldAsync(section).Forget();
        }
    }

    private void OnFieldRowMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FieldRowViewModel row } button)
        {
            return;
        }

        var strings = ResourceStrings.Shared;
        var menu = new MenuFlyout();
        void Item(string key, string id, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = strings.GetString(key), IsEnabled = enabled };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Item("Settings_MenuEditField", "Settings_FieldMenuEditItem", () => ViewModel.EditFieldAsync(row).Forget());
        Item("Settings_MenuMoveUp", "Settings_FieldMenuMoveUpItem", () => ViewModel.MoveFieldAsync(row, -1).Forget(), row.CanMoveUp);
        Item("Settings_MenuMoveDown", "Settings_FieldMenuMoveDownItem", () => ViewModel.MoveFieldAsync(row, 1).Forget(), row.CanMoveDown);
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Settings_MenuRemoveField", "Settings_FieldMenuRemoveItem", () => ViewModel.DeactivateFieldAsync(row).Forget());
        menu.ShowAt(button);
    }

    private static T? FindAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
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

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
