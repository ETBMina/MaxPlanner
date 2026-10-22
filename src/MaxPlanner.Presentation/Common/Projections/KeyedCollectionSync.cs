using System.Collections.ObjectModel;

namespace MaxPlanner.Presentation.Common.Projections;

/// <summary>
/// Brings a small bound collection (the checklist items of one card) in line with a new ordered list of
/// models while keeping each item ViewModel instance, so a focused checkbox keeps its container (T-07).
/// </summary>
public static class KeyedCollectionSync
{
    /// <summary>
    /// After the call <paramref name="target"/> holds one ViewModel per model, in model order. Existing
    /// ViewModels are updated in place and moved; new ones are created; missing ones are removed.
    /// </summary>
    public static void Sync<TModel, TKey, TViewModel>(
        ObservableCollection<TViewModel> target,
        IReadOnlyList<TModel> models,
        Func<TModel, TKey> modelKey,
        Func<TViewModel, TKey> viewModelKey,
        Func<TModel, TViewModel> create,
        Action<TViewModel, TModel> update)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(modelKey);
        ArgumentNullException.ThrowIfNull(viewModelKey);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(update);

        var wanted = new HashSet<TKey>(models.Select(modelKey));
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(viewModelKey(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            var key = modelKey(model);
            var existing = -1;
            for (var j = index; j < target.Count; j++)
            {
                if (EqualityComparer<TKey>.Default.Equals(viewModelKey(target[j]), key))
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                target.Insert(index, create(model));
                continue;
            }

            update(target[existing], model);
            if (existing != index)
            {
                target.Move(existing, index);
            }
        }
    }
}
