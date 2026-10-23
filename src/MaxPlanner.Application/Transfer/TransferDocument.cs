using System.Text.Json.Serialization;

namespace MaxPlanner.Application.Transfer;

// T-10: the versioned transfer DTOs (docs/schemas/transfer-v1.schema.json). They are the interchange contract
// for JSON, CSV, and XLSX, never domain records or database rows. Property names are part of the published
// format: renaming one is a format change that needs a new formatVersion and a documented migration (B-12).

/// <summary>Constants of the published transfer format.</summary>
public static class TransferFormat
{
    /// <summary>The <c>formatVersion</c> this app writes and the newest it reads (B-12: newer versions are rejected).</summary>
    public const int CurrentVersion = 1;

    /// <summary>The <c>format</c> marker of a Max Planner transfer file.</summary>
    public const string Marker = "max-planner-transfer";

    public const string ScopeEntireDataset = "entire-dataset";

    public const string ScopeCurrentView = "current-view";
}

/// <summary>
/// One transfer file's content (B-12 JSON envelope). An export fills every collection; an import may supply only
/// some (a single CSV table, an external file). Empty collections are written explicitly.
/// </summary>
public sealed record TransferDocument
{
    public string Format { get; init; } = TransferFormat.Marker;

    public int FormatVersion { get; init; } = TransferFormat.CurrentVersion;

    /// <summary>The exporting dataset; null for external sources.</summary>
    public Guid? DatasetId { get; init; }

    public DateTimeOffset? ExportedAtUtc { get; init; }

    /// <summary>Entire dataset or a view export; a view export is never a backup (T-09).</summary>
    public TransferScope? Scope { get; init; }

    public TransferLookups Lookups { get; init; } = new();

    public IReadOnlyList<TransferFieldDefinition> FieldDefinitions { get; init; } = [];

    /// <summary>The business defaults, including the default CR Owner (B-12); null when the source has none.</summary>
    public TransferDefaults? Defaults { get; init; }

    public IReadOnlyList<TransferTask> Tasks { get; init; } = [];

    public IReadOnlyList<TransferSubtaskCard> SubtaskCards { get; init; } = [];

    public IReadOnlyList<TransferChangeRequest> ChangeRequests { get; init; } = [];

    /// <summary>The permanent task-to-CR links (T-05); CR-owned values are stored once, on the CR (T-13).</summary>
    public IReadOnlyList<TransferLink> Links { get; init; } = [];

    public IReadOnlyList<TransferReview> Reviews { get; init; } = [];

    public IReadOnlyList<TransferGeneralTodo> GeneralTodos { get; init; } = [];

    /// <summary>Top-level business records, the unit of the T-10 50,000-record limit.</summary>
    [JsonIgnore]
    public int TopLevelRecordCount => Tasks.Count + SubtaskCards.Count + ChangeRequests.Count + Reviews.Count + GeneralTodos.Count;
}

/// <param name="Kind"><see cref="TransferFormat.ScopeEntireDataset"/> or <see cref="TransferFormat.ScopeCurrentView"/>.</param>
/// <param name="EntityType">For a view export: the entity type of the view, for example <c>tasks</c>.</param>
/// <param name="Description">For a view export: the filters that defined it, as shown to the user.</param>
public sealed record TransferScope(string Kind, string? EntityType = null, string? Description = null);

public sealed record TransferLookups
{
    public IReadOnlyList<TransferTaskStatus> TaskStatuses { get; init; } = [];

    public IReadOnlyList<TransferCrStatus> CrStatuses { get; init; } = [];

    public IReadOnlyList<TransferOrderedLookup> Priorities { get; init; } = [];

    public IReadOnlyList<TransferOrderedLookup> Squads { get; init; } = [];

    public IReadOnlyList<TransferNamedLookup> People { get; init; } = [];

    public IReadOnlyList<TransferNamedLookup> Tags { get; init; } = [];
}

/// <summary>A task status with its semantic flags (B-04); behavior never comes from the name.</summary>
public sealed record TransferTaskStatus(Guid Id, string Name, int Order, bool IsCompleted, bool SetsStartDate, bool IsBacklog);

public sealed record TransferCrStatus(Guid Id, string Name, int Order, bool IsCompleted, bool IsBacklog);

public sealed record TransferOrderedLookup(Guid Id, string Name, int Order);

public sealed record TransferNamedLookup(Guid Id, string Name);

public sealed record TransferFieldOption(Guid Id, string Label, int Order);

/// <param name="Target"><c>Task</c> or <c>ChangeRequest</c>.</param>
/// <param name="Kind">One of the B-11 kinds: <c>Text</c>, <c>MultilineText</c>, <c>Number</c>, <c>Date</c>, <c>Lookup</c>, <c>People</c>, <c>Tags</c>, <c>Url</c>.</param>
public sealed record TransferFieldDefinition(
    Guid Id,
    string Key,
    string Target,
    string Label,
    string Kind,
    bool IsRequired,
    TransferValue? DefaultValue,
    int DisplayOrder,
    IReadOnlyList<TransferFieldOption> Options,
    bool IsActive);

/// <summary>A typed custom value; <see cref="Kind"/> selects the property that holds it (B-11: no coercion).</summary>
/// <param name="Ids">People or Tags values (person or tag IDs).</param>
public sealed record TransferValue(string Kind, string? Text = null, decimal? Number = null, DateOnly? Date = null, Guid? Option = null, IReadOnlyList<Guid>? Ids = null);

public sealed record TransferDefaults(
    Guid DefaultTaskStatusId,
    Guid DefaultCrStatusId,
    Guid DefaultPriorityId,
    Guid? DefaultCrOwnerId,
    Guid CalendarSchedulingStatusId,
    Guid CrBacklogStatusId);

/// <summary>Content kept but never shown: the pre-link title (ADR 0005), unselected CR references (B-13.6), unknown fields.</summary>
public sealed record TransferPreserved(string? PreLinkTitle, IReadOnlyList<string>? UnselectedCrReferences, string? LegacyJson);

public sealed record TransferChecklistItem(Guid Id, string Text, bool IsCompleted, long Order);

/// <summary>
/// A task. For a linked task <see cref="OwnTitle"/> is null: the title is the CR's (ADR 0005). Nullable members are
/// optional in partial (merge) sources.
/// </summary>
public sealed record TransferTask
{
    public required Guid Id { get; init; }

    public string? OwnTitle { get; init; }

    public string? Service { get; init; }

    public Guid? StatusId { get; init; }

    public Guid? PriorityId { get; init; }

    public IReadOnlyList<Guid>? DesignerIds { get; init; }

    public IReadOnlyList<Guid>? DeveloperIds { get; init; }

    public IReadOnlyList<Guid>? TagIds { get; init; }

    public string? Notes { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public bool? IsArchived { get; init; }

    /// <summary>The Board rank (B-07).</summary>
    public long? Order { get; init; }

    public IReadOnlyList<TransferChecklistItem>? ChecklistItems { get; init; }

    /// <summary>Custom values by field ID (<c>D</c> format), including values of deactivated fields (B-11).</summary>
    public IReadOnlyDictionary<string, TransferValue>? CustomValues { get; init; }

    public TransferPreserved? Preserved { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public long? Revision { get; init; }

    /// <summary>
    /// The member names a partial source supplied (tabular columns, T-10 merge: absent means unchanged). Null means
    /// the record is complete, which is how a JSON export is read. Never serialized.
    /// </summary>
    [JsonIgnore]
    public IReadOnlySet<string>? SuppliedFields { get; init; }
}

public sealed record TransferSubtaskCard
{
    public required Guid Id { get; init; }

    public required Guid ParentTaskId { get; init; }

    public string? Subtitle { get; init; }

    public Guid? StatusId { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public string? Notes { get; init; }

    public long? Order { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public long? Revision { get; init; }

    [JsonIgnore]
    public IReadOnlySet<string>? SuppliedFields { get; init; }
}

public sealed record TransferChangeRequest
{
    public required Guid Id { get; init; }

    public string? Number { get; init; }

    public string? Title { get; init; }

    public Guid? OwnerId { get; init; }

    public Guid? SquadId { get; init; }

    public Guid? StatusId { get; init; }

    public Guid? PriorityId { get; init; }

    public DateOnly? PlannedStart { get; init; }

    public DateOnly? PlannedEnd { get; init; }

    public string? EmailSubject { get; init; }

    public string? SolutionDesignUrl { get; init; }

    public string? Description { get; init; }

    public string? TaskNumber { get; init; }

    public IReadOnlyDictionary<string, TransferValue>? CustomValues { get; init; }

    public TransferPreserved? Preserved { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public long? Revision { get; init; }

    [JsonIgnore]
    public IReadOnlySet<string>? SuppliedFields { get; init; }
}

public sealed record TransferLink(Guid TaskId, Guid ChangeRequestId, DateTimeOffset? LinkedAtUtc);

public sealed record TransferReview
{
    public required Guid Id { get; init; }

    public string? Title { get; init; }

    public string? CrNumberText { get; init; }

    public string? TaskNumberText { get; init; }

    public IReadOnlyList<string>? Services { get; init; }

    public Guid? DeveloperId { get; init; }

    public string? Comments { get; init; }

    public bool? IsDone { get; init; }

    public DateOnly? CompletedOn { get; init; }

    public long? Order { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public long? Revision { get; init; }

    [JsonIgnore]
    public IReadOnlySet<string>? SuppliedFields { get; init; }
}

public sealed record TransferGeneralTodo
{
    public required Guid Id { get; init; }

    public string? Text { get; init; }

    public bool? IsCompleted { get; init; }

    public long? Order { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public long? Revision { get; init; }

    [JsonIgnore]
    public IReadOnlySet<string>? SuppliedFields { get; init; }
}
