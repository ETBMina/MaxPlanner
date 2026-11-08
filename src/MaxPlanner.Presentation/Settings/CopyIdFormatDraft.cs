using MaxPlanner.Application.Settings;

namespace MaxPlanner.Presentation.Settings;

/// <summary>
/// The Copy ID format box's save state (design §4.6). Committed updates replace the box text only while it has no
/// unsaved edit (ADR 0010), and saves run one at a time: a value committed while a save is in flight waits until the
/// committed defaults (and their stamp) have been refreshed. Use on the UI thread.
/// </summary>
public sealed class CopyIdFormatDraft
{
    private string? _synced;
    private string? _saving;
    private string? _pending;
    private bool _awaitingRefresh;

    public bool IsSaving => _saving is not null || _awaitingRefresh;

    /// <summary>A committed format arrived. Returns the text the box should show.</summary>
    public string Sync(string text, string committed)
    {
        var keep = _synced is not null && text != _synced;
        _synced = committed;
        _awaitingRefresh = false;
        return keep ? text : committed;
    }

    /// <summary>The user committed <paramref name="text"/>. Returns the format to save now, or null when there is nothing to save yet.</summary>
    public string? Commit(string text, string committed)
    {
        var format = CopyIdFormatter.Normalize(text);
        if (IsSaving)
        {
            _pending = format;
            return null;
        }

        _pending = null;
        if (format == committed)
        {
            return null;
        }

        _saving = format;
        return format;
    }

    /// <summary>The save ended. A successful save waits for its committed update before the next save is sent.</summary>
    public void SaveEnded(bool succeeded, bool refreshed)
    {
        _saving = null;
        _awaitingRefresh = succeeded && !refreshed;
    }

    /// <summary>After a save ends and the defaults are current: the waiting value to save, or null.</summary>
    public string? TakePending(string committed)
    {
        if (IsSaving || _pending is not { } pending)
        {
            return null;
        }

        _pending = null;
        if (pending == committed)
        {
            return null;
        }

        _saving = pending;
        return pending;
    }
}
