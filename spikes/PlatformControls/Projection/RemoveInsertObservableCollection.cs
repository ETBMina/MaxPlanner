using DynamicData.Binding;

namespace PlatformControls.Projection;

/// <summary>
/// Spike 5 variant C: reports a sort move as Remove + Add instead of a single Move notification, to compare how
/// the ListView treats selection and the focused container under each notification shape.
/// </summary>
internal sealed class RemoveInsertObservableCollection<T> : ObservableCollectionExtended<T>
{
    protected override void MoveItem(int oldIndex, int newIndex)
    {
        var item = this[oldIndex];
        RemoveItem(oldIndex);
        InsertItem(newIndex, item);
    }
}
