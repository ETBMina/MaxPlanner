using CommunityToolkit.Mvvm.ComponentModel;

namespace MaxPlanner.Presentation.Common.Projections;

/// <summary>An item ViewModel that a <see cref="ProjectionView{TModel, TKey, TViewModel}"/> keeps and updates in place.</summary>
public interface IProjectionItem<in TModel, out TKey>
{
    TKey Key { get; }

    /// <summary>
    /// Replaces the displayed read model on the same instance, so the bound container, focus, and any
    /// draft survive the update (T-07, spike 5). Called on the UI thread.
    /// </summary>
    void Update(TModel model);
}

/// <summary>A minimal base for item ViewModels over an immutable read model.</summary>
public abstract class ProjectionItemViewModel<TModel, TKey> : ObservableObject, IProjectionItem<TModel, TKey>
    where TModel : class
{
    protected ProjectionItemViewModel(TModel model) => Model = model ?? throw new ArgumentNullException(nameof(model));

    public TModel Model { get; private set; }

    public abstract TKey Key { get; }

    public void Update(TModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (EqualityComparer<TModel>.Default.Equals(Model, model))
        {
            return;
        }

        var previous = Model;
        Model = model;
        OnPropertyChanged(nameof(Model));
        OnModelUpdated(previous);
    }

    /// <summary>Raise change notifications for derived properties whose values differ from <paramref name="previous"/>.</summary>
    protected virtual void OnModelUpdated(TModel previous)
    {
    }
}
