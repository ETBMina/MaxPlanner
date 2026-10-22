using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;

namespace MaxPlanner.Presentation.Common.Projections;

/// <summary>
/// One page list over a store cache, in the pipeline shape spike 5 verified (ADR 0010): filter on the
/// store scheduler, switch to the UI thread, update item ViewModels in place, then sort and bind.
/// Owns its subscriptions; disposing it stops every further UI update.
/// </summary>
/// <remarks>
/// Create, use, and dispose it on the UI thread. <see cref="Items"/> changes only on the UI thread.
/// </remarks>
public sealed class ProjectionView<TModel, TKey, TViewModel> : IDisposable
    where TModel : notnull
    where TKey : notnull
    where TViewModel : class, IProjectionItem<TModel, TKey>
{
    private readonly BehaviorSubject<Func<TModel, bool>> _filter;
    private readonly BehaviorSubject<IComparer<TViewModel>> _comparer;
    private readonly IDisposable _subscription;
    private readonly IDisposable _initialization;
    private bool _isDisposed;

    /// <param name="storeScheduler">The store's scheduler (<c>WorkspaceProjectionStore.Scheduler</c>).</param>
    /// <param name="uiScheduler">A scheduler over the window's UI thread.</param>
    /// <param name="create">Creates the ViewModel for a record entering the list; later updates call <see cref="IProjectionItem{TModel, TKey}.Update"/>.</param>
    /// <param name="filter">Null shows every record.</param>
    public ProjectionView(
        IObservableCache<TModel, TKey> source,
        IScheduler storeScheduler,
        IScheduler uiScheduler,
        Func<TModel, TViewModel> create,
        IComparer<TViewModel> comparer,
        Func<TModel, bool>? filter = null)
        : this((source ?? throw new ArgumentNullException(nameof(source))).Connect(), storeScheduler, uiScheduler, create, comparer, filter)
    {
    }

    /// <param name="changes">
    /// A cold change stream over one or more store caches (for example tasks and Subtask cards merged with
    /// <c>Or</c>). It is subscribed on <paramref name="storeScheduler"/> and must emit only there.
    /// </param>
    public ProjectionView(
        IObservable<IChangeSet<TModel, TKey>> changes,
        IScheduler storeScheduler,
        IScheduler uiScheduler,
        Func<TModel, TViewModel> create,
        IComparer<TViewModel> comparer,
        Func<TModel, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(storeScheduler);
        ArgumentNullException.ThrowIfNull(uiScheduler);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(comparer);

        _filter = new BehaviorSubject<Func<TModel, bool>>(filter ?? (_ => true));
        _comparer = new BehaviorSubject<IComparer<TViewModel>>(comparer);
        Selection = new ProjectionSelection<TKey, TViewModel>(item => item.Key);

        // SubscribeOn makes the initial contents and every filter evaluation run on the store scheduler,
        // never on the UI thread. ObserveOn precedes the inline update because the update mutates
        // ViewModels the UI is bound to. Default SortAndBind options keep Move notifications (focus
        // follows a re-sorted row) and avoid binary search, because sort keys change in place.
        _subscription = changes
            .SubscribeOn(storeScheduler)
            .Filter(_filter.ObserveOn(storeScheduler))
            .ObserveOn(uiScheduler)
            .TransformWithInlineUpdate(create, (viewModel, model) => viewModel.Update(model))
            .SortAndBind(out var items, _comparer.ObserveOn(uiScheduler))
            .Subscribe(_ => RestoreSelection());
        Items = items;

        // Both schedulers run work in submission order. The subscription above is queued on the store
        // scheduler first and queues the filter's first predicate there while it runs, so a marker that hops
        // twice on the store scheduler and then to the UI arrives after the initial contents were bound.
        // Pages use it to tell "still loading" from "empty" (design §2.5).
        _initialization = storeScheduler.Schedule(() => storeScheduler.Schedule(() => uiScheduler.Schedule(MarkInitialized)));
    }

    /// <summary>Raised once on the UI thread after the records present at creation have been bound.</summary>
    public event EventHandler? Initialized;

    public ReadOnlyObservableCollection<TViewModel> Items { get; }

    public ProjectionSelection<TKey, TViewModel> Selection { get; }

    public bool IsInitialized { get; private set; }

    /// <summary>Re-evaluates membership of every record on the store scheduler.</summary>
    public void SetFilter(Func<TModel, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!_isDisposed)
        {
            _filter.OnNext(filter);
        }
    }

    public void SetComparer(IComparer<TViewModel> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        if (!_isDisposed)
        {
            _comparer.OnNext(comparer);
        }
    }

    /// <summary>
    /// Re-sorts every item with the current comparer. Used when an item whose position was held while it
    /// was being edited is released (design §7.7), because its sort keys changed without a store update.
    /// </summary>
    public void Resort()
    {
        if (!_isDisposed)
        {
            _comparer.OnNext(_comparer.Value);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _initialization.Dispose();
        _subscription.Dispose();
        _filter.OnCompleted();
        _comparer.OnCompleted();
        _filter.Dispose();
        _comparer.Dispose();
    }

    private void MarkInitialized()
    {
        if (_isDisposed || IsInitialized)
        {
            return;
        }

        IsInitialized = true;
        Initialized?.Invoke(this, EventArgs.Empty);
    }

    // Nothing can be selected while the constructor runs, so Items is assigned whenever this does work.
    private void RestoreSelection()
    {
        if (Selection.HasSelection)
        {
            Selection.Restore(Items);
        }
    }
}
