using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Application.Filtering;

/// <summary>
/// An inclusive date range filter (B-10). A record without the date fails the filter unless
/// <see cref="IncludeUndated"/> is set. Only a range with at least one bound is a filter at all.
/// </summary>
public sealed record DateRangeFilter(DateOnly? From, DateOnly? To, bool IncludeUndated)
{
    public bool IsSet => From is not null || To is not null;

    /// <summary>B-10: an inverted range is rejected before it is applied.</summary>
    public bool IsInverted => From is { } from && To is { } to && from > to;

    /// <summary>A single date against the inclusive bounds.</summary>
    public bool Matches(DateOnly? value) =>
        !IsSet || (value is { } day ? (From is null || day >= From) && (To is null || day <= To) : IncludeUndated);

    /// <summary>
    /// B-10 Calendar planned-range rule: the record's range overlaps the filter (start ≤ To and end ≥ From),
    /// ignoring absent bounds. It is an overlap, never an exact-date match.
    /// </summary>
    public bool MatchesOverlap(DateOnly? start, DateOnly? end)
    {
        if (!IsSet)
        {
            return true;
        }

        if (start is null && end is null)
        {
            return IncludeUndated;
        }

        var first = start ?? end!.Value;
        var last = end ?? start!.Value;
        return (To is null || first <= To) && (From is null || last >= From);
    }
}

/// <summary>The Period choices of the Dashboard and the Filters flyout. <see cref="AllTime"/> is no filter.</summary>
public enum PeriodPreset
{
    AllTime,

    /// <summary>Today and the 7 days before it: [today − 7, today], 8 calendar days.</summary>
    Last7Days,

    /// <summary>Today and the 30 days before it: [today − 30, today].</summary>
    Last30Days,

    /// <summary>Fixed From/To dates, both inclusive; either may be open.</summary>
    Custom,
}

/// <summary>
/// The Period filter: a record is in the period when its start date (a task's or Subtask card's own Start Date, a
/// CR's planned start) falls inside it, inclusive. The end date is never considered, and a record without a start
/// date is outside every set period. Rolling presets are stored as presets and resolved against the local day each
/// time they are matched, so they move at midnight.
/// </summary>
public sealed record PeriodFilter(PeriodPreset Preset, DateOnly? From = null, DateOnly? To = null)
{
    public static PeriodFilter AllTime { get; } = new(PeriodPreset.AllTime);

    public static PeriodFilter Last7Days { get; } = new(PeriodPreset.Last7Days);

    public static PeriodFilter Last30Days { get; } = new(PeriodPreset.Last30Days);

    public static PeriodFilter Custom(DateOnly? from, DateOnly? to) => new(PeriodPreset.Custom, from, to);

    public bool IsSet => IsRolling || (Preset == PeriodPreset.Custom && (From is not null || To is not null));

    /// <summary>The period moves with the local day.</summary>
    public bool IsRolling => Preset is PeriodPreset.Last7Days or PeriodPreset.Last30Days;

    public bool IsInverted => Preset == PeriodPreset.Custom && From is { } from && To is { } to && from > to;

    /// <summary>The inclusive bounds on <paramref name="today"/>; both null when the period is not set.</summary>
    public (DateOnly? From, DateOnly? To) Resolve(DateOnly today) => Preset switch
    {
        PeriodPreset.Last7Days => (today.AddDays(-7), today),
        PeriodPreset.Last30Days => (today.AddDays(-30), today),
        PeriodPreset.Custom => (From, To),
        _ => (null, null),
    };

    /// <summary>A start date against the period on <paramref name="today"/>; an undated record is outside a set period.</summary>
    public bool Matches(DateOnly? start, DateOnly today)
    {
        if (!IsSet)
        {
            return true;
        }

        if (start is not { } day)
        {
            return false;
        }

        var (from, to) = Resolve(today);
        return (from is null || day >= from) && (to is null || day <= to);
    }
}

/// <summary>An inclusive number range for a Number custom field; a record without a value fails it.</summary>
public sealed record NumberRangeFilter(decimal? Min, decimal? Max)
{
    public bool IsSet => Min is not null || Max is not null;

    public bool IsInverted => Min is { } min && Max is { } max && min > max;

    public bool Matches(decimal? value) =>
        !IsSet || (value is { } number && (Min is null || number >= Min) && (Max is null || number <= Max));
}

/// <summary>B-10: Tags match "any selected" by default, with an explicit "all selected" mode.</summary>
public enum TagMatchMode
{
    Any,
    All,
}

public enum CrLinkMode
{
    /// <summary>No CR filter.</summary>
    Any,

    /// <summary>Unlinked tasks only (and the Subtask cards of unlinked parents).</summary>
    NoCr,

    /// <summary>Records linked to <see cref="CrLinkFilter.ChangeRequestId"/>.</summary>
    Specific,
}

/// <summary>The task CR filter of design §2.6: "Any CR", "No CR", or one CR.</summary>
public sealed record CrLinkFilter(CrLinkMode Mode, ChangeRequestId? ChangeRequestId)
{
    public static CrLinkFilter Any { get; } = new(CrLinkMode.Any, null);

    public static CrLinkFilter NoCr { get; } = new(CrLinkMode.NoCr, null);

    public static CrLinkFilter For(ChangeRequestId id) => new(CrLinkMode.Specific, id);

    public bool IsSet => Mode != CrLinkMode.Any;

    public bool Matches(ChangeRequestId? linked) => Mode switch
    {
        CrLinkMode.NoCr => linked is null,
        CrLinkMode.Specific => linked == ChangeRequestId,
        _ => true,
    };
}

/// <summary>
/// A filter on one custom field with the operator its kind supports (B-10): text contains, number range, date
/// range, or lookup membership. People, Tags and legacy fields are not filterable.
/// </summary>
public sealed record CustomFieldFilter(
    FieldId FieldId,
    string? TextContains,
    NumberRangeFilter? Number,
    DateRangeFilter? Date,
    IReadOnlyList<FieldOptionId> Options)
{
    public bool IsSet =>
        !string.IsNullOrWhiteSpace(TextContains) || Number is { IsSet: true } || Date is { IsSet: true } || Options.Count > 0;

    public bool Matches(IReadOnlyDictionary<FieldId, CustomValue>? values)
    {
        var value = values is not null && values.TryGetValue(FieldId, out var found) ? found : null;
        if (!string.IsNullOrWhiteSpace(TextContains))
        {
            var text = value switch
            {
                TextCustomValue t => t.Value,
                MultilineTextCustomValue m => m.Value,
                UrlCustomValue u => u.Url.Value.ToString(),
                _ => null,
            };
            if (text is null || !text.Contains(TextContains.Trim(), StringComparison.CurrentCultureIgnoreCase))
            {
                return false;
            }
        }

        if (Number is { IsSet: true } number && !number.Matches((value as NumberCustomValue)?.Value))
        {
            return false;
        }

        if (Date is { IsSet: true } date && !date.Matches((value as DateCustomValue)?.Value))
        {
            return false;
        }

        return Options.Count == 0 || (value is LookupCustomValue lookup && Options.Contains(lookup.OptionId));
    }

    /// <summary>The kinds that have a filter operator (B-10).</summary>
    public static bool IsFilterable(FieldKind kind) =>
        kind is FieldKind.Text or FieldKind.MultilineText or FieldKind.Url or FieldKind.Number or FieldKind.Date or FieldKind.Lookup;
}
