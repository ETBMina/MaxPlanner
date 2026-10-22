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
    {
        ArgumentNullException.ThrowIfNull(source);
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
        _subscription = source.Connect()
            .SubscribeOn(storeScheduler)
            .Filter(_filter.ObserveOn(storeScheduler))
            .ObserveOn(uiScheduler)
            .TransformWithInlineUpdate(create, (viewModel, model) => viewModel.Update(model))
            .SortAndBind(out var items, _comparer.ObserveOn(uiScheduler))
            .Subscribe(_ => RestoreSelection());
        Items = items;
    }

    public ReadOnlyObservableCollection<TViewModel> Items { get; }

    public ProjectionSelection<TKey, TViewModel> Selection { get; }

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

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _subscription.Dispose();
        _filter.OnCompleted();
        _comparer.OnCompleted();
        _filter.Dispose();
        _comparer.Dispose();
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
