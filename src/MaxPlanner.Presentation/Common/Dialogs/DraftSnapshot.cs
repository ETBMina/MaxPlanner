using MaxPlanner.Domain.Values;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// The editable values of an editor draft by stable field key (for example <c>Subtitle</c>), compared one by
/// one to tell whether the draft is dirty and which fields a conflict review lists (design §5.10).
/// </summary>
/// <remarks>Values must have value equality (strings, IDs, dates, records).</remarks>
internal sealed class DraftSnapshot
{
    private readonly Dictionary<string, object?> _values;

    public DraftSnapshot(IReadOnlyList<(string Key, object? Value)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Keys = [.. values.Select(v => v.Key)];
        _values = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
    }

    /// <summary>The field keys in form order.</summary>
    public IReadOnlyList<string> Keys { get; }

    public object? this[string key] => _values[key];

    public bool SameAs(DraftSnapshot other, string key) => Equals(_values[key], other._values[key]);

    public bool SameAs(DraftSnapshot other) => Keys.All(k => SameAs(other, k));
}

/// <summary>Date conversions shared by the editors' <c>CalendarDatePicker</c>s (B-04, T-05).</summary>
internal static class DraftDates
{
    public static DateTimeOffset? ToPicker(DateOnly? date) => date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)) : null;

    // The picker's local date component is the calendar day; converting through UTC could shift it (T-05).
    public static DateOnly? FromPicker(DateTimeOffset? value) => value is { } v ? DateOnly.FromDateTime(v.DateTime) : null;

    /// <summary>
    /// B-04: an unchanged date sends no intent, so automation may fill it on a transition; an edited date is
    /// set; a cleared date is an explicit clear that automation must respect in this save.
    /// </summary>
    public static DateIntent Intent(DateOnly? draft, DateOnly? based, bool cleared) =>
        draft != based ? (draft is { } d ? DateIntent.Set(d) : DateIntent.Cleared)
        : draft is null && cleared ? DateIntent.Cleared
        : DateIntent.Unchanged;
}
