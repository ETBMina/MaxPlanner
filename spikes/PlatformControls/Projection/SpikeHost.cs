using System.Globalization;
using Microsoft.UI.Dispatching;

namespace PlatformControls.Projection;

/// <summary>App-lifetime spike services: the store, the UI scheduler and the mutator outlive any page.</summary>
internal static class SpikeHost
{
    public static ProjectionStore Store { get; private set; } = null!;

    public static DispatcherQueueScheduler UiScheduler { get; private set; } = null!;

    public static Mutator Mutator { get; private set; } = null!;

    /// <summary>Collection notifications raised by a view after it was disposed (spike 5.6; must stay 0).</summary>
    public static int ChangesAfterDispose { get; set; }

    public static void Initialize(DispatcherQueue dispatcherQueue, int recordCount)
    {
        UiScheduler = new DispatcherQueueScheduler(dispatcherQueue);
        Store = new ProjectionStore();
        Store.Load(recordCount);
        Mutator = new Mutator(Store, dispatcherQueue);
        SpikeLog.Write(string.Create(CultureInfo.InvariantCulture, $"Store loaded {Store.Count} records in {Store.LoadMilliseconds:F1} ms"));
    }

    public static string DescribeStoreState() => string.Create(
        CultureInfo.InvariantCulture,
        $"Store: {Store.Count} records, cache load {Store.LoadMilliseconds:F1} ms | active Connect() subscriptions: {Store.ActiveSubscriptions} | mutator: {Mutator.Describe()} | changes after dispose: {ChangesAfterDispose} | dropped UI work: {UiScheduler.DroppedWorkItems}");
}
