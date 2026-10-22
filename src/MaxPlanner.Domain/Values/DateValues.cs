using MaxPlanner.Domain.Common;

namespace MaxPlanner.Domain.Values;

/// <summary>
/// Actual work dates of a task or Subtask card. Either may be absent; when both exist,
/// Start ≤ End (B-04). These are calendar days, never converted through UTC.
/// </summary>
public sealed record ActualDates
{
    private ActualDates(DateOnly? start, DateOnly? end)
    {
        Start = start;
        End = end;
    }

    public static ActualDates None { get; } = new(null, null);

    public DateOnly? Start { get; }

    public DateOnly? End { get; }

    public static DomainResult<ActualDates> Create(DateOnly? start, DateOnly? end) =>
        start is { } s && end is { } e && s > e
            ? DomainResult.Failure<ActualDates>(DomainErrorCodes.DateRangeInverted, "EndDate")
            : DomainResult.Success(new ActualDates(start, end));
}

/// <summary>
/// A CR's planned schedule. Both dates or none (no range); one-day ranges have equal endpoints.
/// </summary>
/// <remarks>B-04/B-15: the range is inclusive, so a CR planned to end today is not yet overdue.</remarks>
public sealed record PlannedRange
{
    private PlannedRange(DateOnly start, DateOnly end)
    {
        Start = start;
        End = end;
    }

    public DateOnly Start { get; }

    public DateOnly End { get; }

    /// <summary>Number of calendar days covered, counting both endpoints.</summary>
    public int DurationDays => End.DayNumber - Start.DayNumber + 1;

    public static DomainResult<PlannedRange> Create(DateOnly start, DateOnly end) =>
        start > end
            ? DomainResult.Failure<PlannedRange>(DomainErrorCodes.DateRangeInverted, "PlannedEnd")
            : DomainResult.Success(new PlannedRange(start, end));

    /// <summary>Both values or neither: a half-filled planned range is invalid (B-04).</summary>
    public static DomainResult<PlannedRange?> CreateOptional(DateOnly? start, DateOnly? end) =>
        (start, end) switch
        {
            (null, null) => DomainResult.Success<PlannedRange?>(null),
            ({ } s, { } e) => Create(s, e).Map<PlannedRange?>(r => r),
            (null, _) => DomainResult.Failure<PlannedRange?>(DomainErrorCodes.Required, "PlannedStart"),
            (_, null) => DomainResult.Failure<PlannedRange?>(DomainErrorCodes.Required, "PlannedEnd"),
        };

    public bool Contains(DateOnly day) => day >= Start && day <= End;

    /// <summary>B-10 interval overlap; absent filter bounds are open.</summary>
    public bool Overlaps(DateOnly? from, DateOnly? to) =>
        (to is null || Start <= to) && (from is null || End >= from);
}

/// <summary>What one command does to one actual date: keep it, set it, or explicitly clear it.</summary>
public readonly record struct DateIntent
{
    private DateIntent(DateIntentKind kind, DateOnly? value)
    {
        Kind = kind;
        Value = value;
    }

    public static DateIntent Unchanged => default;

    public static DateIntent Cleared => new(DateIntentKind.Cleared, null);

    public DateIntentKind Kind { get; }

    public DateOnly? Value { get; }

    public static DateIntent Set(DateOnly value) => new(DateIntentKind.Set, value);
}

public enum DateIntentKind
{
    Unchanged,
    Set,
    Cleared,
}
