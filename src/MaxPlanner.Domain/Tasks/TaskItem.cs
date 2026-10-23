using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Domain.Tasks;

/// <summary>User-editable task fields that are not workflow, dates, or ordering.</summary>
public sealed record TaskContent(
    TitleText? OwnTitle,
    ServiceText? Service,
    IReadOnlyList<PersonId> DesignerIds,
    IReadOnlyList<PersonId> DeveloperIds,
    IReadOnlyList<TagId> TagIds,
    LongText? Notes,
    IReadOnlyDictionary<FieldId, CustomValue> CustomValues);

/// <summary>
/// Everything a task editor saves at once (B-02.8): the content, priority, explicit date edits, and the
/// complete ordered checklist (existing items keep their IDs, B-02.7).
/// </summary>
public sealed record TaskEdit(
    TaskContent Content,
    PriorityId PriorityId,
    DateIntent Start,
    DateIntent End,
    IReadOnlyList<ChecklistItem> ChecklistItems);

/// <summary>
/// A task (B-02). Immutable: every change returns a new instance with the next revision, so cached
/// read models are never mutated in place (AGENTS §4).
/// </summary>
/// <remarks>
/// <see cref="ChangeRequestId"/> is loaded from the link table, which stays the only persisted
/// authority for the relationship (T-05). Title, task number, squad, and owner of a linked task are
/// never stored here; read models resolve them from the current CR.
/// </remarks>
public sealed record TaskItem
{
    private TaskItem()
    {
    }

    public TaskId Id { get; private init; }

    /// <summary>The task's own title: required while unlinked, null once linked (ADR 0005).</summary>
    public TitleText? OwnTitle { get; private init; }

    public ServiceText? Service { get; private init; }

    public TaskStatusId StatusId { get; private init; }

    public PriorityId PriorityId { get; private init; }

    public IReadOnlyList<PersonId> DesignerIds { get; private init; } = [];

    public IReadOnlyList<PersonId> DeveloperIds { get; private init; } = [];

    public IReadOnlyList<TagId> TagIds { get; private init; } = [];

    public LongText? Notes { get; private init; }

    public ActualDates Dates { get; private init; } = ActualDates.None;

    public bool IsArchived { get; private init; }

    public long Rank { get; private init; }

    public IReadOnlyList<ChecklistItem> ChecklistItems { get; private init; } = [];

    /// <summary>Typed custom values by field, including values of deactivated fields (B-11).</summary>
    public IReadOnlyDictionary<FieldId, CustomValue> CustomValues { get; private init; } = CustomValue.NoValues;

    /// <summary>Permanent once set (ADR 0006); null for an unlinked task.</summary>
    public ChangeRequestId? ChangeRequestId { get; private init; }

    public PreservedMetadata Preserved { get; private init; } = PreservedMetadata.Empty;

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public DateTimeOffset UpdatedAtUtc { get; private init; }

    public long Revision { get; private init; }

    public bool IsLinked => ChangeRequestId is not null;

    /// <summary>
    /// Creates a task in <paramref name="status"/>, applying B-04 creation dates. When
    /// <paramref name="linkTo"/> is set the task is created linked (e.g. "Create task" from a CR).
    /// </summary>
    /// <param name="checklistItems">Initial items with new IDs, e.g. from <see cref="CopyTaskPolicy"/>; null for none.</param>
    public static DomainResult<TaskItem> Create(
        TaskId id,
        TaskContent content,
        TaskStatusDefinition status,
        PriorityId priority,
        DateIntent start,
        DateIntent end,
        ChangeRequestId? linkTo,
        long rank,
        DateOnly today,
        DateTimeOffset nowUtc,
        IReadOnlyList<ChecklistItem>? checklistItems = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(status);

        if (InheritancePolicy.ValidateOwnFields(linkTo is not null, content.OwnTitle, content.Service) is { } invalid)
        {
            return DomainResult.Failure<TaskItem>(invalid);
        }

        checklistItems ??= [];
        if (checklistItems.Count > DomainLimits.ChecklistItemsPerTask)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.TooManyItems, "ChecklistItems", DomainLimits.ChecklistItemsPerTask);
        }

        return TaskDatePolicy.Apply(ActualDates.None, from: null, status, start, end, today).Map(dates => new TaskItem
        {
            Id = id,
            StatusId = status.Id,
            PriorityId = priority,
            Dates = dates,
            Rank = rank,
            ChangeRequestId = linkTo,
            ChecklistItems = [.. checklistItems],
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Revision = 1,
        }.WithContent(content));
    }

    /// <summary>
    /// Rebuilds a stored task exactly as persisted, for storage mappers. Creation rules, date automation,
    /// and revision bumps are not applied, but structural invariants are checked so a corrupt row is
    /// reported instead of silently loaded.
    /// </summary>
    /// <param name="changeRequestId">The CR from the task's link row, or null when it has none (T-05).</param>
    public static DomainResult<TaskItem> Rehydrate(
        TaskId id,
        TaskContent content,
        TaskStatusId statusId,
        PriorityId priorityId,
        DateOnly? startDate,
        DateOnly? endDate,
        bool isArchived,
        long rank,
        IReadOnlyList<ChecklistItem> checklistItems,
        ChangeRequestId? changeRequestId,
        PreservedMetadata preserved,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(checklistItems);
        ArgumentNullException.ThrowIfNull(preserved);

        if (InheritancePolicy.ValidateOwnFields(changeRequestId is not null, content.OwnTitle, content.Service) is { } ownership)
        {
            return DomainResult.Failure<TaskItem>(ownership);
        }

        if (checklistItems.Count > DomainLimits.ChecklistItemsPerTask)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.TooManyItems, "ChecklistItems", DomainLimits.ChecklistItemsPerTask);
        }

        if (checklistItems.DistinctBy(i => i.Id).Count() != checklistItems.Count)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.InvalidStoredValue, "ChecklistItems");
        }

        if (StoredRecord.CheckAudit(createdAtUtc, updatedAtUtc, revision) is { } audit)
        {
            return DomainResult.Failure<TaskItem>(audit);
        }

        return ActualDates.Create(startDate, endDate).Map(dates => new TaskItem
        {
            Id = id,
            OwnTitle = content.OwnTitle,
            Service = content.Service,
            DesignerIds = [.. content.DesignerIds],
            DeveloperIds = [.. content.DeveloperIds],
            TagIds = [.. content.TagIds],
            Notes = content.Notes,
            CustomValues = content.CustomValues.ToDictionary().AsReadOnly(),
            StatusId = statusId,
            PriorityId = priorityId,
            Dates = dates,
            IsArchived = isArchived,
            Rank = rank,
            ChecklistItems = [.. checklistItems],
            ChangeRequestId = changeRequestId,
            Preserved = preserved,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            Revision = revision,
        });
    }

    public DomainResult<TaskItem> UpdateContent(TaskContent content, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (EnsureEditable() is { } readOnly)
        {
            return DomainResult.Failure<TaskItem>(readOnly);
        }

        return InheritancePolicy.ValidateOwnFields(IsLinked, content.OwnTitle, content.Service) is { } invalid
            ? DomainResult.Failure<TaskItem>(invalid)
            : DomainResult.Success(WithContent(content).Touch(nowUtc));
    }

    /// <summary>
    /// Applies a whole editor draft as one change with one revision (B-02.8): content, priority, a status
    /// transition with B-04 dates, the checklist, and optionally the permanent link to a CR (B-02.4).
    /// </summary>
    /// <param name="current">The definition of the task's current status (checked against <see cref="StatusId"/>).</param>
    /// <param name="target">The status after the edit; equal to <paramref name="current"/> when it is unchanged.</param>
    /// <param name="rank">The Board rank after the edit; the caller appends to the target column on a status change.</param>
    /// <param name="linkTo">
    /// Links this unlinked task in the same change. The edit is validated as the unlinked task it still is
    /// (its own title is required), then the title moves to preserved metadata exactly as <see cref="LinkTo"/> does.
    /// </param>
    public DomainResult<TaskItem> Edit(
        TaskEdit edit,
        TaskStatusDefinition current,
        TaskStatusDefinition target,
        long rank,
        ChangeRequestId? linkTo,
        DateOnly today,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(target);
        if (EnsureEditable() is { } readOnly)
        {
            return DomainResult.Failure<TaskItem>(readOnly);
        }

        if (current.Id != StatusId)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.StatusMismatch, "Status");
        }

        if (linkTo is not null && IsLinked)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.TaskAlreadyLinked, "ChangeRequest");
        }

        var content = edit.Content;
        if (InheritancePolicy.ValidateOwnFields(IsLinked, content.OwnTitle, content.Service) is { } ownership)
        {
            return DomainResult.Failure<TaskItem>(ownership);
        }

        // D10: a link needs a Service, because siblings under one CR share the CR's title.
        if (linkTo is not null && content.Service is null)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.ServiceRequiredWhenLinked, "Service");
        }

        var items = edit.ChecklistItems;
        if (items.Count > DomainLimits.ChecklistItemsPerTask)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.TooManyItems, "ChecklistItems", DomainLimits.ChecklistItemsPerTask);
        }

        if (items.DistinctBy(i => i.Id).Count() != items.Count)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.DuplicateKey, "ChecklistItems");
        }

        return TaskDatePolicy.Apply(Dates, current, target, edit.Start, edit.End, today).Map(dates =>
        {
            var edited = WithContent(content) with
            {
                PriorityId = edit.PriorityId,
                StatusId = target.Id,
                Dates = dates,
                Rank = rank,
                ChecklistItems = [.. items],
            };
            if (linkTo is { } changeRequest)
            {
                edited = edited with
                {
                    ChangeRequestId = changeRequest,
                    OwnTitle = null,
                    Preserved = Preserved with { PreLinkTitle = content.OwnTitle?.Value },
                };
            }

            return edited.Touch(nowUtc);
        });
    }

    public DomainResult<TaskItem> SetPriority(PriorityId priority, DateTimeOffset nowUtc) =>
        EnsureEditable() is { } readOnly
            ? DomainResult.Failure<TaskItem>(readOnly)
            : DomainResult.Success((this with { PriorityId = priority }).Touch(nowUtc));

    /// <summary>
    /// Moves to <paramref name="target"/> (possibly the same status, i.e. a reorder) at
    /// <paramref name="rank"/>, applying B-04 transition dates in the same change.
    /// </summary>
    /// <param name="current">The definition of the task's current status (checked against <see cref="StatusId"/>).</param>
    public DomainResult<TaskItem> MoveTo(
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
        if (EnsureEditable() is { } readOnly)
        {
            return DomainResult.Failure<TaskItem>(readOnly);
        }

        if (current.Id != StatusId)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.StatusMismatch, "Status");
        }

        return TaskDatePolicy.Apply(Dates, current, target, start, end, today)
            .Map(dates => (this with { StatusId = target.Id, Dates = dates, Rank = rank }).Touch(nowUtc));
    }

    /// <summary>Edits dates without a status transition, so no automatic date is filled (B-04).</summary>
    public DomainResult<TaskItem> EditDates(TaskStatusDefinition current, DateIntent start, DateIntent end, DateOnly today, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (EnsureEditable() is { } readOnly)
        {
            return DomainResult.Failure<TaskItem>(readOnly);
        }

        return current.Id != StatusId
            ? DomainResult.Failure<TaskItem>(DomainErrorCodes.StatusMismatch, "Status")
            : TaskDatePolicy.Apply(Dates, current, current, start, end, today).Map(d => (this with { Dates = d }).Touch(nowUtc));
    }

    /// <summary>
    /// Links this unlinked task to a CR, permanently (ADR 0006). The own title is kept only as
    /// preserved metadata; from now on the CR supplies the title (ADR 0005). Archived tasks may be
    /// linked (D11).
    /// </summary>
    public DomainResult<TaskItem> LinkTo(ChangeRequestId changeRequest, DateTimeOffset nowUtc)
    {
        if (IsLinked)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.TaskAlreadyLinked, "ChangeRequest");
        }

        if (Service is null)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.ServiceRequiredWhenLinked, "Service");
        }

        return DomainResult.Success((this with
        {
            ChangeRequestId = changeRequest,
            OwnTitle = null,
            Preserved = Preserved with { PreLinkTitle = OwnTitle?.Value },
        }).Touch(nowUtc));
    }

    public DomainResult<TaskItem> Archive(DateTimeOffset nowUtc) =>
        IsArchived
            ? DomainResult.Failure<TaskItem>(DomainErrorCodes.ArchivedReadOnly)
            : DomainResult.Success((this with { IsArchived = true }).Touch(nowUtc));

    /// <summary>Restores at <paramref name="rankAtEndOfColumn"/>: B-05 puts a restored task at the end of its column.</summary>
    public DomainResult<TaskItem> Restore(long rankAtEndOfColumn, DateTimeOffset nowUtc) =>
        IsArchived
            ? DomainResult.Success((this with { IsArchived = false, Rank = rankAtEndOfColumn }).Touch(nowUtc))
            : DomainResult.Failure<TaskItem>(DomainErrorCodes.NotArchived);

    public DomainResult<TaskItem> AddChecklistItem(ChecklistItemId id, ChecklistText text, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (EnsureEditable() is { } readOnly)
        {
            return DomainResult.Failure<TaskItem>(readOnly);
        }

        if (ChecklistItems.Count >= DomainLimits.ChecklistItemsPerTask)
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.TooManyItems, "ChecklistItems", DomainLimits.ChecklistItemsPerTask);
        }

        var rank = RankPolicy.After(ChecklistItems.Count == 0 ? null : ChecklistItems.Max(i => i.Rank));
        return DomainResult.Success((this with { ChecklistItems = [.. ChecklistItems, new ChecklistItem(id, text, false, rank)] }).Touch(nowUtc));
    }

    public DomainResult<TaskItem> EditChecklistItem(ChecklistItemId id, ChecklistText text, DateTimeOffset nowUtc) =>
        ReplaceChecklistItem(id, item => item with { Text = text }, nowUtc);

    /// <summary>B-04: toggling a checklist item never changes the task's status or dates.</summary>
    public DomainResult<TaskItem> SetChecklistItemCompleted(ChecklistItemId id, bool isCompleted, DateTimeOffset nowUtc) =>
        ReplaceChecklistItem(id, item => item with { IsCompleted = isCompleted }, nowUtc);

    public DomainResult<TaskItem> MoveChecklistItem(ChecklistItemId id, long rank, DateTimeOffset nowUtc) =>
        ReplaceChecklistItem(id, item => item with { Rank = rank }, nowUtc);

    /// <summary>Removes an item. Also the first half of "Pop out to card" (B-16.3), which returns the removed item.</summary>
    public DomainResult<(TaskItem Task, ChecklistItem Removed)> RemoveChecklistItem(ChecklistItemId id, DateTimeOffset nowUtc)
    {
        if (EnsureEditable() is { } readOnly)
        {
            return DomainResult.Failure<(TaskItem, ChecklistItem)>(readOnly);
        }

        var item = ChecklistItems.FirstOrDefault(i => i.Id == id);
        return item is null
            ? DomainResult.Failure<(TaskItem, ChecklistItem)>(DomainErrorCodes.ItemNotFound, "ChecklistItem")
            : DomainResult.Success(((this with { ChecklistItems = [.. ChecklistItems.Where(i => i.Id != id)] }).Touch(nowUtc), item));
    }

    private DomainResult<TaskItem> ReplaceChecklistItem(ChecklistItemId id, Func<ChecklistItem, ChecklistItem> change, DateTimeOffset nowUtc)
    {
        if (EnsureEditable() is { } readOnly)
        {
            return DomainResult.Failure<TaskItem>(readOnly);
        }

        if (ChecklistItems.All(i => i.Id != id))
        {
            return DomainResult.Failure<TaskItem>(DomainErrorCodes.ItemNotFound, "ChecklistItem");
        }

        return DomainResult.Success((this with { ChecklistItems = [.. ChecklistItems.Select(i => i.Id == id ? change(i) : i)] }).Touch(nowUtc));
    }

    private TaskItem WithContent(TaskContent content) => this with
    {
        OwnTitle = content.OwnTitle,
        Service = content.Service,
        DesignerIds = [.. content.DesignerIds.Distinct()],
        DeveloperIds = [.. content.DeveloperIds.Distinct()],
        TagIds = [.. content.TagIds.Distinct()],
        Notes = content.Notes,
        CustomValues = content.CustomValues.ToDictionary().AsReadOnly(),
    };

    private DomainError? EnsureEditable() =>
        IsArchived ? new DomainError(DomainErrorCodes.ArchivedReadOnly) : null;

    private TaskItem Touch(DateTimeOffset nowUtc) => this with { UpdatedAtUtc = nowUtc, Revision = Revision + 1 };
}
