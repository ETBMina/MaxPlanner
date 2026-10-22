using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PlatformControls.Projection;

/// <summary>
/// Long-lived row view model. The pipeline updates it in place (TransformWithInlineUpdate), so its
/// identity, and therefore the ListView container and the TextBox draft, survive store updates.
/// </summary>
public sealed partial class ItemViewModel : ObservableObject
{
    private static int _nextInstanceId;
    private bool _isDraftDirty;
    private bool _isSyncingDraft;

    public ItemViewModel(ItemRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Id = record.Id;
        Number = record.Number;
        Key = record.Id.ToString("N");
        InstanceId = Interlocked.Increment(ref _nextInstanceId);
        Title = record.Title;
        Status = record.Status;
        Rank = record.Rank;
        SyncDraft(record.Title);
    }

    public Guid Id { get; }

    public int Number { get; }

    public string Key { get; }

    public int InstanceId { get; }

    public string InstanceText => string.Create(CultureInfo.InvariantCulture, $"vm#{InstanceId}");

    public string DraftAutomationId => $"Projection_DraftBox_{Key}";

    public string TitleAutomationId => $"Projection_TitleText_{Key}";

    public string DraftName => string.Create(CultureInfo.InvariantCulture, $"Draft title for item {Number:D5}");

    /// <summary>Number of times a ListView container was prepared for this view model (spike diagnostics).</summary>
    public int ContainerPrepareCount { get; set; }

    public int LastContainerId { get; set; }

    public int InlineUpdateCount { get; private set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RankText))]
    public partial int Rank { get; set; }

    public string RankText => Rank.ToString(CultureInfo.InvariantCulture);

    /// <summary>User-owned draft. Store updates never overwrite it once the user has edited it.</summary>
    [ObservableProperty]
    public partial string Draft { get; set; }

    public void Update(ItemRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        InlineUpdateCount++;
        Title = record.Title;
        Status = record.Status;
        Rank = record.Rank;
        if (!_isDraftDirty)
        {
            SyncDraft(record.Title);
        }
    }

    partial void OnDraftChanged(string value)
    {
        if (!_isSyncingDraft)
        {
            _isDraftDirty = !string.Equals(value, Title, StringComparison.Ordinal);
        }
    }

    private void SyncDraft(string committedTitle)
    {
        _isSyncingDraft = true;
        Draft = committedTitle;
        _isSyncingDraft = false;
    }
}
