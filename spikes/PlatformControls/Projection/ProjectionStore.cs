using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;

namespace PlatformControls.Projection;

/// <summary>
/// Stand-in for the product WorkspaceProjectionStore (ADR 0010): one SourceCache of immutable records,
/// mutated only on the store's own thread.
/// </summary>
internal sealed class ProjectionStore : IDisposable
{
    private static readonly string[] _statuses = ["To do", "In progress", "Review", "Done"];
    private readonly SourceCache<ItemRecord, Guid> _cache = new(record => record.Id);
    private readonly EventLoopScheduler _storeScheduler = new(start => new Thread(start) { IsBackground = true, Name = "ProjectionStore" });
    private int _activeSubscriptions;
    private int _maxRank;

    public int Count => _cache.Count;

    public double LoadMilliseconds { get; private set; }

    /// <summary>Number of live <see cref="Connect"/> subscriptions (spike 5.6 disposal evidence).</summary>
    public int ActiveSubscriptions => Volatile.Read(ref _activeSubscriptions);

    /// <summary>Stopwatch timestamp taken just before AddOrUpdate, per record, for latency measurement.</summary>
    public ConcurrentDictionary<Guid, long> PendingSince { get; } = new();

    public void Load(int count)
    {
        var records = Enumerable.Range(1, count)
            .Select(n => new ItemRecord(ItemRecord.KeyFor(n), n, $"Item {n:D5}", _statuses[n % _statuses.Length], n * 10))
            .ToList();
        var stopwatch = Stopwatch.StartNew();
        _cache.AddOrUpdate(records);
        LoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        _maxRank = count * 10;
    }

    public IObservable<IChangeSet<ItemRecord, Guid>> Connect() =>
        Observable.Create<IChangeSet<ItemRecord, Guid>>(observer =>
        {
            Interlocked.Increment(ref _activeSubscriptions);
            var inner = _cache.Connect().Subscribe(observer);
            return Disposable.Create(() =>
            {
                inner.Dispose();
                Interlocked.Decrement(ref _activeSubscriptions);
            });
        });

    public ItemRecord? Find(Guid id)
    {
        var found = _cache.Lookup(id);
        return found.HasValue ? found.Value : null;
    }

    public int NextEndRank() => Interlocked.Add(ref _maxRank, 10);

    public void Apply(IReadOnlyList<ItemRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        _storeScheduler.Schedule(() =>
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var record in records)
            {
                PendingSince[record.Id] = now;
            }

            _cache.AddOrUpdate(records);
        });
    }

    public void Dispose()
    {
        _storeScheduler.Dispose();
        _cache.Dispose();
    }
}
