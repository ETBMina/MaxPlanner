using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>An inline field error (design §5.0); collapsed when <see cref="Text"/> is empty.</summary>
public sealed partial class FieldError : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FieldError), new PropertyMetadata(null, (d, _) => ((FieldError)d).Apply()));

    public FieldError() => InitializeComponent();

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void Apply()
    {
        ErrorText.Text = Text ?? string.Empty;
        Visibility = string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
