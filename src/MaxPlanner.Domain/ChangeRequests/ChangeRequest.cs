using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Domain.ChangeRequests;

/// <summary>Editable CR fields (B-03.1). Number uniqueness is checked against the dataset by the use case.</summary>
public sealed record ChangeRequestContent(
    CrNumber Number,
    TitleText Title,
    PersonId OwnerId,
    SquadId? SquadId,
    PriorityId PriorityId,
    PlannedRange? Planned,
    EmailSubject? EmailSubject,
    HttpUrl? SolutionDesignUrl,
    LongText? Description,
    TaskNumber? TaskNumber,
    IReadOnlyDictionary<FieldId, CustomValue> CustomValues);

/// <summary>
/// A Change Request. It owns the Title, TaskNumber, Squad, and Owner that every linked task and
/// Subtask card displays (T-13). CRs are not archived (ADR 0020).
/// </summary>
public sealed record ChangeRequest
{
    private ChangeRequest()
    {
    }

    public ChangeRequestId Id { get; private init; }

    public CrNumber Number { get; private init; } = null!;

    public TitleText Title { get; private init; } = null!;

    public PersonId OwnerId { get; private init; }

    /// <summary>Null means no squad; displays as "General" (ADR 0020).</summary>
    public SquadId? SquadId { get; private init; }

    public CrStatusId StatusId { get; private init; }

    public PriorityId PriorityId { get; private init; }

    public PlannedRange? Planned { get; private init; }

    public EmailSubject? EmailSubject { get; private init; }

    public HttpUrl? SolutionDesignUrl { get; private init; }

    public LongText? Description { get; private init; }

    public TaskNumber? TaskNumber { get; private init; }

    /// <summary>Typed custom values by field, including values of deactivated fields (B-11).</summary>
    public IReadOnlyDictionary<FieldId, CustomValue> CustomValues { get; private init; } = CustomValue.NoValues;

    /// <summary>
    /// Unknown imported content kept for lossless export and backup (T-05). Never shown or edited, and kept
    /// unchanged by every edit.
    /// </summary>
    public PreservedMetadata Preserved { get; private init; } = PreservedMetadata.Empty;

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public DateTimeOffset UpdatedAtUtc { get; private init; }

    public long Revision { get; private init; }

    /// <param name="preserved">Unknown content from an import; null (the default) for none.</param>
    public static ChangeRequest Create(
        ChangeRequestId id,
        ChangeRequestContent content,
        CrStatusDefinition status,
        DateTimeOffset nowUtc,
        PreservedMetadata? preserved = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(status);
        return new ChangeRequest
        {
            Id = id,
            StatusId = status.Id,
            Preserved = preserved ?? PreservedMetadata.Empty,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Revision = 1,
        }.WithContent(content);
    }

    /// <summary>
    /// Rebuilds a stored CR exactly as persisted, without a revision bump. The content's value types
    /// already enforce the field rules (required owner, both-or-neither planned dates, valid URL); the
    /// audit values and preserved metadata are checked here so a corrupt row is reported.
    /// </summary>
    public static DomainResult<ChangeRequest> Rehydrate(
        ChangeRequestId id,
        ChangeRequestContent content,
        CrStatusId statusId,
        PreservedMetadata preserved,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(preserved);
        if (preserved.PreLinkTitle is not null)
        {
            // Only a task has a title from before it was linked (ADR 0005).
            return DomainResult.Failure<ChangeRequest>(DomainErrorCodes.InvalidStoredValue, "Preserved");
        }

        return StoredRecord.CheckAudit(createdAtUtc, updatedAtUtc, revision) is { } audit
            ? DomainResult.Failure<ChangeRequest>(audit)
            : DomainResult.Success(new ChangeRequest
            {
                Id = id,
                StatusId = statusId,
                Preserved = preserved,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = updatedAtUtc,
                Revision = revision,
            }.WithContent(content));
    }

    /// <summary>B-03.8: CR status is independent; changing it never touches linked tasks.</summary>
    public ChangeRequest Update(ChangeRequestContent content, CrStatusDefinition status, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(status);
        return (WithContent(content) with { StatusId = status.Id }).Touch(nowUtc);
    }

    /// <summary>
    /// B-08.5: scheduling a CR on a day sets a one-day range and the configured scheduling status in
    /// one change. Replacing an existing range is the caller's explicit, previewed choice.
    /// </summary>
    public ChangeRequest ScheduleOn(DateOnly day, CrStatusDefinition schedulingStatus, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(schedulingStatus);
        return (this with { Planned = PlannedRange.Create(day, day).Value, StatusId = schedulingStatus.Id }).Touch(nowUtc);
    }

    /// <summary>B-08.8: dragging one bar edge changes only that endpoint; inverted ranges are rejected.</summary>
    public DomainResult<ChangeRequest> SetPlannedEndpoint(PlannedEndpoint endpoint, DateOnly day, DateTimeOffset nowUtc)
    {
        if (Planned is null)
        {
            return DomainResult.Failure<ChangeRequest>(DomainErrorCodes.Required, "PlannedStart");
        }

        var range = endpoint == PlannedEndpoint.Start ? PlannedRange.Create(day, Planned.End) : PlannedRange.Create(Planned.Start, day);
        return range.Map(r => (this with { Planned = r }).Touch(nowUtc));
    }

    /// <summary>
    /// B-08.8 date-editor route: replaces both endpoints of an existing range in one change, so an
    /// intermediate inverted range never exists. The status is unchanged; inverted ranges are rejected.
    /// </summary>
    public DomainResult<ChangeRequest> Reschedule(DateOnly start, DateOnly end, DateTimeOffset nowUtc) =>
        Planned is null
            ? DomainResult.Failure<ChangeRequest>(DomainErrorCodes.Required, "PlannedStart")
            : PlannedRange.Create(start, end).Map(r => (this with { Planned = r }).Touch(nowUtc));

    /// <summary>B-08.11: explicitly clears both planned dates and sets the backlog status.</summary>
    public ChangeRequest MoveToBacklog(CrStatusDefinition backlogStatus, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(backlogStatus);
        return (this with { Planned = null, StatusId = backlogStatus.Id }).Touch(nowUtc);
    }

    /// <summary>
    /// B-04/B-11 lookup removal: points the CR's references to the removed value (status, priority, Owner,
    /// Squad, People/Tags custom values) at the replacement. A cleared squad becomes "General"; the Owner can
    /// only be replaced (ADR 0020). Planned dates are kept: this is not a status transition.
    /// </summary>
    /// <returns>The remapped CR, or null when it does not reference the removed value.</returns>
    public ChangeRequest? Remap(LookupRemap remap, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(remap);
        var changed = false;
        var result = this;
        if (remap.Targets(LookupKind.CrStatus, StatusId.Value))
        {
            result = result with { StatusId = new CrStatusId(remap.RequiredReplacement) };
            changed = true;
        }

        if (remap.Targets(LookupKind.Priority, PriorityId.Value))
        {
            result = result with { PriorityId = new PriorityId(remap.RequiredReplacement) };
            changed = true;
        }

        if (remap.Targets(LookupKind.Person, OwnerId.Value))
        {
            result = result with { OwnerId = new PersonId(remap.RequiredReplacement) };
            changed = true;
        }

        if (SquadId is { } squad && remap.Targets(LookupKind.Squad, squad.Value))
        {
            result = result with { SquadId = remap.ReplacementId is { } replacement ? new SquadId(replacement) : null };
            changed = true;
        }

        if (remap.MapCustomValues(CustomValues) is { } values)
        {
            result = result with { CustomValues = values };
            changed = true;
        }

        return changed ? result.Touch(nowUtc) : null;
    }

    private ChangeRequest WithContent(ChangeRequestContent content) => this with
    {
        Number = content.Number,
        Title = content.Title,
        OwnerId = content.OwnerId,
        SquadId = content.SquadId,
        PriorityId = content.PriorityId,
        Planned = content.Planned,
        EmailSubject = content.EmailSubject,
        SolutionDesignUrl = content.SolutionDesignUrl,
        Description = content.Description,
        TaskNumber = content.TaskNumber,
        CustomValues = content.CustomValues.ToDictionary().AsReadOnly(),
    };

    private ChangeRequest Touch(DateTimeOffset nowUtc) => this with { UpdatedAtUtc = nowUtc, Revision = Revision + 1 };
}

public enum PlannedEndpoint
{
    Start,
    End,
}
