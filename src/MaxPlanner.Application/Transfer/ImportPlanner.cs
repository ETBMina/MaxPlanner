using System.Text;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Transfer;

/// <summary>What an import preview needs besides the document and the read snapshot.</summary>
/// <param name="DeletedIds">IDs of the document that tombstones mark as deleted (B-05: never re-created).</param>
/// <param name="SourceNamespace">The B-13.5 namespace of an external import; null for the app's own formats.</param>
/// <param name="SourceKeys">For external records: the ledger identity of each document record ID.</param>
public sealed record ImportContext(
    IReadOnlySet<Guid> DeletedIds,
    string? SourceNamespace = null,
    IReadOnlyDictionary<Guid, (string EntityKind, string SourceKey)>? SourceKeys = null)
{
    public static ImportContext Empty { get; } = new(new HashSet<Guid>());
}

/// <summary>
/// Turns a transfer document into a reviewed <see cref="ImportPlan"/> against one read snapshot (T-10, B-12, B-13).
/// Pure: it reads nothing else and writes nothing, so a preview never changes data.
/// </summary>
/// <remarks>
/// <para>Rules: create-only skips records that exist; merge updates them with the supplied fields only (absent means
/// unchanged, explicit null clears, a supplied collection replaces). Missing records never mean delete. Lookups and
/// fields match by ID, then by their unique normalized name or key, else they are proposed as new definitions.</para>
/// <para>ADR 0006: an import may link a currently unlinked task once; it never removes or changes an existing link
/// (reported as a conflict, and that task row is not applied). Historical dates are kept; B-04 automation never runs.</para>
/// </remarks>
public static partial class ImportPlanner
{
    private const long RankStep = 1024;

    /// <summary>The workflow an import depends on. Apply refuses the plan when it differs at commit.</summary>
    public static string WorkflowStamp(WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var builder = new StringBuilder();
        foreach (var kind in Enum.GetValues<LookupKind>())
        {
            builder.Append(WorkflowStamps.Order(workflow, kind)).Append('#');
            foreach (var entry in WorkflowLookups.Entries(workflow, kind))
            {
                builder.Append(WorkflowStamps.Item(workflow, kind, entry.Id));
            }
        }

        foreach (var field in workflow.Fields.OrderBy(f => f.Id.Value))
        {
            builder.Append(WorkflowStamps.Field(workflow, field.Id));
        }

        return builder.Append(WorkflowStamps.Defaults(workflow)).ToString();
    }

    /// <param name="options">
    /// ADR 0026: the content to import from a Max Planner file with definitions, and the replacements chosen for in-use
    /// configuration it removes. Null for sources without that choice (a single CSV table, external data), which add
    /// missing definitions and never replace configuration.
    /// </param>
    public static ImportPlan Plan(TransferDocument document, ReadSnapshot snapshot, ImportMode mode, ImportContext context, DateTimeOffset nowUtc, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);
        return new Run(document, snapshot, mode, context, options, nowUtc).Execute();
    }

    private const string CustomValuePrefix = "CustomValues:";

    /// <summary>
    /// ADR 0027: the supplied-field member of one custom field. A partial source (an external table) that supplies it,
    /// rather than <c>CustomValues</c> as a whole, changes only that field of an existing record; an absent value clears it.
    /// </summary>
    public static string CustomValueMember(Guid fieldId) => CustomValuePrefix + fieldId.ToString("D");

    private static bool Supplied(IReadOnlySet<string>? supplied, string member) => supplied is null || supplied.Contains(member);

    private sealed partial class Run(TransferDocument doc, ReadSnapshot snapshot, ImportMode mode, ImportContext context, ImportOptions? options, DateTimeOffset now)
    {
        private readonly List<ImportFinding> _findings = [];
        private readonly Dictionary<string, ImportEntityCounts> _counts = [];
        private readonly Dictionary<Guid, Guid> _lookupMap = [];
        private readonly Dictionary<Guid, Guid> _fieldMap = [];
        private readonly Dictionary<Guid, Guid> _optionMap = [];
        private readonly Dictionary<Guid, Guid?> _crMap = [];
        private readonly Dictionary<Guid, long> _bucketMax = [];
        private readonly List<ImportLedgerEntry> _ledger = [];
        private List<TaskStatusDefinition> _taskStatuses = [];
        private List<CrStatusDefinition> _crStatuses = [];
        private List<PriorityDefinition> _priorities = [];
        private List<SquadDefinition> _squads = [];
        private List<PersonDefinition> _people = [];
        private List<TagDefinition> _tags = [];
        private List<FieldDefinition> _fields = [];
        private WorkflowDefaults _defaults = snapshot.Workflow.Defaults;
        private ReadSnapshot _snapshot = snapshot;
        private bool _workflowChanged;

        private bool IsTargetEmpty => _snapshot.Tasks.Count == 0 && _snapshot.ChangeRequests.Count == 0 && _snapshot.SubtaskCards.Count == 0
            && _snapshot.ReviewItems.Count == 0 && _snapshot.GeneralTodos.Count == 0;

        private string ExistsCode => context.SourceNamespace is null ? ImportFindingCodes.Exists : ImportFindingCodes.AlreadyImported;

        public ImportPlan Execute()
        {
            var stamp = WorkflowStamp(_snapshot.Workflow);
            var presence = Presence();
            if (doc.TopLevelRecordCount > TransferLimits.MaxTopLevelRecords)
            {
                Add(ImportSeverity.Error, ImportEntity.File, string.Empty, ImportFindingCodes.TooManyRecords, detail: Invariant(TransferLimits.MaxTopLevelRecords));
                return new ImportPlan { Mode = mode, WorkflowStamp = stamp, Findings = _findings, Presence = presence, IsTargetEmpty = IsTargetEmpty };
            }

            switch (options?.Content)
            {
                case null:
                    MergeWorkflow();
                    break;
                case TransferContent.DataOnly:
                    MatchWorkflowForData();
                    break;
                default:
                    ReplaceWorkflow();
                    break;
            }

            InitializeBuckets();
            List<PlannedRecord<ChangeRequest>> changeRequests = [];
            List<PlannedRecord<TaskItem>> tasks = [];
            List<PlannedLink> links = [];
            List<PlannedRecord<SubtaskCard>> cards = [];
            List<PlannedRecord<ReviewItem>> reviews = [];
            List<PlannedRecord<GeneralTodo>> todos = [];
            if (options?.Content == TransferContent.ConfigurationOnly)
            {
                if (doc.TopLevelRecordCount > 0)
                {
                    Add(ImportSeverity.Info, ImportEntity.File, string.Empty, ImportFindingCodes.RecordsIgnored, detail: Invariant(doc.TopLevelRecordCount));
                }
            }
            else
            {
                changeRequests = PlanChangeRequests();
                (tasks, links) = PlanTasks();
                cards = PlanSubtaskCards(tasks);
                reviews = PlanReviews();
                todos = PlanGeneralTodos();
            }

            var workflow = _workflowChanged
                ? new WorkflowSnapshot(_taskStatuses, _crStatuses, _priorities, _squads, _people, _tags, _fields, _defaults)
                : null;

            return new ImportPlan
            {
                Mode = mode,
                Content = options?.Content,
                WorkflowStamp = stamp,
                NewWorkflow = workflow,
                Replacement = _replacement,
                Removals = _removals,
                ChangeRequests = changeRequests,
                Tasks = tasks,
                Links = links,
                SubtaskCards = cards,
                Reviews = reviews,
                GeneralTodos = todos,
                LedgerEntries = _ledger,
                Findings = _findings,
                Counts = _counts,
                Presence = presence,
                IsTargetEmpty = IsTargetEmpty,
            };
        }

        private static string Invariant(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private ImportPresence Presence() => new(
            doc.Tasks.Count > 0,
            doc.Tasks.Any(t => t.ChecklistItems is { Count: > 0 }),
            doc.SubtaskCards.Count > 0,
            doc.ChangeRequests.Count > 0,
            doc.Reviews.Count > 0,
            doc.GeneralTodos.Count > 0,
            doc.Tasks.Any(t => t.StartDate is not null || t.EndDate is not null) || doc.ChangeRequests.Any(c => c.PlannedStart is not null),
            doc.Tasks.Any(t => !string.IsNullOrEmpty(t.Notes)) || doc.ChangeRequests.Any(c => !string.IsNullOrEmpty(c.Description)));

        // ----- Findings and counts -----
        private void Add(ImportSeverity severity, string entity, string key, string code, string? field = null, string? detail = null) =>
            _findings.Add(new ImportFinding(severity, entity, key, code, field, detail));

        private void Count(string entity, ImportAction action) =>
            _counts[entity] = _counts.GetValueOrDefault(entity, ImportEntityCounts.Zero).Add(action);

        private void Errors(string entity, string key, List<DomainError> errors)
        {
            foreach (var error in errors)
            {
                Add(ImportSeverity.Error, entity, key, error.Code == DomainErrorCodes.UnknownReference ? ImportFindingCodes.UnknownReference : ImportFindingCodes.Invalid, error.Field, error.Code);
            }

            Count(entity, ImportAction.Error);
        }

        private static T? Check<T>(DomainResult<T> result, List<DomainError> errors)
        {
            if (result.IsSuccess)
            {
                return result.Value;
            }

            errors.Add(result.Error!);
            return default;
        }

        private void AddLedgerEntryIfExternal(Guid id)
        {
            if (context.SourceNamespace is { } ns && context.SourceKeys is { } keys && keys.TryGetValue(id, out var source))
            {
                _ledger.Add(new ImportLedgerEntry(ns, source.EntityKind, source.SourceKey, id));
            }
        }

        // ----- Reference resolution -----
        private Guid? MapLookup(Guid id) => _lookupMap.TryGetValue(id, out var mapped) ? mapped : ExistsInWorkflow(id) ? id : null;

        private bool ExistsInWorkflow(Guid id) =>
            _taskStatuses.Any(s => s.Id.Value == id) || _crStatuses.Any(s => s.Id.Value == id) || _priorities.Any(p => p.Id.Value == id)
            || _squads.Any(s => s.Id.Value == id) || _people.Any(p => p.Id.Value == id) || _tags.Any(t => t.Id.Value == id);

        /// <summary>A file option's target option; an external source names the options of existing fields directly (ADR 0027).</summary>
        private Guid? MapOption(Guid id) =>
            _optionMap.TryGetValue(id, out var mapped) ? mapped
            : context.SourceNamespace is not null && _fields.Any(f => f.Options.Any(o => o.Id.Value == id)) ? id
            : null;

        private T? Resolve<T>(Guid? id, IEnumerable<T> definitions, Func<T, Guid> idOf, string field, List<DomainError> errors)
            where T : class
        {
            if (id is not { } value)
            {
                return null;
            }

            var mapped = MapLookup(value);
            var definition = mapped is { } m ? definitions.FirstOrDefault(d => idOf(d) == m) : null;
            if (definition is null)
            {
                errors.Add(new DomainError(DomainErrorCodes.UnknownReference, field));
            }

            return definition;
        }

        /// <summary>
        /// ADR 0024: a task has at most one Designer and one Developer. The first resolved person is kept; any others
        /// are dropped with a warning that names them (format version 1 wrote an array, ADR 0028).
        /// </summary>
        private PersonId? SinglePerson(IReadOnlyList<Guid>? ids, string field, string entity, string key, List<DomainError> errors)
        {
            var people = ResolveList(ids, id => _people.Any(p => p.Id.Value == id), id => new PersonId(id), field, errors);
            if (people.Count > 1)
            {
                var dropped = people.Skip(1).Select(p => _people.First(d => d.Id == p).Name.Display);
                Add(ImportSeverity.Warning, entity, key, ImportFindingCodes.PeopleDropped, field, string.Join(", ", dropped));
            }

            return people.Count > 0 ? people[0] : null;
        }

        private List<TId> ResolveList<TId>(IReadOnlyList<Guid>? ids, Func<Guid, bool> exists, Func<Guid, TId> wrap, string field, List<DomainError> errors)
        {
            var result = new List<TId>();
            foreach (var id in ids ?? [])
            {
                if (MapLookup(id) is { } mapped && exists(mapped))
                {
                    var wrapped = wrap(mapped);
                    if (!result.Contains(wrapped))
                    {
                        result.Add(wrapped);
                    }
                }
                else
                {
                    errors.Add(new DomainError(DomainErrorCodes.UnknownReference, field));
                }
            }

            return result;
        }

        private System.Collections.ObjectModel.ReadOnlyDictionary<FieldId, CustomValue> CustomValues(IReadOnlyDictionary<string, TransferValue>? source, FieldTarget target, List<DomainError> errors)
        {
            var values = new Dictionary<FieldId, CustomValue>();
            foreach (var (fieldKey, value) in source ?? new Dictionary<string, TransferValue>())
            {
                var definition = Guid.TryParse(fieldKey, out var fileId) && (_fieldMap.TryGetValue(fileId, out var mapped) ? mapped : fileId) is var targetId
                    ? _fields.FirstOrDefault(f => f.Id.Value == targetId && f.Target == target)
                    : null;
                if (definition is null)
                {
                    errors.Add(new DomainError(DomainErrorCodes.UnknownField, "Custom"));
                    continue;
                }

                var domain = TransferValues.ToDomain(value, definition.Kind, MapOption, MapLookup, MapLookup);
                if (domain is null)
                {
                    errors.Add(new DomainError(DomainErrorCodes.CustomValueKindMismatch, definition.ErrorField));
                    continue;
                }

                if (definition.IsActive && CustomFieldPolicy.ValidateValue(definition, domain) is { } invalid)
                {
                    errors.Add(invalid);
                    continue;
                }

                values[definition.Id] = domain;
            }

            return values.AsReadOnly();
        }

        /// <summary>
        /// The custom values a record gets: all supplied values (values of inactive fields are kept, B-11), only the
        /// fields supplied one by one (<see cref="CustomValueMember"/>, ADR 0027), or the stored values unchanged.
        /// </summary>
        private IReadOnlyDictionary<FieldId, CustomValue> PlannedCustomValues(
            IReadOnlySet<string>? supplied,
            IReadOnlyDictionary<string, TransferValue>? source,
            IReadOnlyDictionary<FieldId, CustomValue>? stored,
            FieldTarget target,
            List<DomainError> errors)
        {
            stored ??= CustomValue.NoValues;
            if (Supplied(supplied, nameof(TransferTask.CustomValues)))
            {
                return CustomFieldPolicy.MergeForUpdate(target, _fields, stored, CustomValues(source, target, errors));
            }

            var fields = supplied!.Where(m => m.StartsWith(CustomValuePrefix, StringComparison.Ordinal))
                .Select(m => Guid.TryParse(m.AsSpan(CustomValuePrefix.Length), out var id) ? new FieldId(_fieldMap.TryGetValue(id, out var mapped) ? mapped : id) : (FieldId?)null)
                .OfType<FieldId>().ToList();
            if (fields.Count == 0)
            {
                return stored;
            }

            var merged = stored.Where(v => !fields.Contains(v.Key)).ToDictionary();
            foreach (var (id, value) in CustomValues(source, target, errors))
            {
                merged[id] = value;
            }

            return merged.AsReadOnly();
        }

        private void RequireCustomFields(FieldTarget target, IReadOnlyDictionary<FieldId, CustomValue> values, List<DomainError> errors) =>
            errors.AddRange(CustomFieldPolicy.Errors(target, _fields, values).Where(e => e.Code == DomainErrorCodes.Required));

        private static (DateTimeOffset Created, DateTimeOffset Updated) Audit(DateTimeOffset? created, DateTimeOffset? updated, DateTimeOffset now)
        {
            var c = created ?? updated ?? now;
            var u = updated ?? c;
            return (c, u < c ? c : u);
        }

        // ----- Board buckets -----
        private void InitializeBuckets()
        {
            var archived = _snapshot.Tasks.Where(t => t.IsArchived).Select(t => t.Id).ToHashSet();
            foreach (var task in _snapshot.Tasks.Where(t => !t.IsArchived))
            {
                Bump(task.StatusId.Value, task.Rank);
            }

            foreach (var card in _snapshot.SubtaskCards.Where(c => !archived.Contains(c.ParentTaskId)))
            {
                Bump(card.StatusId.Value, card.Rank);
            }
        }

        private void Bump(Guid status, long rank) => _bucketMax[status] = Math.Max(_bucketMax.GetValueOrDefault(status, 0), rank);

        /// <summary>B-07: a record without a supplied rank is appended to the end of its full bucket.</summary>
        private long Append(Guid status)
        {
            var rank = _bucketMax.GetValueOrDefault(status, 0) + RankStep;
            _bucketMax[status] = rank;
            return rank;
        }
    }
}
