using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The content of a page's accent "New …" button (Board, Grid, Checklists, Calendar): the Add glyph and the label, 8 epx
/// apart. The button keeps its own x:Uid, AutomationId, command, and Ctrl+N accelerator; the label text comes from
/// an x:Uid on this element (<c>Key.Text</c>).
/// </summary>
public sealed partial class NewButtonContent : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(NewButtonContent), new PropertyMetadata(string.Empty, (d, e) => ((NewButtonContent)d)._label.Text = e.NewValue as string ?? string.Empty));

    private readonly TextBlock _label = new();

    public NewButtonContent()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        Children.Add(new FontIcon { FontSize = (double)Microsoft.UI.Xaml.Application.Current.Resources["BodyIconSize"], Glyph = "" });
        Children.Add(_label);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>False leaves only the glyph, for a page that drops the label in a narrow window (Grid, design §4.3).</summary>
    public bool IsTextVisible
    {
        get => _label.Visibility == Visibility.Visible;
        set => _label.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
}
