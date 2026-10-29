using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// Overlapping avatars for a multi-person field, then a "+N" circle for the rest. It is decorative: every surface that
/// uses it shows the names as text beside it, so UI Automation skips it.
/// </summary>
public sealed partial class PersonAvatarGroup : StackPanel
{
    /// <summary>Circles shown at most, the "+N" overflow included.</summary>
    public const int MaxVisible = 3;

    public static readonly DependencyProperty NamesProperty = DependencyProperty.Register(
        nameof(Names), typeof(IReadOnlyList<string>), typeof(PersonAvatarGroup), new PropertyMetadata(null, (d, _) => ((PersonAvatarGroup)d).Rebuild()));

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(PersonAvatarGroup), new PropertyMetadata(PersonAvatar.SmallDiameter, (d, _) => ((PersonAvatarGroup)d).Rebuild()));

    private static readonly Lazy<ResourceStrings> Strings = new(() => new ResourceStrings());

    public PersonAvatarGroup()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    public IReadOnlyList<string>? Names
    {
        get => (IReadOnlyList<string>?)GetValue(NamesProperty);
        set => SetValue(NamesProperty, value);
    }

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        var names = Names ?? [];
        var visible = PersonAvatarIdentity.VisibleCount(names.Count, MaxVisible);
        // A small overlap keeps the earlier circle's initials readable under the next one.
        var overlap = -Math.Round(Diameter * 0.15);
        for (var i = 0; i < visible; i++)
        {
            Add(new PersonAvatar { DisplayName = names[i], Diameter = Diameter });
        }

        if (names.Count > visible)
        {
            Add(Overflow(names.Count - visible));
        }

        void Add(FrameworkElement element)
        {
            // Later circles sit on earlier ones; the ring style separates them with the surface color.
            if (Children.Count > 0)
            {
                element.Margin = new Thickness(overlap, 0, 0, 0);
            }

            if (element is PersonAvatar)
            {
                element.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["PersonAvatarStackedStyle"];
            }

            Children.Add(element);
        }
    }

    private WinGrid Overflow(int hidden)
    {
        var circle = new WinGrid
        {
            Width = Diameter,
            Height = Diameter,
            CornerRadius = new CornerRadius(Diameter / 2),
            BackgroundSizing = BackgroundSizing.OuterBorderEdge,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["PersonAvatarOverflowStyle"],
        };
        circle.Children.Add(new TextBlock
        {
            Text = Strings.Value.Format("PersonAvatar_Overflow", hidden),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = PersonAvatarIdentity.FontSizeFor(Diameter),
            TextLineBounds = TextLineBounds.Tight,
            IsTextScaleFactorEnabled = false,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["PersonAvatarOverflowTextStyle"],
        });
        return circle;
    }
}
