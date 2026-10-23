using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace MaxPlanner.Presentation.Settings;

/// <summary>
/// The native settings card of ADR 0021 (design §4.6): icon, header, description, and a right-aligned action slot
/// on the card surface. It is not clickable itself; actions are explicit controls in <see cref="Action"/>, so UIA
/// Invoke works. Its accessible name is the header. At S the action wraps below the text (<see cref="IsWrapped"/>).
/// </summary>
[ContentProperty(Name = nameof(Action))]
public sealed partial class SettingsCard : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty, (d, _) => ((SettingsCard)d).ApplyText()));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty, (d, _) => ((SettingsCard)d).ApplyText()));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty, (d, _) => ((SettingsCard)d).ApplyText()));

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(UIElement), typeof(SettingsCard), new PropertyMetadata(null, (d, _) => ((SettingsCard)d).ApplyAction()));

    public static readonly DependencyProperty IsWrappedProperty = DependencyProperty.Register(
        nameof(IsWrapped), typeof(bool), typeof(SettingsCard), new PropertyMetadata(false, (d, _) => ((SettingsCard)d).ApplyLayout()));

    public static readonly DependencyProperty IsDescriptionSelectableProperty = DependencyProperty.Register(
        nameof(IsDescriptionSelectable), typeof(bool), typeof(SettingsCard), new PropertyMetadata(false, (d, _) => ((SettingsCard)d).ApplyText()));

    private readonly Microsoft.UI.Xaml.Controls.Grid _layout = new() { ColumnSpacing = 16, RowSpacing = 8 };
    private readonly FontIcon _icon = new() { FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ContentPresenter _action = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

    public SettingsCard()
    {
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetAccessibilityView(_icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _header.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"];

        // A style with a ThemeResource foreground, so the secondary text follows the window's theme (design §3).
        _description.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["PageSubtitleTextStyle"];
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_header);
        text.Children.Add(_description);
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(text, 1);
        _layout.Children.Add(_icon);
        _layout.Children.Add(text);
        _layout.Children.Add(_action);
        Content = new Border { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["SettingsCardBorderStyle"], Child = _layout };
        ApplyLayout();
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>A Segoe Fluent Icons glyph (design §6.4).</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public UIElement? Action
    {
        get => (UIElement?)GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    /// <summary>T-08.1 S breakpoint: the action moves below the header and spans the card.</summary>
    public bool IsWrapped
    {
        get => (bool)GetValue(IsWrappedProperty);
        set => SetValue(IsWrappedProperty, value);
    }

    /// <summary>For values the user may copy, such as the data folder path (design §4.6).</summary>
    public bool IsDescriptionSelectable
    {
        get => (bool)GetValue(IsDescriptionSelectableProperty);
        set => SetValue(IsDescriptionSelectableProperty, value);
    }

    private void ApplyText()
    {
        _header.Text = Header;
        _description.Text = Description;
        _description.IsTextSelectionEnabled = IsDescriptionSelectable;
        _description.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        _icon.Glyph = Glyph;
        _icon.Visibility = string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(this, Header);
    }

    private void ApplyAction() => _action.Content = Action;

    private void ApplyLayout()
    {
        Microsoft.UI.Xaml.Controls.Grid.SetRow(_action, IsWrapped ? 1 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(_action, IsWrapped ? 1 : 2);
        _action.HorizontalAlignment = IsWrapped ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }
}
