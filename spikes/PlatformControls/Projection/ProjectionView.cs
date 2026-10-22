using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;

namespace PlatformControls.Projection;

/// <summary>
/// One view pipeline in the ADR 0010 shape: Filter → ObserveOn(UI) → TransformWithInlineUpdate → SortAndBind.
/// </summary>
internal sealed class ProjectionView : IDisposable
{
    private readonly CompositeDisposable _subscriptions = [];
    private readonly BehaviorSubject<Func<ItemRecord, bool>> _filter = new(_ => true);
    private readonly BehaviorSubject<IComparer<ItemViewModel>> _comparer = new(RankComparer.Instance);
    private readonly ProjectionStore _store;
    private readonly List<double> _latencies = [];
    private readonly Stopwatch _buildStopwatch = Stopwatch.StartNew();

    public ProjectionView(ProjectionStore store, IScheduler uiScheduler, bool updatesAsRefresh, bool movesAsRemoveInsert)
    {
        _store = store;
        UpdatesAsRefresh = updatesAsRefresh;
        MovesAsRemoveInsert = movesAsRemoveInsert;

        // The inline update mutates bound view models, so everything after ObserveOn runs on the UI thread.
        // Filter stays on the store thread.
        var transformed = store.Connect()
            .Filter(_filter)
            .ObserveOn(uiScheduler)
            .TransformWithInlineUpdate(record => new ItemViewModel(record), (viewModel, record) =>
            {
                if (IsDisposed)
                {
                    SpikeHost.ChangesAfterDispose++;
                }

                viewModel.Update(record);
                RecordLatency(record.Id);
            });

        if (updatesAsRefresh)
        {
            transformed = transformed.Select(ConvertUpdatesToRefresh);
        }

        if (movesAsRemoveInsert)
        {
            var target = new RemoveInsertObservableCollection<ItemViewModel>();
            _subscriptions.Add(transformed.SortAndBind(target, _comparer).Subscribe(OnChangeSetBound));
            Items = new ReadOnlyObservableCollection<ItemViewModel>(target);
        }
        else
        {
            _subscriptions.Add(transformed.SortAndBind(out var items, _comparer).Subscribe(OnChangeSetBound));
            Items = items;
        }
    }

    public event EventHandler? InitialBindCompleted;

    /// <summary>Raised on the UI thread after each change set has been applied to <see cref="Items"/>.</summary>
    public event EventHandler? ChangeSetApplied;

    public ReadOnlyObservableCollection<ItemViewModel> Items { get; }

    public bool UpdatesAsRefresh { get; }

    public bool MovesAsRemoveInsert { get; }

    public bool IsDisposed { get; private set; }

    public double InitialBindMilliseconds { get; private set; } = double.NaN;

    public int ChangeSetsBound { get; private set; }

    public bool FilterHasObservers => _filter.HasObservers;

    public bool ComparerHasObservers => _comparer.HasObservers;

    public string DescribeLatency()
    {
        if (_latencies.Count == 0)
        {
            return "no updates yet";
        }

        var sorted = _latencies.Order().ToArray();
        var p95 = sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(sorted.Length * 0.95) - 1)];
        return $"n={sorted.Length} last={_latencies[^1]:F2} ms avg={sorted.Average():F2} ms p95={p95:F2} ms max={sorted[^1]:F2} ms";
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _subscriptions.Dispose();
        _filter.OnCompleted();
        _comparer.OnCompleted();
    }

    /// <summary>
    /// Variant B: TransformWithInlineUpdate reports an in-place update as ChangeReason.Update with the same
    /// instance; this turns it into Refresh so SortAndBind only moves the item when its sort position changes.
    /// </summary>
    private static IChangeSet<ItemViewModel, Guid> ConvertUpdatesToRefresh(IChangeSet<ItemViewModel, Guid> changes) =>
        new ChangeSet<ItemViewModel, Guid>(changes.Select(change => change.Reason == ChangeReason.Update
            ? new Change<ItemViewModel, Guid>(ChangeReason.Refresh, change.Key, change.Current)
            : change));

    private void RecordLatency(Guid id)
    {
        if (_store.PendingSince.TryRemove(id, out var since))
        {
            _latencies.Add(Stopwatch.GetElapsedTime(since).TotalMilliseconds);
        }
    }

    private void OnChangeSetBound(IChangeSet<ItemViewModel, Guid> changes)
    {
        ChangeSetsBound++;
        ChangeSetApplied?.Invoke(this, EventArgs.Empty);
        if (double.IsNaN(InitialBindMilliseconds))
        {
            InitialBindMilliseconds = _buildStopwatch.Elapsed.TotalMilliseconds;
            InitialBindCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
}
