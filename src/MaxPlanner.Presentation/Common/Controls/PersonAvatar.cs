using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// A person's avatar: a filled circle with their initials in a stable per-person color (<see cref="PersonAvatarIdentity"/>).
/// Two elements (this panel and one TextBlock), no template, so it stays cheap in virtualized rows and cards.
/// </summary>
/// <remarks>
/// <para>
/// Next to the visible name the avatar is decorative and hidden from UI Automation (<see cref="IsDecorative"/>, the
/// default). Shown alone, it is an Image named after the person, with the name as its tooltip.
/// </para>
/// <para>
/// High Contrast (design §3 rule 3) drops the custom palette: the local fill is cleared so <c>PersonAvatarStyle</c>
/// applies ButtonFace with a ButtonText border and initials. The check runs on load and on each theme change, as the
/// Calendar palette does.
/// </para>
/// </remarks>
public sealed partial class PersonAvatar : WinGrid
{
    public const double SmallDiameter = 20;
    public const double DefaultDiameter = 24;

    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName), typeof(string), typeof(PersonAvatar), new PropertyMetadata(string.Empty, (d, _) => ((PersonAvatar)d).UpdateIdentity()));

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(PersonAvatar), new PropertyMetadata(DefaultDiameter, (d, _) => ((PersonAvatar)d).UpdateSize()));

    public static readonly DependencyProperty IsDecorativeProperty = DependencyProperty.Register(
        nameof(IsDecorative), typeof(bool), typeof(PersonAvatar), new PropertyMetadata(true, (d, _) => ((PersonAvatar)d).UpdateAccessibility()));

    private const string PersonGlyph = "";

    // One shared instance; its HighContrastChanged event cannot be subscribed in a desktop app (no CoreWindow), so the
    // flag is read when an avatar is restyled.
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();
    private static readonly SolidColorBrush[] Fills = [.. PersonAvatarIdentity.Palette.Select(c => new SolidColorBrush(ColorHelper.FromArgb(255, c.R, c.G, c.B)))];
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons");

    private readonly TextBlock _initials = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeights.SemiBold,
        TextLineBounds = TextLineBounds.Tight,
        IsTextScaleFactorEnabled = false,
        IsHitTestVisible = false,
    };

    public PersonAvatar()
    {
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        Style = (Style)resources["PersonAvatarStyle"];
        _initials.Style = (Style)resources["PersonAvatarTextStyle"];
        BackgroundSizing = BackgroundSizing.OuterBorderEdge;
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(_initials, AccessibilityView.Raw);
        Children.Add(_initials);
        Loaded += (_, _) => UpdateFill();
        ActualThemeChanged += (_, _) => UpdateFill();
        UpdateSize();
        UpdateIdentity();
        UpdateAccessibility();
    }

    /// <summary>The person's display name; the initials and color follow it, so a rename restyles the avatar.</summary>
    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    /// <summary>The circle's size in epx: <see cref="SmallDiameter"/> for dense rows and chips, <see cref="DefaultDiameter"/> otherwise.</summary>
    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    /// <summary>True (the default) when the name is shown beside the avatar, so UI Automation and tooltips skip it.</summary>
    public bool IsDecorative
    {
        get => (bool)GetValue(IsDecorativeProperty);
        set => SetValue(IsDecorativeProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PersonAvatarAutomationPeer(this);

    private void UpdateSize()
    {
        var diameter = Diameter;
        Width = diameter;
        Height = diameter;
        CornerRadius = new CornerRadius(diameter / 2);
        _initials.FontSize = PersonAvatarIdentity.FontSizeFor(diameter);
    }

    private void UpdateIdentity()
    {
        var name = DisplayName;
        var initials = PersonAvatarIdentity.Initials(name);

        // A name without letters or digits ("—") still gets a circle, with the Person glyph instead of initials.
        if (initials.Length == 0)
        {
            _initials.FontFamily = IconFont;
        }
        else
        {
            _initials.ClearValue(TextBlock.FontFamilyProperty);
        }

        _initials.Text = initials.Length == 0 ? PersonGlyph : initials;
        UpdateFill();
        UpdateAccessibility();
    }

    private void UpdateFill()
    {
        if (Accessibility.HighContrast)
        {
            ClearValue(BackgroundProperty);
        }
        else
        {
            Background = Fills[PersonAvatarIdentity.PaletteIndex(DisplayName)];
        }
    }

    private void UpdateAccessibility()
    {
        var name = IsDecorative ? null : DisplayName;
        AutomationProperties.SetAccessibilityView(this, IsDecorative ? AccessibilityView.Raw : AccessibilityView.Content);
        AutomationProperties.SetName(this, name ?? string.Empty);
        ToolTipService.SetToolTip(this, string.IsNullOrEmpty(name) ? null : name);
    }

    private sealed partial class PersonAvatarAutomationPeer(PersonAvatar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(PersonAvatar);
    }
}
