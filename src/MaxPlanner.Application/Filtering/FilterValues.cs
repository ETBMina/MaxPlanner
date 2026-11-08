using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Application.Filtering;

/// <summary>
/// The rolling date presets of the filter menu's date submenus (design §2.6). They are stored as presets and resolved
/// against the local day each time they are matched, so they move at midnight. Weeks run Sunday–Saturday.
/// </summary>
[Flags]
public enum DatePresets
{
    None = 0,

    /// <summary>Before today.</summary>
    Past = 1,

    Today = 2,

    Tomorrow = 4,

    /// <summary>The Sunday–Saturday week that contains today.</summary>
    ThisWeek = 8,

    /// <summary>The Sunday–Saturday week after this week.</summary>
    NextWeek = 16,

    /// <summary>After the end of next week.</summary>
    Future = 32,
}

/// <summary>Resolves a <see cref="DatePresets"/> value to inclusive bounds on a given local day.</summary>
public static class DatePresetRanges
{
    /// <summary>Every single preset, in menu order.</summary>
    public static IReadOnlyList<DatePresets> All { get; } =
        [DatePresets.Past, DatePresets.Today, DatePresets.Tomorrow, DatePresets.ThisWeek, DatePresets.NextWeek, DatePresets.Future];

    /// <summary>The Sunday that starts the week containing <paramref name="day"/> (app convention).</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(int)day.DayOfWeek);

    /// <summary>The inclusive bounds of one preset on <paramref name="today"/>; a null bound is open.</summary>
    public static (DateOnly? From, DateOnly? To) Resolve(DatePresets preset, DateOnly today)
    {
        var week = WeekStart(today);
        return preset switch
        {
            DatePresets.Past => (null, today.AddDays(-1)),
            DatePresets.Today => (today, today),
            DatePresets.Tomorrow => (today.AddDays(1), today.AddDays(1)),
            DatePresets.ThisWeek => (week, week.AddDays(6)),
            DatePresets.NextWeek => (week.AddDays(7), week.AddDays(13)),
            DatePresets.Future => (week.AddDays(14), null),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "A single preset is expected."),
        };
    }
}

/// <summary>
/// A date filter (B-10, design §2.6): rolling <see cref="Presets"/>, an optional inclusive custom range
/// (<see cref="From"/>/<see cref="To"/>, "Customised"), and <see cref="IncludeUndated"/> ("No date"). The selected
/// parts combine as a union. A record without the date matches only through "No date".
/// </summary>
/// <remarks>
/// The positional shape is the one saved before the presets existed: an older From/To range with "Include records
/// without" reads as Customised plus No date, with the same meaning.
/// </remarks>
public sealed record DateRangeFilter(DateOnly? From, DateOnly? To, bool IncludeUndated, DatePresets Presets = DatePresets.None)
{
    public bool HasCustomRange => From is not null || To is not null;

    public bool IsSet => HasCustomRange || IncludeUndated || Presets != DatePresets.None;

    /// <summary>The filter moves with the local day.</summary>
    public bool IsRolling => Presets != DatePresets.None;

    /// <summary>B-10: an inverted range is rejected before it is applied.</summary>
    public bool IsInverted => From is { } from && To is { } to && from > to;

    /// <summary>The inclusive ranges of the selected presets and the custom range on <paramref name="today"/>.</summary>
    public IEnumerable<(DateOnly? From, DateOnly? To)> Ranges(DateOnly today)
    {
        foreach (var preset in DatePresetRanges.All.Where(p => Presets.HasFlag(p)))
        {
            yield return DatePresetRanges.Resolve(preset, today);
        }

        if (HasCustomRange)
        {
            yield return (From, To);
        }
    }

    /// <summary>A single date against the union of the selected parts on <paramref name="today"/>.</summary>
    public bool Matches(DateOnly? value, DateOnly today)
    {
        if (!IsSet)
        {
            return true;
        }

        return value is { } day
            ? Ranges(today).Any(r => (r.From is null || day >= r.From) && (r.To is null || day <= r.To))
            : IncludeUndated;
    }

    /// <summary>
    /// B-10 Calendar planned-range rule: the record's range overlaps one of the selected ranges (start ≤ To and
    /// end ≥ From), ignoring absent bounds. It is an overlap, never an exact-date match.
    /// </summary>
    public bool MatchesOverlap(DateOnly? start, DateOnly? end, DateOnly today)
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
        return Ranges(today).Any(r => (r.To is null || first <= r.To) && (r.From is null || last >= r.From));
    }
}

/// <summary>The Active date choices of the filter menu and the Dashboard. <see cref="AllTime"/> is no filter.</summary>
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
/// The Active date filter (was "Period"): a record matches when its active span overlaps the range, i.e. it was
/// active at some point in it. The span is a task's or Subtask card's Start Date → End Date, a CR's planned start →
/// planned end, a review's created day → completed day. A missing end means still active (open-ended); a record
/// without a start is outside every set range. Rolling presets are stored as presets and resolved against the local
/// day each time they are matched, so they move at midnight.
/// </summary>
public sealed record PeriodFilter(PeriodPreset Preset, DateOnly? From = null, DateOnly? To = null)
{
    public static PeriodFilter AllTime { get; } = new(PeriodPreset.AllTime);

    public static PeriodFilter Last7Days { get; } = new(PeriodPreset.Last7Days);

    public static PeriodFilter Last30Days { get; } = new(PeriodPreset.Last30Days);

    public static PeriodFilter Custom(DateOnly? from, DateOnly? to) => new(PeriodPreset.Custom, from, to);

    public bool IsSet => IsRolling || (Preset == PeriodPreset.Custom && (From is not null || To is not null));

    /// <summary>The range moves with the local day.</summary>
    public bool IsRolling => Preset is PeriodPreset.Last7Days or PeriodPreset.Last30Days;

    public bool IsInverted => Preset == PeriodPreset.Custom && From is { } from && To is { } to && from > to;

    /// <summary>The inclusive bounds on <paramref name="today"/>; both null when the range is not set.</summary>
    public (DateOnly? From, DateOnly? To) Resolve(DateOnly today) => Preset switch
    {
        PeriodPreset.Last7Days => (today.AddDays(-7), today),
        PeriodPreset.Last30Days => (today.AddDays(-30), today),
        PeriodPreset.Custom => (From, To),
        _ => (null, null),
    };

    /// <summary>
    /// The active span <paramref name="start"/> → <paramref name="end"/> overlaps the range on
    /// <paramref name="today"/>. A null end is open-ended (still active); a null start never matches a set range.
    /// </summary>
    public bool Matches(DateOnly? start, DateOnly? end, DateOnly today)
    {
        if (!IsSet)
        {
            return true;
        }

        if (start is not { } first)
        {
            return false;
        }

        var (from, to) = Resolve(today);
        return (to is null || first <= to) && (from is null || end is not { } last || last >= from);
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
/// filter (presets, custom range, No date), or lookup membership. People, Tags and legacy fields are not filterable.
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

    /// <param name="today">The local day the rolling date presets resolve against.</param>
    public bool Matches(IReadOnlyDictionary<FieldId, CustomValue>? values, DateOnly today)
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

        if (Date is { IsSet: true } date && !date.Matches((value as DateCustomValue)?.Value, today))
        {
            return false;
        }

        return Options.Count == 0 || (value is LookupCustomValue lookup && Options.Contains(lookup.OptionId));
    }

    /// <summary>The kinds that have a filter operator (B-10).</summary>
    public static bool IsFilterable(FieldKind kind) =>
        kind is FieldKind.Text or FieldKind.MultilineText or FieldKind.Url or FieldKind.Number or FieldKind.Date or FieldKind.Lookup;
}
