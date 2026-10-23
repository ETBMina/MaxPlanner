using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Dashboard;

/// <summary>A Dashboard bar chart (ADR 0011). <see cref="ChartKey"/> names it: <c>Dashboard_{Key}Chart</c> (design §8.1).</summary>
public sealed partial class BarChart : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(BarChartViewModel), typeof(BarChart), new PropertyMetadata(null, (d, _) => ((BarChart)d).Bindings.Update()));

    public static readonly DependencyProperty ChartKeyProperty = DependencyProperty.Register(
        nameof(ChartKey), typeof(string), typeof(BarChart), new PropertyMetadata(string.Empty, (d, e) => ((BarChart)d).ApplyAutomationIds((string)e.NewValue)));

    public BarChart() => InitializeComponent();

    public BarChartViewModel? ViewModel
    {
        get => (BarChartViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public string ChartKey
    {
        get => (string)GetValue(ChartKeyProperty);
        set => SetValue(ChartKeyProperty, value);
    }

    /// <summary>Table rows get their own AutomationId so the bar and table rows never share one.</summary>
    public static string TableId(string barId) => barId + "_Table";

    private void ApplyAutomationIds(string key)
    {
        AutomationProperties.SetAutomationId(ChartRoot, "Dashboard_" + key + "Chart");
        AutomationProperties.SetAutomationId(TableToggle, "Dashboard_" + key + "ChartTableToggle");
    }
}
