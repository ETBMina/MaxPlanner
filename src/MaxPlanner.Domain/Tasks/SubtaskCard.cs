using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Domain.Tasks;

/// <summary>
/// A lightweight Board card owned by one task (B-16). Its title, service, and CR values are the
/// parent's and are resolved by read models; only the subtitle, status, dates, and notes are its own.
/// </summary>
/// <remarks>
/// B-16.4: date automation applies to the card's own dates and never to the parent's, so work on a
/// completed task can continue here without rewriting that task's history. Whether the parent is
/// archived (which makes the card read-only, B-16.6) is checked by the use case, which loads both.
/// </remarks>
public sealed record SubtaskCard
{
    private SubtaskCard()
    {
    }

    public SubtaskCardId Id { get; private init; }

    public TaskId ParentTaskId { get; private init; }

    public SubtitleText Subtitle { get; private init; } = null!;

    public TaskStatusId StatusId { get; private init; }

    public ActualDates Dates { get; private init; } = ActualDates.None;

    public LongText? Notes { get; private init; }

    public long Rank { get; private init; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public DateTimeOffset UpdatedAtUtc { get; private init; }

    public long Revision { get; private init; }

    public static DomainResult<SubtaskCard> Create(
        SubtaskCardId id,
        TaskId parentTaskId,
        SubtitleText subtitle,
        LongText? notes,
        TaskStatusDefinition status,
        DateIntent start,
        DateIntent end,
        long rank,
        DateOnly today,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        ArgumentNullException.ThrowIfNull(status);
        return TaskDatePolicy.Apply(ActualDates.None, from: null, status, start, end, today).Map(dates => new SubtaskCard
        {
            Id = id,
            ParentTaskId = parentTaskId,
            Subtitle = subtitle,
            Notes = notes,
            StatusId = status.Id,
            Dates = dates,
            Rank = rank,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Revision = 1,
        });
    }

    /// <summary>
    /// Rebuilds a stored card exactly as persisted, without creation rules, date automation, or a
    /// revision bump; structural invariants are still checked so a corrupt row is reported.
    /// </summary>
    public static DomainResult<SubtaskCard> Rehydrate(
        SubtaskCardId id,
        TaskId parentTaskId,
        SubtitleText subtitle,
        TaskStatusId statusId,
        DateOnly? startDate,
        DateOnly? endDate,
        LongText? notes,
        long rank,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        return StoredRecord.CheckAudit(createdAtUtc, updatedAtUtc, revision) is { } audit
            ? DomainResult.Failure<SubtaskCard>(audit)
            : ActualDates.Create(startDate, endDate).Map(dates => new SubtaskCard
            {
                Id = id,
                ParentTaskId = parentTaskId,
                Subtitle = subtitle,
                StatusId = statusId,
                Dates = dates,
                Notes = notes,
                Rank = rank,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = updatedAtUtc,
                Revision = revision,
            });
    }

    public SubtaskCard Edit(SubtitleText subtitle, LongText? notes, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        return (this with { Subtitle = subtitle, Notes = notes }).Touch(nowUtc);
    }

    public DomainResult<SubtaskCard> MoveTo(
        TaskStatusDefinition current,
        TaskStatusDefinition target,
        long rank,
        DateIntent start,
        DateIntent end,
        DateOnly today,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(target);
        return current.Id != StatusId
            ? DomainResult.Failure<SubtaskCard>(DomainErrorCodes.StatusMismatch, "Status")
            : TaskDatePolicy.Apply(Dates, current, target, start, end, today)
                .Map(dates => (this with { StatusId = target.Id, Dates = dates, Rank = rank }).Touch(nowUtc));
    }

    public DomainResult<SubtaskCard> EditDates(TaskStatusDefinition current, DateIntent start, DateIntent end, DateOnly today, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current.Id != StatusId
            ? DomainResult.Failure<SubtaskCard>(DomainErrorCodes.StatusMismatch, "Status")
            : TaskDatePolicy.Apply(Dates, current, current, start, end, today).Map(d => (this with { Dates = d }).Touch(nowUtc));
    }

    private SubtaskCard Touch(DateTimeOffset nowUtc) => this with { UpdatedAtUtc = nowUtc, Revision = Revision + 1 };
}
