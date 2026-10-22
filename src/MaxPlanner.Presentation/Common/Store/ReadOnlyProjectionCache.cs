using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;

namespace MaxPlanner.Presentation.Common.Store;

/// <summary>Counts live subscriptions across the store's caches (T-11 navigation and disposal evidence).</summary>
internal sealed class SubscriptionCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public IObservable<T> Track<T>(IObservable<T> source) =>
        Observable.Create<T>(observer =>
        {
            Interlocked.Increment(ref _count);
            var inner = source.Subscribe(observer);
            return Disposable.Create(() =>
            {
                inner.Dispose();
                Interlocked.Decrement(ref _count);
            });
        });
}

/// <summary>
/// A read-only view of one store cache: consumers can query and connect, but only the store can edit,
/// and disposing this view does nothing because the store owns the cache.
/// </summary>
internal sealed class ReadOnlyProjectionCache<TObject, TKey> : IObservableCache<TObject, TKey>
    where TObject : notnull
    where TKey : notnull
{
    private readonly IObservableCache<TObject, TKey> _inner;
    private readonly SubscriptionCounter _counter;

    public ReadOnlyProjectionCache(IObservableCache<TObject, TKey> inner, SubscriptionCounter counter)
    {
        _inner = inner;
        _counter = counter;
    }

    public int Count => _inner.Count;

    public IObservable<int> CountChanged => _counter.Track(_inner.CountChanged);

    public IReadOnlyList<TObject> Items => _inner.Items;

    public IReadOnlyList<TKey> Keys => _inner.Keys;

    public IReadOnlyDictionary<TKey, TObject> KeyValues => _inner.KeyValues;

    public IObservable<IChangeSet<TObject, TKey>> Connect(Func<TObject, bool>? predicate = null, bool suppressEmptyChangeSets = true) =>
        _counter.Track(_inner.Connect(predicate, suppressEmptyChangeSets));

    public IObservable<IChangeSet<TObject, TKey>> Preview(Func<TObject, bool>? predicate = null) => _counter.Track(_inner.Preview(predicate));

    public IObservable<Change<TObject, TKey>> Watch(TKey key) => _counter.Track(_inner.Watch(key));

    public Optional<TObject> Lookup(TKey key) => _inner.Lookup(key);

    public void Dispose()
    {
    }
}
