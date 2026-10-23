using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;

namespace MaxPlanner.Application.Transfer;

/// <summary>T-10: create-only is the default; merge needs an explicit choice and a preview.</summary>
public enum ImportMode
{
    CreateOnly,
    Merge,
}

/// <summary>How a preview finding affects the plan.</summary>
public enum ImportSeverity
{
    /// <summary>Blocks Apply until the source or the mapping is corrected.</summary>
    Error,

    /// <summary>The record is not applied (for example a change to a permanent link, AC-29); the rest can be.</summary>
    Conflict,

    /// <summary>Applied, but the user should know (a value that was proposed or kept).</summary>
    Warning,

    Info,
}

/// <summary>Stable entity names used in findings, counts, and the import ledger.</summary>
public static class ImportEntity
{
    public const string File = "file";
    public const string Lookup = "lookup";
    public const string Field = "field";
    public const string Task = "task";
    public const string SubtaskCard = "subtaskCard";
    public const string ChangeRequest = "changeRequest";
    public const string Link = "link";
    public const string Review = "review";
    public const string GeneralTodo = "generalTodo";
}

/// <summary>Stable finding codes; Presentation shows <c>Import_Finding_{Code}</c>.</summary>
public static class ImportFindingCodes
{
    public const string TooManyRecords = "too-many-records";
    public const string Invalid = "invalid";
    public const string Exists = "exists";
    public const string Unchanged = "unchanged";
    public const string Deleted = "deleted";
    public const string DuplicateInSource = "duplicate-in-source";
    public const string DuplicateCrNumber = "duplicate-cr-number";
    public const string MatchedByNumber = "matched-by-number";
    public const string UnknownReference = "unknown-reference";
    public const string MissingParent = "missing-parent";
    public const string LinkChangeRefused = "link-change-refused";
    public const string UnlinkRefused = "unlink-refused";
    public const string PermanentLink = "permanent-link";
    public const string LinkTargetSkipped = "link-target-skipped";
    public const string TitlePreserved = "title-preserved";
    public const string LookupCreated = "lookup-created";
    public const string LookupMapped = "lookup-mapped";
    public const string FieldCreated = "field-created";
    public const string FieldMapped = "field-mapped";
    public const string FieldKindMismatch = "field-kind-mismatch";
    public const string OptionCreated = "option-created";
    public const string DefaultsApplied = "defaults-applied";
    public const string DefaultsKept = "defaults-kept";
    public const string DefaultUsed = "default-used";
    public const string OwnerRequired = "owner-required";
    public const string ConflictingCrValues = "conflicting-cr-values";
    public const string CrValueProposed = "cr-value-proposed";
    public const string MultipleCrReferences = "multiple-cr-references";
    public const string AlreadyImported = "already-imported";
    public const string AmbiguousColumn = "ambiguous-column";
    public const string UnknownColumn = "unknown-column";
    public const string MissingColumn = "missing-column";
    public const string InvalidDate = "invalid-date";
    public const string InvalidBoolean = "invalid-boolean";
    public const string PreservedText = "preserved-text";
    public const string NoTitle = "no-title";
}

/// <param name="RecordKey">The record ID, or the source key of an external record; never record content.</param>
/// <param name="Field">The offending field, when one is known.</param>
/// <param name="Detail">A short non-sensitive detail such as a lookup name or a CR number.</param>
public sealed record ImportFinding(ImportSeverity Severity, string Entity, string RecordKey, string Code, string? Field = null, string? Detail = null);

public enum ImportAction
{
    Create,
    Update,
    Skip,
    Conflict,
    Error,
}

/// <summary>Per-entity Create / Update / Skip / Conflict / Error counts (T-10 preview).</summary>
public sealed record ImportEntityCounts(int Create, int Update, int Skip, int Conflict, int Error)
{
    public static ImportEntityCounts Zero { get; } = new(0, 0, 0, 0, 0);

    public ImportEntityCounts Add(ImportAction action) => action switch
    {
        ImportAction.Create => this with { Create = Create + 1 },
        ImportAction.Update => this with { Update = Update + 1 },
        ImportAction.Skip => this with { Skip = Skip + 1 },
        ImportAction.Conflict => this with { Conflict = Conflict + 1 },
        _ => this with { Error = Error + 1 },
    };
}

/// <summary>B-13.4: which kinds of data the source contains, so the preview can say what cannot be reconstructed.</summary>
public sealed record ImportPresence(bool Tasks, bool ChecklistItems, bool SubtaskCards, bool ChangeRequests, bool Reviews, bool GeneralTodos, bool Dates, bool Notes);

/// <summary>A record to write: a create when <see cref="ExpectedRevision"/> is null, otherwise an update of that revision.</summary>
public sealed record PlannedRecord<T>(T Record, long? ExpectedRevision)
{
    public bool IsCreate => ExpectedRevision is null;
}

/// <summary>A B-13.5 ledger row the import adds: source namespace, entity, source key, and the record it became.</summary>
public sealed record ImportLedgerEntry(string SourceNamespace, string EntityKind, string SourceKey, Guid RecordId);

/// <summary>A planned permanent link between a task and a CR (ADR 0006).</summary>
/// <param name="TaskExists">The task already exists unlinked; Apply rechecks that it is still unlinked.</param>
public sealed record PlannedLink(CrTaskLink Link, bool TaskExists);

/// <summary>
/// The reviewed outcome of an import preview (T-10): every record already validated into its domain form, the
/// findings, and the counts. Applying it rechecks everything it depends on inside one transaction.
/// </summary>
public sealed record ImportPlan
{
    public required ImportMode Mode { get; init; }

    /// <summary>The workflow the preview was made against (<see cref="ImportPlanner.WorkflowStamp"/>).</summary>
    public required string WorkflowStamp { get; init; }

    /// <summary>The complete workflow after the import's new lookups, options, and fields; null when unchanged.</summary>
    public WorkflowSnapshot? NewWorkflow { get; init; }

    public IReadOnlyList<PlannedRecord<ChangeRequest>> ChangeRequests { get; init; } = [];

    public IReadOnlyList<PlannedRecord<TaskItem>> Tasks { get; init; } = [];

    public IReadOnlyList<PlannedLink> Links { get; init; } = [];

    public IReadOnlyList<PlannedRecord<SubtaskCard>> SubtaskCards { get; init; } = [];

    public IReadOnlyList<PlannedRecord<ReviewItem>> Reviews { get; init; } = [];

    public IReadOnlyList<PlannedRecord<GeneralTodo>> GeneralTodos { get; init; } = [];

    public IReadOnlyList<ImportLedgerEntry> LedgerEntries { get; init; } = [];

    public IReadOnlyList<ImportFinding> Findings { get; init; } = [];

    public IReadOnlyDictionary<string, ImportEntityCounts> Counts { get; init; } = new Dictionary<string, ImportEntityCounts>();

    public required ImportPresence Presence { get; init; }

    /// <summary>B-13.9: the target had no business records; a non-empty target gets a safety backup before Apply.</summary>
    public bool IsTargetEmpty { get; init; }

    public bool HasErrors => Findings.Any(f => f.Severity == ImportSeverity.Error);

    public int RecordWriteCount =>
        ChangeRequests.Count + Tasks.Count + Links.Count + SubtaskCards.Count + Reviews.Count + GeneralTodos.Count;

    public bool HasChanges => RecordWriteCount > 0 || NewWorkflow is not null || LedgerEntries.Count > 0;

    /// <summary>Apply is disabled while errors exist (design §4.6) and when nothing would change.</summary>
    public bool CanApply => !HasErrors && HasChanges;
}
