using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Workflow;

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

    /// <summary>ADR 0024: a task keeps one Designer and one Developer; the detail names the people that were dropped.</summary>
    public const string PeopleDropped = "people-dropped";

    // ADR 0026: content selection and configuration replace.

    /// <summary>A configuration import ignores the file's records; the detail is their number.</summary>
    public const string RecordsIgnored = "records-ignored";

    /// <summary>Data only: a value the records use matches no existing value by ID or name.</summary>
    public const string ConfigurationMissing = "config-missing";

    /// <summary>A replace adds this lookup value or custom field.</summary>
    public const string ConfigurationAdded = "config-added";

    /// <summary>A replace changes the name, order, flags, or options of this lookup value or custom field.</summary>
    public const string ConfigurationUpdated = "config-updated";

    /// <summary>A replace removes this lookup value or Lookup option; in-use ones are remapped as chosen.</summary>
    public const string ConfigurationRemoved = "config-removed";

    /// <summary>A replace turns off a custom field the file does not have; its values are kept (B-11).</summary>
    public const string FieldDeactivated = "field-deactivated";

    /// <summary>An in-use removed value has no valid replacement yet (B-04); Apply is blocked.</summary>
    public const string RemovalUnresolved = "removal-unresolved";

    /// <summary>The file has no task status, CR status, or priority, which B-04 requires.</summary>
    public const string ConfigurationRequired = "config-required";

    /// <summary>The file's business defaults do not resolve to its own values.</summary>
    public const string DefaultsInvalid = "defaults-invalid";

    /// <summary>A view or data-only export holds only part of a configuration, so it cannot replace one.</summary>
    public const string ConfigurationIncomplete = "config-incomplete";

    /// <summary>A field's People or Tags default names a value the configuration does not have; that value is dropped.</summary>
    public const string DefaultDropped = "default-dropped";

    // ADR 0027: external mapping targets and unmapped columns.

    /// <summary>A Created value is not an ISO date or date-time.</summary>
    public const string InvalidCreated = "invalid-created";

    /// <summary>A Created value is later than the import time.</summary>
    public const string CreatedInFuture = "created-in-future";

    /// <summary>A value is not valid for the kind of the custom field its column maps to; the field is its label.</summary>
    public const string InvalidCustomValue = "invalid-custom-value";

    /// <summary>A column's custom field cannot be used or created (label, key, or field); the detail is the label.</summary>
    public const string CustomFieldInvalid = "custom-field-invalid";

    /// <summary>An unmapped column the user chose to discard; the detail is the column.</summary>
    public const string ColumnDiscarded = "column-discarded";

    /// <summary>An unmapped column of a review or General to-do table, which have no preserved metadata; the detail is the column.</summary>
    public const string ColumnNotStored = "column-not-stored";

    /// <summary>A new Lookup field has no values, or more distinct values than a Lookup takes; the detail is the label.</summary>
    public const string LookupFieldValues = "lookup-field-values";

    /// <summary>A checklist list has elements that are not text.</summary>
    public const string InvalidChecklist = "invalid-checklist";

    /// <summary>A record has a checklist both in its Markdown body and in a mapped Checklist items column.</summary>
    public const string ChecklistConflict = "checklist-conflict";

    /// <summary>A custom field an external source creates matches an existing field that is turned off; the detail is its key.</summary>
    public const string FieldInactive = "field-inactive";
}

/// <summary>ADR 0026: what an export writes or an import applies.</summary>
public enum TransferContent
{
    /// <summary>Configuration and data (default).</summary>
    Both,

    /// <summary>Lookups, custom field definitions, and business defaults; no records.</summary>
    ConfigurationOnly,

    /// <summary>Records: an import never changes configuration; an export writes only the configuration they use.</summary>
    DataOnly,
}

/// <summary>Identifies a configuration value an import removes: a lookup value, or a Lookup option of a field.</summary>
public readonly record struct ConfigurationRemovalKey(LookupKind? Kind, Guid? FieldId, Guid Id)
{
    public static ConfigurationRemovalKey ForLookup(LookupKind kind, Guid id) => new(kind, null, id);

    public static ConfigurationRemovalKey ForOption(Guid fieldId, Guid optionId) => new(null, fieldId, optionId);
}

/// <summary>ADR 0026: the import's content choice and the user's resolution of in-use removals.</summary>
/// <param name="Replacements">
/// For each in-use removed value: the value that takes over its references, or null to clear them where B-04 allows.
/// A missing key means "not chosen yet".
/// </param>
public sealed record ImportOptions(TransferContent Content, IReadOnlyDictionary<ConfigurationRemovalKey, Guid?>? Replacements = null);

/// <summary>A value a removed one can be replaced with.</summary>
public sealed record ConfigurationChoice(Guid Id, string Name);

/// <summary>An in-use value that a configuration replace removes, as the wizard's Lookups step shows it (ADR 0026).</summary>
/// <param name="FieldLabel">For a Lookup option: the label of its field; null for lookup values.</param>
/// <param name="Usage">The records (and defaults) that use the value.</param>
/// <param name="CanClear">B-04: references may be cleared instead of replaced (Designer, Developer, review Developer, squads, tags, optional fields).</param>
/// <param name="Choices">The file's values of the same kind, which can replace it.</param>
/// <param name="IsResolved">A valid choice was made; <paramref name="ChosenId"/> is it (null = clear).</param>
public sealed record ConfigurationRemoval(
    ConfigurationRemovalKey Key,
    string Name,
    string? FieldLabel,
    LookupUsageCounts Usage,
    bool CanClear,
    IReadOnlyList<ConfigurationChoice> Choices,
    bool IsResolved,
    Guid? ChosenId);

/// <summary>A lookup value a replace deletes, with the record usage the preview saw (revalidated at commit, T-10).</summary>
public sealed record RemovedLookup(LookupKind Kind, Guid Id, LookupUsageCounts RecordUsage);

/// <summary>A Lookup option a replace deletes, with the number of records the preview saw use it.</summary>
public sealed record RemovedOption(FieldOptionRemap Probe, FieldTarget Target, int RecordUsage);

/// <summary>
/// ADR 0026: how Apply replaces the configuration. The staging workflows are saved first (temporary names so renames
/// never collide, then the new values beside the removed ones), the remapped records next, the imported data after
/// them, and finally <see cref="ImportPlan.NewWorkflow"/>, which deletes the removed values.
/// </summary>
public sealed record ConfigurationReplacement
{
    public IReadOnlyList<WorkflowSnapshot> Staging { get; init; } = [];

    public IReadOnlyList<PlannedRecord<TaskItem>> Tasks { get; init; } = [];

    public IReadOnlyList<PlannedRecord<SubtaskCard>> SubtaskCards { get; init; } = [];

    public IReadOnlyList<PlannedRecord<ChangeRequest>> ChangeRequests { get; init; } = [];

    public IReadOnlyList<PlannedRecord<ReviewItem>> Reviews { get; init; } = [];

    public IReadOnlyList<RemovedLookup> RemovedLookups { get; init; } = [];

    public IReadOnlyList<RemovedOption> RemovedOptions { get; init; } = [];

    /// <summary>Records whose references the replace moves to another value or clears.</summary>
    public int RecordCount => Tasks.Count + SubtaskCards.Count + ChangeRequests.Count + Reviews.Count;
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

    /// <summary>ADR 0026: the content choice; null for sources without one (a single CSV table, external data).</summary>
    public TransferContent? Content { get; init; }

    /// <summary>The workflow the preview was made against (<see cref="ImportPlanner.WorkflowStamp"/>).</summary>
    public required string WorkflowStamp { get; init; }

    /// <summary>The complete workflow after the import's new lookups, options, and fields; null when unchanged.</summary>
    public WorkflowSnapshot? NewWorkflow { get; init; }

    /// <summary>ADR 0026: set when the import replaces the configuration (it then needs an explicit confirmation).</summary>
    public ConfigurationReplacement? Replacement { get; init; }

    /// <summary>ADR 0026: in-use values the replace removes, each resolved or still waiting for a choice.</summary>
    public IReadOnlyList<ConfigurationRemoval> Removals { get; init; } = [];

    public bool IsConfigurationReplace => Replacement is not null;

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

    /// <summary>Every record Apply writes: the imported ones, plus those that only a configuration replace remaps.</summary>
    public int TotalWriteCount
    {
        get
        {
            if (Replacement is not { } r)
            {
                return RecordWriteCount;
            }

            var tasks = Tasks.Select(t => t.Record.Id).ToHashSet();
            var cards = SubtaskCards.Select(c => c.Record.Id).ToHashSet();
            var changeRequests = ChangeRequests.Select(c => c.Record.Id).ToHashSet();
            var reviews = Reviews.Select(c => c.Record.Id).ToHashSet();
            return RecordWriteCount
                + r.Tasks.Count(t => !tasks.Contains(t.Record.Id))
                + r.SubtaskCards.Count(c => !cards.Contains(c.Record.Id))
                + r.ChangeRequests.Count(c => !changeRequests.Contains(c.Record.Id))
                + r.Reviews.Count(c => !reviews.Contains(c.Record.Id));
        }
    }

    public bool HasChanges => RecordWriteCount > 0 || NewWorkflow is not null || LedgerEntries.Count > 0;

    /// <summary>Apply is disabled while errors exist (design §4.6) and when nothing would change.</summary>
    public bool CanApply => !HasErrors && HasChanges;
}
