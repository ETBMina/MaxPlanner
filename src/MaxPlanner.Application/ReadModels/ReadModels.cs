using MaxPlanner.Application.Abstractions;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Application.ReadModels;

/// <summary>A lookup reference with its current display name.</summary>
public sealed record LookupRef<TId>(TId Id, string Name)
    where TId : struct;

/// <summary>
/// The values a task (and its Subtask cards) shows from its linked CR (T-13). Always resolved from the
/// current CR; a task never stores them. For an unlinked task every value is null except
/// <see cref="SquadName"/>, which is the "General" label.
/// </summary>
public sealed record InheritedCrValues(
    ChangeRequestId? ChangeRequestId,
    string? CrNumber,
    string? TaskNumber,
    SquadId? SquadId,
    string SquadName,
    PersonId? OwnerId,
    string? OwnerName);

/// <summary>A task row/card as every view displays it (T-05, B-07).</summary>
public sealed record TaskReadModel(
    TaskId Id,
    BoardCardKind Kind,
    string EffectiveTitle,
    bool IsTitleInherited,
    string? Service,
    LookupRef<TaskStatusId> Status,
    int StatusOrder,
    bool IsCompleted,
    LookupRef<PriorityId> Priority,
    int PriorityOrder,
    InheritedCrValues Cr,
    IReadOnlyList<LookupRef<PersonId>> Designers,
    IReadOnlyList<LookupRef<PersonId>> Developers,
    IReadOnlyList<LookupRef<TagId>> Tags,
    string? Notes,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool IsArchived,
    long Rank,
    int ChecklistDone,
    int ChecklistTotal,
    int SubtaskCardCount,
    IReadOnlyDictionary<FieldId, CustomValue> CustomValues,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Revision);

/// <summary>A Subtask card with the parent's title and service and the CR values inherited through the parent (B-16.2).</summary>
public sealed record SubtaskCardReadModel(
    SubtaskCardId Id,
    BoardCardKind Kind,
    TaskId ParentTaskId,
    string Subtitle,
    string ParentTitle,
    string? ParentService,
    bool IsParentArchived,
    LookupRef<TaskStatusId> Status,
    int StatusOrder,
    bool IsCompleted,
    InheritedCrValues Cr,
    string? Notes,
    DateOnly? StartDate,
    DateOnly? EndDate,
    long Rank,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Revision);

/// <summary>A CR as Calendar, Records mode, and the Dashboard display it (B-08, B-15).</summary>
/// <param name="IsOverdue">Computed for the "today" passed to the composer; never persisted (B-15).</param>
public sealed record ChangeRequestReadModel(
    ChangeRequestId Id,
    string Number,
    string NormalizedNumber,
    string Title,
    string? TaskNumber,
    LookupRef<PersonId> Owner,
    SquadId? SquadId,
    string SquadName,
    LookupRef<CrStatusId> Status,
    int StatusOrder,
    bool IsCompleted,
    bool IsBacklog,
    LookupRef<PriorityId> Priority,
    int PriorityOrder,
    DateOnly? PlannedStart,
    DateOnly? PlannedEnd,
    string? EmailSubject,
    string? SolutionDesignUrl,
    string? Description,
    IReadOnlyDictionary<FieldId, CustomValue> CustomValues,
    bool IsOverdue,
    int DaysOverdue,
    int LinkedTaskCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Revision);
