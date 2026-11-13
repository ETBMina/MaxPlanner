using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// The Grid page's code-built menus (design §4.3): the column header menu, the column chooser, and the row menu.
/// Each builder takes the view model and the row or column it acts on; the page shows the result. The decisions
/// (which items, which are enabled) are the pure <see cref="HeaderMenuStateFor"/> and <see cref="RowMenuItemsFor"/>.
/// </summary>
internal static class GridMenus
{
    /// <summary>The header menu: Sort, Move left, Move right, Hide.</summary>
    /// <param name="index">The column's position among the displayed columns.</param>
    /// <param name="count">The number of displayed columns.</param>
    public static MenuFlyout HeaderMenu(GridPageViewModel viewModel, GridColumnViewModel column, int index, int count)
    {
        var strings = ResourceStrings.Shared;
        var (canMoveLeft, canMoveRight, canHide) = HeaderMenuStateFor(index, count);
        var menu = new MenuFlyout();
        menu.Items.AddItem(strings.GetString("Grid_HeaderMenuSort"), "Grid_HeaderMenuSortItem", () => viewModel.CycleSort(column.Key));
        menu.Items.AddItem(strings.GetString("Grid_HeaderMenuMoveLeft"), "Grid_HeaderMenuMoveLeftItem", () => viewModel.MoveColumn(column.Key, -1), canMoveLeft);
        menu.Items.AddItem(strings.GetString("Grid_HeaderMenuMoveRight"), "Grid_HeaderMenuMoveRightItem", () => viewModel.MoveColumn(column.Key, 1), canMoveRight);
        menu.Items.AddItem(strings.GetString("Grid_HeaderMenuHide"), "Grid_HeaderMenuHideItem", () => viewModel.SetVisible(column.Key, false), canHide);
        return menu;
    }

    /// <summary>A column moves only within the displayed columns, and the last displayed column cannot be hidden.</summary>
    internal static (bool CanMoveLeft, bool CanMoveRight, bool CanHide) HeaderMenuStateFor(int index, int count) =>
        (index > 0, index < count - 1, count > 1);

    /// <summary>
    /// The column chooser's content (design §4.3): a CheckBox per column plus Reset; visibility never changes the
    /// results (B-10).
    /// </summary>
    /// <param name="close">Closes the chooser after Reset.</param>
    public static FrameworkElement ColumnChooser(GridPageViewModel viewModel, Action close)
    {
        var strings = ResourceStrings.Shared;
        var list = new StackPanel { Spacing = 2 };
        foreach (var column in viewModel.Columns)
        {
            var check = new CheckBox { Content = column.Header, IsChecked = column.IsVisible };
            AutomationProperties.SetAutomationId(check, "Grid_ColumnCheck_" + column.Key);
            check.Click += (_, _) =>
            {
                viewModel.SetVisible(column.Key, check.IsChecked == true);
                check.IsChecked = column.IsVisible;
            };
            list.Children.Add(check);
        }

        var reset = new Button { Content = strings.GetString("Grid_ColumnsReset") };
        AutomationProperties.SetAutomationId(reset, "Grid_ColumnsResetButton");
        reset.Click += (_, _) =>
        {
            viewModel.ResetLayoutCommand.Execute(null);
            close();
        };
        var heading = new TextBlock { Text = strings.GetString("Grid_ColumnsHeading"), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        var scroller = new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new StackPanel { Spacing = 8, MinWidth = 220 };
        root.Children.Add(heading);
        root.Children.Add(scroller);
        root.Children.Add(reset);
        return root;
    }

    /// <summary>The row menu (design §4.3): Open, Open CR, Copy…, Copy ID, Link to CR…, Add Subtask card, Archive/Restore, Delete….</summary>
    public static MenuFlyout RowMenu(GridPageViewModel viewModel, GridRowViewModel row)
    {
        var strings = ResourceStrings.Shared;
        var menu = new MenuFlyout();
        foreach (var item in RowMenuItemsFor(row.Model))
        {
            switch (item)
            {
                case GridRowMenuItem.Open:
                    menu.Items.AddItem(strings.GetString("Board_MenuOpen"), "Grid_RowMenuOpenItem", () => viewModel.OpenRowAsync(row).Forget());
                    break;
                case GridRowMenuItem.OpenCr:
                    menu.Items.AddItem(strings.GetString("Board_MenuOpenCr"), "Grid_RowMenuOpenCrItem", () => viewModel.OpenCrAsync(row).Forget());
                    break;
                case GridRowMenuItem.Copy:
                    menu.Items.AddItem(strings.GetString("Board_MenuCopy"), "Grid_RowMenuCopyItem", () => viewModel.CopyRowAsync(row).Forget());
                    break;
                case GridRowMenuItem.CopyId:
                    menu.Items.AddItem(strings.GetString("Board_MenuCopyId"), "Grid_RowMenuCopyIdItem", () => ClipboardText.Set(viewModel.CopyIdText(row)));
                    break;
                case GridRowMenuItem.LinkToCr:
                    menu.Items.AddItem(strings.GetString("Grid_MenuLinkToCr"), "Grid_RowMenuLinkToCrItem", () => viewModel.LinkRowAsync(row).Forget());
                    break;
                case GridRowMenuItem.Restore:
                    menu.Items.AddItem(strings.GetString("Grid_MenuRestore"), "Grid_RowMenuRestoreItem", () => viewModel.RestoreRowAsync(row).Forget());
                    break;
                case GridRowMenuItem.AddSubtaskCard:
                    menu.Items.AddItem(strings.GetString("Board_MenuAddSubtaskCard"), "Grid_RowMenuAddSubtaskCardItem", () => viewModel.AddSubtaskCardAsync(row).Forget());
                    break;
                case GridRowMenuItem.Archive:
                    menu.Items.AddItem(strings.GetString("Board_MenuArchive"), "Grid_RowMenuArchiveItem", () => viewModel.ArchiveRowAsync(row).Forget());
                    break;
                case GridRowMenuItem.Delete:
                    menu.Items.AddSeparator();
                    menu.Items.AddItem(strings.GetString("Board_MenuDelete"), "Grid_RowMenuDeleteItem", () => viewModel.DeleteRowAsync(row).Forget());
                    break;
                default:
                    break;
            }
        }

        return menu;
    }

    /// <summary>
    /// The row menu's items in order. Open CR needs a linked CR; Copy… is for tasks; Link to CR… is offered for every
    /// unlinked task, archived ones included (B-03.6/B-05); an archived task offers Restore, an active one Add Subtask
    /// card and Archive. Delete… is last, after a separator.
    /// </summary>
    internal static IReadOnlyList<GridRowMenuItem> RowMenuItemsFor(WorkItemRecord model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var items = new List<GridRowMenuItem> { GridRowMenuItem.Open };
        if (model.Cr.ChangeRequestId is not null)
        {
            items.Add(GridRowMenuItem.OpenCr);
        }

        if (model.Task is not null)
        {
            items.Add(GridRowMenuItem.Copy);
        }

        items.Add(GridRowMenuItem.CopyId);
        if (model.Task is { } task)
        {
            if (task.Cr.ChangeRequestId is null)
            {
                items.Add(GridRowMenuItem.LinkToCr);
            }

            if (task.IsArchived)
            {
                items.Add(GridRowMenuItem.Restore);
            }
            else
            {
                items.Add(GridRowMenuItem.AddSubtaskCard);
                items.Add(GridRowMenuItem.Archive);
            }
        }

        items.Add(GridRowMenuItem.Delete);
        return items;
    }
}
