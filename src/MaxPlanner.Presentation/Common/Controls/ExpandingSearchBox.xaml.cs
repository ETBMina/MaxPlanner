using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The page search (design §2.3 row 2): a Search icon button that expands into an <see cref="AutoSuggestBox"/> with an
/// inline Clear button. It shows expanded whenever <see cref="Text"/> is not empty, so an active search is never
/// hidden. Its layout footprint is the icon or, expanded, up to <see cref="ExpandedWidth"/> of its own slot; place it
/// last in a left-aligned row whose remaining width it may use, and expanding never moves a neighbour. View-side only;
/// use on the UI thread.
/// </summary>
public sealed partial class ExpandingSearchBox : UserControl
{
    // Below this slot width the box overlays its neighbours instead of squeezing into the slot.
    private const double MinInlineWidth = 200;

    // Space reserved at the end of the text for the Clear button so long text never runs under it.
    private const double ClearButtonReserve = 30;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ExpandingSearchBox), new PropertyMetadata(string.Empty, OnTextPropertyChanged));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(ExpandingSearchBox), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SearchNameProperty = DependencyProperty.Register(
        nameof(SearchName), typeof(string), typeof(ExpandingSearchBox), new PropertyMetadata(string.Empty, OnAutomationChanged));

    public static readonly DependencyProperty ButtonToolTipProperty = DependencyProperty.Register(
        nameof(ButtonToolTip), typeof(string), typeof(ExpandingSearchBox), new PropertyMetadata(string.Empty, OnAutomationChanged));

    public static readonly DependencyProperty AutomationIdPrefixProperty = DependencyProperty.Register(
        nameof(AutomationIdPrefix), typeof(string), typeof(ExpandingSearchBox), new PropertyMetadata(string.Empty, OnAutomationChanged));

    public static readonly DependencyProperty ExpandedWidthProperty = DependencyProperty.Register(
        nameof(ExpandedWidth), typeof(double), typeof(ExpandingSearchBox), new PropertyMetadata(280d, (d, _) => ((ExpandingSearchBox)d).InvalidateMeasure()));

    private static UISettings? s_uiSettings;

    private bool _isExpanded;
    private bool _isOverlay;
    private bool _templatePartsAdjusted;
    private double _arrangedOffset = double.NaN;
    private double _arrangedParentWidth = double.NaN;
    private Storyboard? _revealStoryboard;

    public ExpandingSearchBox()
    {
        InitializeComponent();
        ApplyAutomation();

        // Handled events too: the box may mark Esc handled itself (it restores the typed text) before it bubbles.
        Box.AddHandler(KeyDownEvent, new KeyEventHandler(OnBoxKeyDown), true);
    }

    /// <summary>The search text; bind it TwoWay to the page's search property. Non-empty text keeps the box expanded.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>The accessible name of both the Search button and the search box, such as "Search tasks".</summary>
    public string SearchName
    {
        get => (string)GetValue(SearchNameProperty);
        set => SetValue(SearchNameProperty, value);
    }

    /// <summary>The Search button tooltip, including its shortcut, such as "Search tasks (Ctrl+F)".</summary>
    public string ButtonToolTip
    {
        get => (string)GetValue(ButtonToolTipProperty);
        set => SetValue(ButtonToolTipProperty, value);
    }

    /// <summary>
    /// The page key for UI Automation IDs: <c>{prefix}_SearchButton</c>, <c>{prefix}_SearchBox</c> and
    /// <c>{prefix}_SearchClearButton</c> (design §8).
    /// </summary>
    public string AutomationIdPrefix
    {
        get => (string)GetValue(AutomationIdPrefixProperty);
        set => SetValue(AutomationIdPrefixProperty, value);
    }

    /// <summary>The expanded width in epx when the slot allows it (default 280).</summary>
    public double ExpandedWidth
    {
        get => (double)GetValue(ExpandedWidthProperty);
        set => SetValue(ExpandedWidthProperty, value);
    }

    public bool IsExpanded => _isExpanded;

    /// <summary>Ctrl+F (design §7.1): expands the search and moves keyboard focus into the box.</summary>
    public void FocusSearch() => Expand(focusBox: true);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_isExpanded)
        {
            Root.Measure(availableSize);
            return Root.DesiredSize;
        }

        // The footprint never exceeds the slot, so a wider box (overlay) cannot reflow the row.
        var width = double.IsInfinity(availableSize.Width) ? ExpandedWidth : Math.Min(ExpandedWidth, availableSize.Width);
        Root.Measure(new Size(width, availableSize.Height));
        return new Size(width, Root.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var boxWidth = finalSize.Width;
        var left = 0d;
        var overlay = false;
        var offset = ActualOffset.X;
        var parentWidth = VisualTreeHelper.GetParent(this) is FrameworkElement parent ? parent.ActualWidth : finalSize.Width;
        if (_isExpanded && finalSize.Width < MinInlineWidth)
        {
            // Too narrow to type in: overlay the neighbours in the parent row, leftward first, then rightward. Its own
            // slot does not move when it expands, so the offset from the previous arrange pass is still valid.
            var roomLeft = Math.Max(0, offset);
            var roomRight = Math.Max(0, parentWidth - offset - finalSize.Width);
            boxWidth = Math.Min(Math.Max(ExpandedWidth, finalSize.Width), finalSize.Width + roomLeft + roomRight);
            left = Math.Min(roomLeft, boxWidth - finalSize.Width);
            overlay = boxWidth > finalSize.Width;
            Root.Measure(new Size(boxWidth, finalSize.Height));
        }

        Root.Arrange(new Rect(-left, 0, boxWidth, finalSize.Height));
        SetOverlay(overlay);

        // The first pass (or a parent resize) may have used a stale offset; arrange once more with the settled one.
        if (_isExpanded && (offset != _arrangedOffset || parentWidth != _arrangedParentWidth))
        {
            _arrangedOffset = offset;
            _arrangedParentWidth = parentWidth;
            DispatcherQueue.TryEnqueue(InvalidateArrange);
        }

        return finalSize;
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ExpandingSearchBox)d;
        var text = e.NewValue as string ?? string.Empty;
        if (control.Box.Text != text)
        {
            control.Box.Text = text;
        }

        // B-10: a restored or drill-down search shows expanded; an externally cleared one collapses unless in use.
        if (text.Length > 0)
        {
            control.Expand(focusBox: false);
        }
        else if (!control.ContainsFocus())
        {
            control.Collapse(focusButton: false);
        }
    }

    private static void OnAutomationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ExpandingSearchBox)d).ApplyAutomation();

    private void ApplyAutomation()
    {
        AutomationProperties.SetName(SearchButton, SearchName);
        AutomationProperties.SetName(Box, SearchName);
        ToolTipService.SetToolTip(SearchButton, string.IsNullOrEmpty(ButtonToolTip) ? null : ButtonToolTip);
        var prefix = AutomationIdPrefix;
        AutomationProperties.SetAutomationId(SearchButton, prefix + "_SearchButton");
        AutomationProperties.SetAutomationId(Box, prefix + "_SearchBox");
        AutomationProperties.SetAutomationId(ClearButton, prefix + "_SearchClearButton");
    }

    private void Expand(bool focusBox)
    {
        if (!_isExpanded)
        {
            _isExpanded = true;
            Canvas.SetZIndex(this, 1);
            SearchButton.Visibility = Visibility.Collapsed;
            Surface.Visibility = Visibility.Visible;
            InvalidateMeasure();
            PlayReveal();
        }

        if (focusBox)
        {
            // The box becomes visible in this layout pass; focus it once it can take focus.
            DispatcherQueue.TryEnqueue(() => Box.Focus(FocusState.Keyboard));
        }
    }

    private void Collapse(bool focusButton)
    {
        if (!_isExpanded)
        {
            return;
        }

        _isExpanded = false;
        _arrangedOffset = double.NaN;
        _arrangedParentWidth = double.NaN;
        _revealStoryboard?.Stop();
        SearchButton.Visibility = Visibility.Visible;
        Surface.Visibility = Visibility.Collapsed;
        Canvas.SetZIndex(this, 0);
        SetOverlay(false);
        InvalidateMeasure();
        if (focusButton)
        {
            DispatcherQueue.TryEnqueue(() => SearchButton.Focus(FocusState.Keyboard));
        }
    }

    // Clearing goes through Text so the page's search (and its filters) resets exactly like an emptied box. It
    // collapses first so the text change does not collapse it without returning focus to the Search button.
    private void ClearAndCollapse()
    {
        Collapse(focusButton: true);
        Text = string.Empty;
    }

    private void SetOverlay(bool overlay)
    {
        if (overlay != _isOverlay)
        {
            _isOverlay = overlay;
            VisualStateManager.GoToState(this, overlay ? "Overlay" : "Inline", false);
        }
    }

    private void PlayReveal()
    {
        s_uiSettings ??= new UISettings();
        if (!s_uiSettings.AnimationsEnabled)
        {
            Surface.Opacity = 1;
            return;
        }

        if (_revealStoryboard is null)
        {
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(167)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, Surface);
            Storyboard.SetTargetProperty(fade, nameof(Opacity));
            _revealStoryboard = new Storyboard();
            _revealStoryboard.Children.Add(fade);
        }

        _revealStoryboard.Begin();
    }

    private bool ContainsFocus()
    {
        if (XamlRoot is null)
        {
            return false;
        }

        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, this))
            {
                return true;
            }
        }

        return false;
    }

    private void OnSearchButtonClick(object sender, RoutedEventArgs e) => Expand(focusBox: true);

    private void OnClearButtonClick(object sender, RoutedEventArgs e) => ClearAndCollapse();

    private void OnBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (Text != sender.Text)
        {
            Text = sender.Text;
        }
    }

    private void OnBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ClearAndCollapse();
        }
    }

    // Losing focus with an empty box collapses it; an active search stays visible (design §2.3).
    private void OnPartLostFocus(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isExpanded && string.IsNullOrEmpty(Text) && !ContainsFocus())
            {
                Collapse(focusButton: false);
            }
        });

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_templatePartsAdjusted)
        {
            _templatePartsAdjusted = AdjustTemplateParts();
        }
    }

    /// <summary>
    /// Hides the text box's own delete button (the inline Clear button replaces it, and two X buttons would show while
    /// focused) and reserves end padding for the Clear button. Template parts of the WinUI 2.3 AutoSuggestBox text box
    /// style; if a future template renames them, the box still works and only shows both buttons.
    /// </summary>
    private bool AdjustTemplateParts()
    {
        var deleteButton = FindDescendant(Box, "DeleteButton");
        var content = FindDescendant(Box, "ContentElement") as ScrollViewer;
        if (deleteButton is null || content is null)
        {
            return false;
        }

        // The template shows this button through a Visibility animation, so it is neutralized by size instead.
        deleteButton.Width = 0;
        deleteButton.Opacity = 0;
        deleteButton.IsHitTestVisible = false;
        var padding = content.Padding;
        content.Padding = new Thickness(padding.Left, padding.Top, padding.Right + ClearButtonReserve, padding.Bottom);
        return true;
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            if (FindDescendant(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
