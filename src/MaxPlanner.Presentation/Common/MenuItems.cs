using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Common;

/// <summary>Builds the items of a context menu made in code (a <see cref="MenuFlyout"/> or a submenu).</summary>
public static class MenuItems
{
    /// <summary>Adds a command item with a stable AutomationId (design §8.1) and, when given, a Segoe Fluent Icons glyph.</summary>
    public static MenuFlyoutItem AddItem(this IList<MenuFlyoutItemBase> items, string text, string automationId, Action action, bool isEnabled = true, string? glyph = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(action);
        var item = new MenuFlyoutItem { Text = text, IsEnabled = isEnabled };
        if (glyph is not null)
        {
            item.Icon = new FontIcon { Glyph = glyph };
        }

        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => action();
        items.Add(item);
        return item;
    }

    public static void AddSeparator(this IList<MenuFlyoutItemBase> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        items.Add(new MenuFlyoutSeparator());
    }
}
