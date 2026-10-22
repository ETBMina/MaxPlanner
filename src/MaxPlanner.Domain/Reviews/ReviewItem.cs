using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Domain.Reviews;

/// <summary>Editable review fields (B-17.1). CR and task numbers are free text, never links or identities.</summary>
public sealed record ReviewContent(
    TitleText Title,
    TaskNumber? CrNumberText,
    TaskNumber? TaskNumberText,
    IReadOnlyList<ServiceText> Services,
    PersonId? DeveloperId,
    LongText? Comments);

/// <summary>A record of reviewing someone else's work, kept without a CR or task (B-17).</summary>
public sealed record ReviewItem
{
    private ReviewItem()
    {
    }

    public ReviewItemId Id { get; private init; }

    public ReviewContent Content { get; private init; } = null!;

    public bool IsDone { get; private init; }

    /// <summary>The local day the review was marked done; null while open. Drives week/month counts (B-15).</summary>
    public DateOnly? CompletedOn { get; private init; }

    public long Rank { get; private init; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public DateTimeOffset UpdatedAtUtc { get; private init; }

    public long Revision { get; private init; }

    public static ReviewItem Create(ReviewItemId id, ReviewContent content, long rank, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new ReviewItem
        {
            Id = id,
            Content = Normalize(content),
            Rank = rank,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Revision = 1,
        };
    }

    /// <summary>
    /// Rebuilds a stored review item exactly as persisted (services are not re-normalized), without a
    /// revision bump. A done item must have a completion day and an open one must not (B-17.1).
    /// </summary>
    public static DomainResult<ReviewItem> Rehydrate(
        ReviewItemId id,
        ReviewContent content,
        bool isDone,
        DateOnly? completedOn,
        long rank,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (isDone != completedOn.HasValue)
        {
            return DomainResult.Failure<ReviewItem>(DomainErrorCodes.InvalidStoredValue, "CompletedOn");
        }

        return StoredRecord.CheckAudit(createdAtUtc, updatedAtUtc, revision) is { } audit
            ? DomainResult.Failure<ReviewItem>(audit)
            : DomainResult.Success(new ReviewItem
            {
                Id = id,
                Content = content with { Services = [.. content.Services] },
                IsDone = isDone,
                CompletedOn = completedOn,
                Rank = rank,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = updatedAtUtc,
                Revision = revision,
            });
    }

    public ReviewItem Edit(ReviewContent content, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(content);
        return (this with { Content = Normalize(content) }).Touch(nowUtc);
    }

    /// <summary>Checking sets CompletedOn to today; unchecking clears it (B-17.1). Re-checking an already done item keeps its date.</summary>
    public ReviewItem SetDone(bool isDone, DateOnly today, DateTimeOffset nowUtc) =>
        isDone == IsDone ? this : (this with { IsDone = isDone, CompletedOn = isDone ? today : null }).Touch(nowUtc);

    public ReviewItem MoveTo(long rank, DateTimeOffset nowUtc) => (this with { Rank = rank }).Touch(nowUtc);

    private static ReviewContent Normalize(ReviewContent content) =>
        content with { Services = [.. content.Services.DistinctBy(s => s.Value, StringComparer.OrdinalIgnoreCase)] };

    private ReviewItem Touch(DateTimeOffset nowUtc) => this with { UpdatedAtUtc = nowUtc, Revision = Revision + 1 };
}
