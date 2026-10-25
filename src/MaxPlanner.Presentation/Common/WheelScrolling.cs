using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MaxPlanner.Presentation.Common;

/// <summary>
/// Routes a mouse wheel by its axis (design §9 Mouse wheel): the normal wheel only ever scrolls vertically, and the
/// tilt wheel or Shift+wheel only ever scrolls horizontally, each to the nearest scroller that can move on that axis.
/// </summary>
/// <remarks>
/// <para>
/// WinUI has no tunnelling wheel event, and a ScrollViewer acts on the wheel in its own PointerWheelChanged
/// processing, which runs after its descendants' handlers and ignores an event they marked handled. So the handler
/// sits on the template root of each opted-in ScrollViewer (or of the ScrollViewer in an opted-in ListView's
/// template): it sees every wheel over the scroller, its content and its bars before the scroller does, and after any
/// inner scroller that consumed it.
/// </para>
/// <para>
/// On a scroller whose vertical axis is disabled (a horizontal-only scroller would otherwise turn the normal wheel
/// sideways), a normal wheel goes to the innermost vertical scroller under the pointer — doing nothing at its end, so
/// it never chains sideways — else to <see cref="VerticalWheelTargetProperty"/>, else to the nearest vertically
/// scrolling ancestor, else nowhere. A <see cref="VerticalWheelScrollsHorizontallyProperty"/> region (a Board column
/// header) instead scrolls the horizontal scroller. On every opted-in scroller, a horizontal wheel goes to the
/// nearest horizontally scrolling element under the pointer or ancestor, else nowhere, so an inner vertical list never
/// keeps it from the page's horizontal scroller. A normal wheel on a vertical scroller and Ctrl+wheel stay native.
/// Only mouse input is routed: touch, pen and precision touchpad gestures keep their native behaviour.
/// </para>
/// </remarks>
public static class WheelScrolling
{
    // One wheel notch (WHEEL_DELTA = 120) scrolls 120 epx, the step the Grid's row scroll bar forwarding already
    // used; a high-resolution wheel's smaller deltas scroll proportionally less.
    private const double OffsetPerWheelDelta = 1;

    public static readonly DependencyProperty RoutesMouseWheelProperty = DependencyProperty.RegisterAttached(
        "RoutesMouseWheel",
        typeof(bool),
        typeof(WheelScrolling),
        new PropertyMetadata(false, OnRoutesMouseWheelChanged));

    /// <summary>
    /// The element whose vertical scroller receives a normal wheel turned over a part of a horizontal-only scroller's
    /// content that has no vertical scroller of its own (a table header). The element may be a ScrollViewer or a
    /// control whose template contains one, such as a ListView.
    /// </summary>
    public static readonly DependencyProperty VerticalWheelTargetProperty = DependencyProperty.RegisterAttached(
        "VerticalWheelTarget",
        typeof(UIElement),
        typeof(WheelScrolling),
        new PropertyMetadata(null));

    /// <summary>
    /// Marks a region inside a horizontal-only scroller where the normal wheel scrolls that scroller horizontally,
    /// except over a vertical scroller within the region.
    /// </summary>
    public static readonly DependencyProperty VerticalWheelScrollsHorizontallyProperty = DependencyProperty.RegisterAttached(
        "VerticalWheelScrollsHorizontally",
        typeof(bool),
        typeof(WheelScrolling),
        new PropertyMetadata(false));

    private static readonly DependencyProperty HookedRootProperty = DependencyProperty.RegisterAttached(
        "HookedRoot",
        typeof(UIElement),
        typeof(WheelScrolling),
        new PropertyMetadata(null));

    private static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached(
        "Owner",
        typeof(ScrollViewer),
        typeof(WheelScrolling),
        new PropertyMetadata(null));

    // The offsets a wheel animation is still heading to; NaN once the view has settled.
    private static readonly DependencyProperty PendingHorizontalOffsetProperty = DependencyProperty.RegisterAttached(
        "PendingHorizontalOffset",
        typeof(double),
        typeof(WheelScrolling),
        new PropertyMetadata(double.NaN));

    private static readonly DependencyProperty PendingVerticalOffsetProperty = DependencyProperty.RegisterAttached(
        "PendingVerticalOffset",
        typeof(double),
        typeof(WheelScrolling),
        new PropertyMetadata(double.NaN));

    private static readonly DependencyProperty TracksPendingOffsetsProperty = DependencyProperty.RegisterAttached(
        "TracksPendingOffsets",
        typeof(bool),
        typeof(WheelScrolling),
        new PropertyMetadata(false));

    public static bool GetRoutesMouseWheel(FrameworkElement element) => (bool)element.GetValue(RoutesMouseWheelProperty);

    public static void SetRoutesMouseWheel(FrameworkElement element, bool value) => element.SetValue(RoutesMouseWheelProperty, value);

    public static UIElement? GetVerticalWheelTarget(ScrollViewer element) => (UIElement?)element.GetValue(VerticalWheelTargetProperty);

    public static void SetVerticalWheelTarget(ScrollViewer element, UIElement? value) => element.SetValue(VerticalWheelTargetProperty, value);

    public static bool GetVerticalWheelScrollsHorizontally(UIElement element) => (bool)element.GetValue(VerticalWheelScrollsHorizontallyProperty);

    public static void SetVerticalWheelScrollsHorizontally(UIElement element, bool value) => element.SetValue(VerticalWheelScrollsHorizontallyProperty, value);

    /// <summary>
    /// Scrolls <paramref name="scroller"/> along <paramref name="axis"/> by one wheel event, animated like the native
    /// wheel unless animations are off. A tilt to the right, and a normal wheel turned toward the user, move toward the
    /// end. Successive events build on the offset still being animated to, so a fast spin loses no distance.
    /// </summary>
    public static void ScrollByWheel(ScrollViewer scroller, Orientation axis, PointerPointProperties properties)
    {
        var horizontal = axis == Orientation.Horizontal;
        var change = (properties.IsHorizontalMouseWheel ? properties.MouseWheelDelta : -properties.MouseWheelDelta) * OffsetPerWheelDelta;

        // Mandatory snap points would pull a small step back to the current snap point, so step a viewport instead
        // (the Board at S shows one column per viewport, design §4.2).
        var snap = horizontal ? scroller.HorizontalSnapPointsType : scroller.VerticalSnapPointsType;
        if (snap is SnapPointsType.Mandatory or SnapPointsType.MandatorySingle)
        {
            change = Math.Sign(change) * (horizontal ? scroller.ViewportWidth : scroller.ViewportHeight);
        }

        if (!(bool)scroller.GetValue(TracksPendingOffsetsProperty))
        {
            scroller.SetValue(TracksPendingOffsetsProperty, true);
            scroller.ViewChanged += OnTrackedViewChanged;
        }

        var pendingProperty = horizontal ? PendingHorizontalOffsetProperty : PendingVerticalOffsetProperty;
        var pending = (double)scroller.GetValue(pendingProperty);
        var from = double.IsNaN(pending) ? (horizontal ? scroller.HorizontalOffset : scroller.VerticalOffset) : pending;
        var to = Math.Clamp(from + change, 0, horizontal ? scroller.ScrollableWidth : scroller.ScrollableHeight);
        scroller.SetValue(pendingProperty, to);
        var disableAnimation = !MotionSettings.AnimationsEnabled;
        _ = horizontal
            ? scroller.ChangeView(to, null, null, disableAnimation)
            : scroller.ChangeView(null, to, null, disableAnimation);
    }

    private static void OnRoutesMouseWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnHostLoaded;
        if (e.NewValue is true)
        {
            element.Loaded += OnHostLoaded;
            Hook(element);
        }
        else
        {
            Unhook(element);
        }
    }

    // The template is usually applied after the attached property is set during XAML parsing, so hooking is retried
    // on every Loaded; a re-applied template gets a new root and is re-hooked.
    private static void OnHostLoaded(object sender, RoutedEventArgs e) => Hook((FrameworkElement)sender);

    private static void Hook(FrameworkElement element)
    {
        var scroller = element as ScrollViewer ?? FindDescendantScroller(element);
        var root = scroller is not null && VisualTreeHelper.GetChildrenCount(scroller) > 0
            ? VisualTreeHelper.GetChild(scroller, 0) as UIElement
            : null;
        if (root is null || ReferenceEquals(root, element.GetValue(HookedRootProperty)))
        {
            return;
        }

        Unhook(element);
        root.SetValue(OwnerProperty, scroller);
        root.PointerWheelChanged += OnWheelChanged;
        element.SetValue(HookedRootProperty, root);
    }

    private static void Unhook(FrameworkElement element)
    {
        if (element.GetValue(HookedRootProperty) is UIElement previous)
        {
            previous.PointerWheelChanged -= OnWheelChanged;
            previous.ClearValue(OwnerProperty);
        }

        element.ClearValue(HookedRootProperty);
    }

    private static void OnWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled
            || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse
            || (e.KeyModifiers & VirtualKeyModifiers.Control) != 0
            || sender is not UIElement root
            || root.GetValue(OwnerProperty) is not ScrollViewer owner)
        {
            return;
        }

        var properties = e.GetCurrentPoint(root).Properties;
        if (properties.MouseWheelDelta == 0)
        {
            return;
        }

        // Shift+wheel keeps the Windows convention of scrolling horizontally.
        var horizontal = properties.IsHorizontalMouseWheel || (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (!horizontal && owner.VerticalScrollMode != ScrollMode.Disabled)
        {
            return;
        }

        e.Handled = true;
        if (FindTarget(e.OriginalSource as DependencyObject, root, owner, horizontal) is { } target)
        {
            ScrollByWheel(target.Scroller, target.Axis, properties);
        }
    }

    private static (ScrollViewer Scroller, Orientation Axis)? FindTarget(DependencyObject? source, UIElement root, ScrollViewer owner, bool horizontal)
    {
        var axis = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        var isOverVerticalScroller = false;
        var current = source;
        for (; current is not null && !ReferenceEquals(current, root); current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer scroller)
            {
                if (CanScroll(scroller, axis))
                {
                    return (scroller, axis);
                }

                // A vertical list too short to scroll still owns the normal wheel; it must not fall to the region.
                isOverVerticalScroller |= scroller.VerticalScrollMode != ScrollMode.Disabled;
            }
            else if (!horizontal && !isOverVerticalScroller && current is UIElement element && GetVerticalWheelScrollsHorizontally(element))
            {
                return CanScroll(owner, Orientation.Horizontal) ? (owner, Orientation.Horizontal) : null;
            }
        }

        // The source is not under the scroller, so the pointer is over a popup (a dropdown or flyout) that left the
        // wheel unhandled; nothing beneath the popup moves.
        if (current is null)
        {
            return null;
        }

        if (!horizontal && ResolveScroller(GetVerticalWheelTarget(owner)) is { } designated && CanScroll(designated, axis))
        {
            return (designated, axis);
        }

        for (DependencyObject? ancestor = owner; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is ScrollViewer scroller && CanScroll(scroller, axis))
            {
                return (scroller, axis);
            }
        }

        return null;
    }

    private static bool CanScroll(ScrollViewer scroller, Orientation axis) => axis == Orientation.Horizontal
        ? scroller.HorizontalScrollMode != ScrollMode.Disabled && scroller.ScrollableWidth > 0
        : scroller.VerticalScrollMode != ScrollMode.Disabled && scroller.ScrollableHeight > 0;

    private static ScrollViewer? ResolveScroller(DependencyObject? element) =>
        element as ScrollViewer ?? (element is null ? null : FindDescendantScroller(element));

    // A ListView's template scroller sits a few levels down, above its items, so the first one found is it.
    private static ScrollViewer? FindDescendantScroller(DependencyObject element)
    {
        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if ((child as ScrollViewer ?? FindDescendantScroller(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // Once a view settles, by this wheel or by any other input, the next wheel starts from the actual offset.
    private static void OnTrackedViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate && sender is ScrollViewer scroller)
        {
            scroller.ClearValue(PendingHorizontalOffsetProperty);
            scroller.ClearValue(PendingVerticalOffsetProperty);
        }
    }
}
