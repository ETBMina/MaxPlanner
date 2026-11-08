using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MaxPlanner.Presentation.Common.Filters;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// A Priority box item (design §6.1): the priority glyph in its rank colour before the name, as on the Board chip and in
/// the filter menu. It renders its <see cref="FrameworkElement.DataContext"/>, an <see cref="IPriorityChoice"/>, so the
/// shared <c>PriorityChoiceTemplate</c> needs no bindings. The glyph is decorative; the accessible name is the text.
/// </summary>
public sealed partial class PriorityLabel : UserControl
{
    private readonly FontIcon _glyph = new() { FontSize = 12 };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public PriorityLabel()
    {
        AutomationProperties.SetAccessibilityView(_glyph, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _glyph, _text } };
        DataContextChanged += (_, args) => Show(args.NewValue as IPriorityChoice);
    }

    private void Show(IPriorityChoice? choice)
    {
        _text.Text = choice?.DisplayName ?? string.Empty;
        AutomationProperties.SetName(this, _text.Text);
        if (choice?.Level is { } level)
        {
            _glyph.Glyph = WorkflowGlyphs.Priority(level);
            _glyph.Style = FilterButton.GlyphStyle(MenuFields.PriorityTone(level));
            _glyph.Visibility = Visibility.Visible;
        }
        else
        {
            _glyph.Visibility = Visibility.Collapsed;
        }
    }
}
