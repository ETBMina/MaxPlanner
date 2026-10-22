using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GridGate;

/// <summary>
/// In-process virtualization probe shared by the TableView and ListView fallback windows. It scrolls
/// with ScrollViewer.ChangeView (no UI Automation client involved) and records realized containers,
/// memory, and frame gaps from CompositionTarget.Rendering.
/// </summary>
internal static class ScrollProbe
{
    private const int StepsPerSweep = 120;
    private static readonly TimeSpan s_stepDelay = TimeSpan.FromMilliseconds(16);

    // Diagnostic only: reads ObservableObject's event backing field to count live binding subscriptions.
    private static readonly FieldInfo? s_propertyChangedField =
        typeof(ObservableObject).GetField(nameof(ObservableObject.PropertyChanged), BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Total PropertyChanged subscribers across all rows; it should track realized rows, not grow with scrolling.</summary>
    public static int CountSubscribers(IEnumerable<TaskRowViewModel> rows) =>
        rows.Sum(row => (s_propertyChangedField?.GetValue(row) as Delegate)?.GetInvocationList().Length ?? 0);

    public static string Measure(ListViewBase list, IReadOnlyCollection<TaskRowViewModel> rows)
    {
        var rowCount = rows.Count;
        var subscribers = CountSubscribers(rows);
        using var process = Process.GetCurrentProcess();
        var realized = list.ItemsPanelRoot is Panel panel ? panel.Children.Count : -1;
        var offset = FindScrollViewer(list)?.VerticalOffset ?? -1;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Rows={rowCount}; realized row containers={realized}; PropertyChanged subscribers={subscribers}; workingSet={process.WorkingSet64 / (1024 * 1024)} MB; private={process.PrivateMemorySize64 / (1024 * 1024)} MB; managed={GC.GetTotalMemory(false) / (1024 * 1024)} MB; verticalOffset={offset:F0}");
    }

    public static async Task RunAsync(ListViewBase list, IReadOnlyCollection<TaskRowViewModel> rows, int cycles, Action<string> log)
    {
        var viewer = FindScrollViewer(list) ?? throw new InvalidOperationException("No ScrollViewer in the list template.");
        log("Stress start: " + Measure(list, rows));
        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            var frames = new List<TimeSpan>(4 * StepsPerSweep);
            void OnRendering(object? sender, object e)
            {
                if (e is RenderingEventArgs args)
                {
                    frames.Add(args.RenderingTime);
                }
            }

            CompositionTarget.Rendering += OnRendering;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await SweepAsync(viewer, down: true);
                await SweepAsync(viewer, down: false);
            }
            finally
            {
                CompositionTarget.Rendering -= OnRendering;
            }

            var gaps = frames.Zip(frames.Skip(1), (a, b) => (b - a).TotalMilliseconds).ToList();
            var slow = gaps.Count(g => g > 50);
            log(string.Create(
                CultureInfo.InvariantCulture,
                $"Stress cycle {cycle}: {stopwatch.ElapsedMilliseconds} ms, frames={frames.Count}, avg gap={(gaps.Count > 0 ? gaps.Average() : 0):F1} ms, max gap={(gaps.Count > 0 ? gaps.Max() : 0):F0} ms, gaps>50ms={slow}; {Measure(list, rows)}"));
        }

        // A full collection separates retained objects from garbage that simply has not been collected yet.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        log("Stress end after forced GC: " + Measure(list, rows));
    }

    private static async Task SweepAsync(ScrollViewer viewer, bool down)
    {
        for (var step = 1; step <= StepsPerSweep; step++)
        {
            var fraction = (double)step / StepsPerSweep;
            var target = viewer.ScrollableHeight * (down ? fraction : 1 - fraction);
            viewer.ChangeView(null, target, null, disableAnimation: true);
            await Task.Delay(s_stepDelay);
        }
    }

    /// <summary>Returns the descendant ScrollViewer with the largest vertical extent (TableView nests a horizontal one).</summary>
    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        ScrollViewer? best = null;
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is ScrollViewer viewer && (best is null || viewer.ScrollableHeight > best.ScrollableHeight))
            {
                best = viewer;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, i));
            }
        }

        return best;
    }
}
