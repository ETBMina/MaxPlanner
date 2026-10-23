using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Checklists;

/// <summary>Chooses the General to-dos card or a task group card for the Checklists repeater (design §4.5).</summary>
public sealed partial class ChecklistCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GeneralTemplate { get; set; }

    public DataTemplate? GroupTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is GeneralTodosViewModel ? GeneralTemplate : GroupTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
