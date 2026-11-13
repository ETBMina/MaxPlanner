using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace MaxPlanner.Presentation.Grid;

/// <summary>Keyboard state and the double-click guard shared by the Grid page and its flyout editor.</summary>
internal static class GridInput
{
    /// <summary>
    /// Windows' default double-click time. A click-opened list or flyout ignores the dismissal caused by the
    /// second click of a double-click that started it (design §4.3 single-tap editing).
    /// </summary>
    public const long DoubleClickGuardMs = 500;

    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    public static bool IsWithinDoubleClickWindow(long? startedAt) => IsWithinDoubleClickWindow(startedAt, Environment.TickCount64);

    /// <param name="startedAt">
    /// When the list or flyout was opened by a click, in <see cref="Environment.TickCount64"/> milliseconds; null when it
    /// was opened another way (F2, double-tap, UI Automation, a reopen), which has no double-click to guard.
    /// </param>
    /// <param name="now">The current <see cref="Environment.TickCount64"/>.</param>
    internal static bool IsWithinDoubleClickWindow(long? startedAt, long now) => startedAt is { } at && now - at < DoubleClickGuardMs;
}
