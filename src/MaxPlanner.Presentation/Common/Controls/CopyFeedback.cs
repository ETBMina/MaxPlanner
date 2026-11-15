using System.Runtime.CompilerServices;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The "Copied" hint a copy button shows after a successful copy: the button's tooltip is swapped for one that reads
/// "Copied" and is opened at the button for <see cref="CopyFeedbackRules.HintDuration"/>, then the button's own tooltip
/// comes back. A tooltip never takes focus, so keyboard and pointer users stay where they are. Screen readers hear the
/// copy through the app's announcement instead (the caller announces it).
/// </summary>
public static class CopyFeedback
{
    private static readonly ConditionalWeakTable<FrameworkElement, HintState> States = new();

    /// <summary>Shows the hint at <paramref name="anchor"/>; a repeated copy restarts the timer.</summary>
    public static void Show(FrameworkElement anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (!States.TryGetValue(anchor, out var state))
        {
            state = new HintState(ToolTipService.GetToolTip(anchor), anchor.DispatcherQueue.CreateTimer());
            state.Timer.IsRepeating = false;
            state.Timer.Interval = CopyFeedbackRules.HintDuration;
            state.Timer.Tick += (_, _) => Hide(anchor);
            States.Add(anchor, state);
        }

        state.Tip.Content = ResourceStrings.Shared.GetString("Copy_Hint");
        ToolTipService.SetToolTip(anchor, state.Tip);
        state.Tip.IsOpen = true;
        state.Timer.Stop();
        state.Timer.Start();
    }

    // The button's own tooltip comes back unless a binding has replaced the hint in the meantime.
    private static void Hide(FrameworkElement anchor)
    {
        if (!States.TryGetValue(anchor, out var state))
        {
            return;
        }

        state.Tip.IsOpen = false;
        if (ReferenceEquals(ToolTipService.GetToolTip(anchor), state.Tip))
        {
            ToolTipService.SetToolTip(anchor, state.Original);
        }

        States.Remove(anchor);
    }

    private sealed class HintState(object? original, DispatcherQueueTimer timer)
    {
        public object? Original { get; } = original;

        public DispatcherQueueTimer Timer { get; } = timer;

        public ToolTip Tip { get; } = new() { Placement = PlacementMode.Top };
    }
}
