using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Common.Filters;

/// <summary>The filter context row under a page header (design §2.3 row 3, §2.6, §2.9).</summary>
public sealed partial class FilterChipsBar : UserControl
{
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.Register(
        nameof(Scope), typeof(FilterScopeViewModel), typeof(FilterChipsBar), new PropertyMetadata(null, (d, _) => ((FilterChipsBar)d).Bindings.Update()));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(string), typeof(FilterChipsBar), new PropertyMetadata(string.Empty, (d, _) => ((FilterChipsBar)d).ApplyAutomationIds()));

    public FilterChipsBar() => InitializeComponent();

    public FilterScopeViewModel? Scope
    {
        get => (FilterScopeViewModel?)GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    public string PageKey
    {
        get => (string)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    private void ApplyAutomationIds()
    {
        AutomationProperties.SetAutomationId(DashboardBar, PageKey + "_DashboardInfoBar");
        AutomationProperties.SetAutomationId(ReturnButton, PageKey + "_ReturnToDashboardButton");
        AutomationProperties.SetAutomationId(InvalidBar, PageKey + "_InvalidFilterInfoBar");
        AutomationProperties.SetAutomationId(ClearAllLink, PageKey + "_ClearAllFiltersLink");
    }

    // Design §8.1: {Page}_FilterChip_{Field}; the page key belongs to this control, not to the chip.
    private void OnChipLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FilterChipViewModel chip } element)
        {
            AutomationProperties.SetAutomationId(element, PageKey + "_FilterChip_" + chip.Key);
        }
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FilterChipViewModel chip })
        {
            chip.Remove();
        }
    }
}
