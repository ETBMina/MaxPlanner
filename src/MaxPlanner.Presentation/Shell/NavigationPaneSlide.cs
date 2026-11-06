using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using XamlGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// Opens and closes the Left (expanded) NavigationView pane as one continuous resize. The stock SplitView transition
/// gives the content its final width at once and then slides it by the difference, so centred and right-aligned
/// content first jumps one way and then slides the other, and opening leaves a gap at the right edge. Here one eased
/// animation on the UI thread moves the pane's clip edge and the content's left edge together each frame (the
/// content's width follows through layout), with the stock duration and easing. Only the two Left inline transitions
/// change; the overlay modes (LeftCompact, LeftMinimal) and the settled visual states stay stock.
/// </summary>
internal sealed class NavigationPaneSlide
{
    // SplitView template parts and states (Microsoft.UI.Xaml generic.xaml).
    private const string OpenStateName = "OpenInlineLeft";
    private const string ClosedStateName = "ClosedCompactLeft";
    private const string ContentTransformName = "ContentTransform";
    private const string PaneClipTransformName = "PaneClipRectangleTransform";

    // SplitViewPaneAnimationOpenDuration; the stock transitions use it and the same KeySpline in both directions.
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(200);

    private readonly SplitView _splitView;
    private readonly FrameworkElement _contentRoot;
    private readonly TranslateTransform _slideClip;
    private bool _isSliding;
    private double _fromEdge;
    private double _toEdge;
    private double _edge;
    private TimeSpan _duration;
    private TimeSpan? _startTime;

    private NavigationPaneSlide(SplitView splitView, FrameworkElement contentRoot, TranslateTransform slideClip)
    {
        _splitView = splitView;
        _contentRoot = contentRoot;
        _slideClip = slideClip;
    }

    /// <summary>Raised when a slide starts and when it ends (true, then false); the content's width changes every frame between.</summary>
    public event EventHandler<bool>? SlidingChanged;

    /// <summary>Hooks the NavigationView's SplitView; returns null (stock behaviour) if its template is not the expected one.</summary>
    public static NavigationPaneSlide? TryAttach(NavigationView navigationView)
    {
        // The NavigationView may still be Collapsed (Ready not reached), so its templates may not be applied yet.
        navigationView.ApplyTemplate();
        if (FindNamedDescendant(navigationView, "RootSplitView") is not SplitView splitView)
        {
            return null;
        }

        splitView.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(splitView) == 0
            || VisualTreeHelper.GetChild(splitView, 0) is not XamlGrid { ColumnDefinitions.Count: > 0 } templateRoot
            || templateRoot.Children.OfType<FrameworkElement>().FirstOrDefault(e => e.Name == "ContentRoot") is not { } contentRoot
            || templateRoot.Children.OfType<FrameworkElement>().FirstOrDefault(e => e.Name == "PaneRoot") is not { Clip: RectangleGeometry { Transform: CompositeTransform paneClip } paneClipGeometry }
            || VisualStateManager.GetVisualStateGroups(templateRoot)
                .FirstOrDefault(g => g.States.Any(s => s.Name == OpenStateName)) is not { } group)
        {
            return null;
        }

        // Exactly the two stock Left inline transitions, so the stock content slide never runs alongside this one.
        var opening = group.Transitions.Where(t => (t.From, t.To) is (ClosedStateName, OpenStateName)).ToList();
        var closing = group.Transitions.Where(t => (t.From, t.To) is (OpenStateName, ClosedStateName)).ToList();
        if (opening is not [{ Storyboard: { } openStoryboard }] || closing is not [{ Storyboard: { } closeStoryboard }])
        {
            return null;
        }

        // The template's pane clip moves to the target state's value at once (no reveal animation, and no generated
        // one either), and the slide offsets it with a transform of its own each frame. The XAML clip, and so hit
        // testing, always matches the visible pane edge; settled, the offset is 0 and the template clip is stock.
        var closedClip = new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero) };
        ReplacePaneAnimations(openStoryboard, paneClip, new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 });
        ReplacePaneAnimations(closeStoryboard, paneClip, closedClip);

        var slideClip = new TranslateTransform();
        paneClipGeometry.Transform = null;
        paneClipGeometry.Transform = new TransformGroup { Children = { paneClip, slideClip } };

        var paneSlide = new NavigationPaneSlide(splitView, contentRoot, slideClip);

        // The closed clip is −(open − compact), as the template's NegativeOpenPaneLengthMinusCompactLength.
        void UpdateClosedClip() => closedClip.Value = splitView.CompactPaneLength - splitView.OpenPaneLength;
        UpdateClosedClip();
        splitView.RegisterPropertyChangedCallback(SplitView.OpenPaneLengthProperty, (_, _) => UpdateClosedClip());
        splitView.RegisterPropertyChangedCallback(SplitView.CompactPaneLengthProperty, (_, _) => UpdateClosedClip());

        // The slide starts when the visual state moves the content column (the pane column's width changes between
        // the compact and the open length). That is synchronous with the state change, before the frame renders;
        // PaneOpening, PaneClosing and CurrentStateChanging can come after a frame already shows the target state.
        var paneColumn = templateRoot.ColumnDefinitions[0];
        var lastWidth = paneColumn.Width.Value;
        paneColumn.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, (_, _) =>
        {
            var width = paneColumn.Width.Value;
            var previous = lastWidth;
            lastWidth = width;
            paneSlide.OnPaneColumnChanged(previous, width);
        });
        splitView.RegisterPropertyChangedCallback(SplitView.DisplayModeProperty, (_, _) => paneSlide.Stop());
        return paneSlide;
    }

    private static void ReplacePaneAnimations(Storyboard storyboard, CompositeTransform paneClip, DiscreteDoubleKeyFrame clipValue)
    {
        // The discrete column and visibility changes stay, so the content takes its target column at once.
        foreach (var animation in storyboard.Children
            .Where(t => Storyboard.GetTargetName(t) is ContentTransformName or PaneClipTransformName)
            .ToList())
        {
            storyboard.Children.Remove(animation);
        }

        var clip = new DoubleAnimationUsingKeyFrames();
        clip.KeyFrames.Add(clipValue);
        Storyboard.SetTarget(clip, paneClip);
        Storyboard.SetTargetProperty(clip, nameof(CompositeTransform.TranslateX));
        storyboard.Children.Add(clip);
    }

    private void OnPaneColumnChanged(double previous, double width)
    {
        // Only a toggle in Left (inline) mode on a laid-out SplitView; LeftCompact and LeftMinimal keep the stock
        // overlay transitions, and the collapsed shell before Ready settles without a slide.
        var openLength = _splitView.OpenPaneLength;
        var compactLength = _splitView.CompactPaneLength;
        if (_splitView.DisplayMode != SplitViewDisplayMode.CompactInline || _splitView.ActualWidth <= 0)
        {
            return;
        }

        if (IsNear(previous, compactLength) && IsNear(width, openLength))
        {
            Start(isOpening: true);
        }
        else if (IsNear(previous, openLength) && IsNear(width, compactLength))
        {
            Start(isOpening: false);
        }
    }

    private static bool IsNear(double value, double target) => Math.Abs(value - target) < 0.5;

    private void Start(bool isOpening)
    {
        var openLength = _splitView.OpenPaneLength;
        var compactLength = _splitView.CompactPaneLength;
        var travel = openLength - compactLength;
        var to = isOpening ? openLength : compactLength;

        // A toggle during a slide reverses from the current edge; otherwise the pane starts from the other end.
        var from = _isSliding ? Math.Clamp(_edge, compactLength, openLength) : isOpening ? compactLength : openLength;
        _toEdge = to;
        if (travel <= 0 || Math.Abs(to - from) < 0.5 || !MotionSettings.AnimationsEnabled)
        {
            // Nothing to slide, or Windows "Animation effects" is off (design §9): the target state as is.
            Stop();
            return;
        }

        _fromEdge = from;
        _duration = SlideDuration * (Math.Abs(to - from) / travel);
        _startTime = null;
        Apply(from);
        if (!_isSliding)
        {
            _isSliding = true;
            CompositionTarget.Rendering += OnRendering;
            SlidingChanged?.Invoke(this, true);
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (e is not RenderingEventArgs args)
        {
            return;
        }

        // The clock starts at the first rendered frame, so a slow first layout (the first open after launch, a
        // page's first layout at a new width) does not swallow the start of the slide.
        _startTime ??= args.RenderingTime;
        var progress = Math.Clamp((args.RenderingTime - _startTime.Value) / _duration, 0, 1);
        if (progress >= 1)
        {
            Stop();
            return;
        }

        Apply(_fromEdge + ((_toEdge - _fromEdge) * PaneEasing.Ease(progress)));
    }

    private void Apply(double edge)
    {
        // The template clip and the content column are already at the target edge (the transition set them), so the
        // same offset moves the pane's clip edge and the content's left edge back to the current edge; layout gives
        // the content the remaining width.
        _edge = edge;
        var offset = edge - _toEdge;
        _slideClip.X = offset;
        _contentRoot.Margin = new Thickness(offset, 0, 0, 0);
    }

    /// <summary>Ends a slide (or a display mode change interrupts it): the target state, exactly as the template sets it.</summary>
    private void Stop()
    {
        _slideClip.X = 0;
        _contentRoot.ClearValue(FrameworkElement.MarginProperty);
        if (_isSliding)
        {
            _isSliding = false;
            CompositionTarget.Rendering -= OnRendering;
            SlidingChanged?.Invoke(this, false);
        }
    }

    private static FrameworkElement? FindNamedDescendant(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            if (FindNamedDescendant(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
