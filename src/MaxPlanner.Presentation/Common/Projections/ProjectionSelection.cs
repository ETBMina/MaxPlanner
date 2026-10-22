using CommunityToolkit.Mvvm.ComponentModel;

namespace MaxPlanner.Presentation.Common.Projections;

/// <summary>
/// Keeps a list's selection in ViewModel state across incremental updates (ADR 0010, design §7.7).
/// Bind the list's <c>SelectedItem</c> two-way to <see cref="SelectedItem"/>. Used on the UI thread.
/// </summary>
/// <remarks>
/// A ListView clears its selection when another row moves above the selected row. That null is
/// transient: it is ignored here, and <see cref="Restore"/> re-announces the selected item after the
/// change set has been applied. Deselect explicitly with <see cref="Clear"/>.
/// </remarks>
public sealed class ProjectionSelection<TKey, TViewModel> : ObservableObject
    where TKey : notnull
    where TViewModel : class
{
    private readonly Func<TViewModel, TKey> _keyOf;
    private TViewModel? _selectedItem;
    private bool _isDisplayCleared;

    public ProjectionSelection(Func<TViewModel, TKey> keyOf) => _keyOf = keyOf ?? throw new ArgumentNullException(nameof(keyOf));

    public TViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (value is null && _selectedItem is not null)
            {
                _isDisplayCleared = true;
                return;
            }

            _isDisplayCleared = false;
            SetProperty(ref _selectedItem, value);
        }
    }

    public bool HasSelection => _selectedItem is not null;

    /// <summary>The selected record's key; meaningful only while <see cref="HasSelection"/> is true.</summary>
    public TKey? SelectedKey => _selectedItem is null ? default : _keyOf(_selectedItem);

    public void Clear()
    {
        _isDisplayCleared = false;
        SetProperty(ref _selectedItem, null, nameof(SelectedItem));
    }

    /// <summary>
    /// Reselects the item with the selected key after a change set; clears the selection when the item
    /// left the list (deleted or filtered out).
    /// </summary>
    public void Restore(IEnumerable<TViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_selectedItem is null)
        {
            return;
        }

        var key = _keyOf(_selectedItem);
        var current = items.FirstOrDefault(item => EqualityComparer<TKey>.Default.Equals(_keyOf(item), key));
        if (current is null)
        {
            Clear();
            return;
        }

        if (_isDisplayCleared || !ReferenceEquals(current, _selectedItem))
        {
            _isDisplayCleared = false;
            _selectedItem = current;
            OnPropertyChanged(nameof(SelectedItem));
        }
    }
}
