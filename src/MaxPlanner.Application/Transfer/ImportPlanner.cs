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
using MaxPlanner.Domain.Values;
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
public static class ImportPlanner
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

    public static ImportPlan Plan(TransferDocument document, ReadSnapshot snapshot, ImportMode mode, ImportContext context, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);
        return new Run(document, snapshot, mode, context, nowUtc).Execute();
    }

    private static bool Supplied(IReadOnlySet<string>? supplied, string member) => supplied is null || supplied.Contains(member);

    private sealed class Run(TransferDocument doc, ReadSnapshot snapshot, ImportMode mode, ImportContext context, DateTimeOffset now)
    {
        private readonly List<ImportFinding> _findings = [];
        private readonly Dictionary<string, ImportEntityCounts> _counts = [];
        private readonly Dictionary<Guid, Guid> _lookupMap = [];
        private readonly Dictionary<Guid, Guid> _fieldMap = [];
        private readonly Dictionary<Guid, Guid> _optionMap = [];
        private readonly Dictionary<Guid, Guid?> _crMap = [];
        private readonly Dictionary<Guid, long> _columnMax = [];
        private readonly List<ImportLedgerEntry> _ledger = [];
        private List<TaskStatusDefinition> _taskStatuses = [];
        private List<CrStatusDefinition> _crStatuses = [];
        private List<PriorityDefinition> _priorities = [];
        private List<SquadDefinition> _squads = [];
        private List<PersonDefinition> _people = [];
        private List<TagDefinition> _tags = [];
        private List<FieldDefinition> _fields = [];
        private WorkflowDefaults _defaults = snapshot.Workflow.Defaults;
        private bool _workflowChanged;

        private bool IsTargetEmpty => snapshot.Tasks.Count == 0 && snapshot.ChangeRequests.Count == 0 && snapshot.SubtaskCards.Count == 0
            && snapshot.ReviewItems.Count == 0 && snapshot.GeneralTodos.Count == 0;

        private string ExistsCode => context.SourceNamespace is null ? ImportFindingCodes.Exists : ImportFindingCodes.AlreadyImported;

        public ImportPlan Execute()
        {
            var stamp = WorkflowStamp(snapshot.Workflow);
            var presence = Presence();
            if (doc.TopLevelRecordCount > TransferLimits.MaxTopLevelRecords)
            {
                Add(ImportSeverity.Error, ImportEntity.File, string.Empty, ImportFindingCodes.TooManyRecords, detail: Invariant(TransferLimits.MaxTopLevelRecords));
                return new ImportPlan { Mode = mode, WorkflowStamp = stamp, Findings = _findings, Presence = presence, IsTargetEmpty = IsTargetEmpty };
            }

            MergeWorkflow();
            InitializeColumns();
            var changeRequests = PlanChangeRequests();
            var (tasks, links) = PlanTasks();
            var cards = PlanSubtaskCards(tasks);
            var reviews = PlanReviews();
            var todos = PlanGeneralTodos();
            var workflow = _workflowChanged
                ? new WorkflowSnapshot(_taskStatuses, _crStatuses, _priorities, _squads, _people, _tags, _fields, _defaults)
                : null;

            return new ImportPlan
            {
                Mode = mode,
                WorkflowStamp = stamp,
                NewWorkflow = workflow,
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

        // ----- Workflow -----
        private void MergeWorkflow()
        {
            var w = snapshot.Workflow;
            _taskStatuses = [.. w.TaskStatuses];
            _crStatuses = [.. w.CrStatuses];
            _priorities = [.. w.Priorities];
            _squads = [.. w.Squads];
            _people = [.. w.People];
            _tags = [.. w.Tags];
            _fields = [.. w.Fields];

            MergeLookups(doc.Lookups.TaskStatuses, _taskStatuses, s => s.Id.Value, s => s.Name, s => s.Order, src => src.Id, src => src.Name,
                (id, name, order, src) => new TaskStatusDefinition(new TaskStatusId(id), name, order, src.IsCompleted, src.SetsStartDate, src.IsBacklog), "TaskStatus");
            MergeLookups(doc.Lookups.CrStatuses, _crStatuses, s => s.Id.Value, s => s.Name, s => s.Order, src => src.Id, src => src.Name,
                (id, name, order, src) => new CrStatusDefinition(new CrStatusId(id), name, order, src.IsCompleted, src.IsBacklog), "CrStatus");
            MergeLookups(doc.Lookups.Priorities, _priorities, p => p.Id.Value, p => p.Name, p => p.Order, src => src.Id, src => src.Name,
                (id, name, order, _) => new PriorityDefinition(new PriorityId(id), name, order), "Priority");
            MergeLookups(doc.Lookups.Squads, _squads, s => s.Id.Value, s => s.Name, s => s.Order, src => src.Id, src => src.Name,
                (id, name, order, _) => new SquadDefinition(new SquadId(id), name, order), "Squad");
            MergeLookups(doc.Lookups.People, _people, p => p.Id.Value, p => p.Name, _ => 0, src => src.Id, src => src.Name,
                (id, name, _, _) => new PersonDefinition(new PersonId(id), name), "Person");
            MergeLookups(doc.Lookups.Tags, _tags, t => t.Id.Value, t => t.Name, _ => 0, src => src.Id, src => src.Name,
                (id, name, _, _) => new TagDefinition(new TagId(id), name), "Tag");
            MergeFields();
            MergeDefaults();
        }

        private void MergeLookups<TDef, TSrc>(
            IReadOnlyList<TSrc> sources,
            List<TDef> target,
            Func<TDef, Guid> idOf,
            Func<TDef, LookupName> nameOf,
            Func<TDef, int> orderOf,
            Func<TSrc, Guid> sourceId,
            Func<TSrc, string> sourceName,
            Func<Guid, LookupName, int, TSrc, TDef> create,
            string kind)
        {
            var nextOrder = target.Count == 0 ? 0 : target.Max(orderOf) + 1;
            foreach (var source in sources)
            {
                var id = sourceId(source);
                if (target.Any(t => idOf(t) == id))
                {
                    _lookupMap[id] = id;
                    continue;
                }

                var name = LookupName.Create(sourceName(source));
                if (!name.IsSuccess)
                {
                    Add(ImportSeverity.Error, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.Invalid, kind, name.Error!.Code);
                    continue;
                }

                if (target.FirstOrDefault(t => nameOf(t).Normalized == name.Value.Normalized) is { } byName)
                {
                    // The normalized name is the lookup's unique key (B-04), so this is the same value, not a guess.
                    _lookupMap[id] = idOf(byName);
                    Add(ImportSeverity.Info, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.LookupMapped, kind, name.Value.Display);
                    continue;
                }

                target.Add(create(id, name.Value, nextOrder++, source));
                _lookupMap[id] = id;
                _workflowChanged = true;
                Add(ImportSeverity.Info, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.LookupCreated, kind, name.Value.Display);
            }
        }

        private void MergeFields()
        {
            var nextOrder = new Dictionary<FieldTarget, int>
            {
                [FieldTarget.Task] = _fields.Where(f => f.Target == FieldTarget.Task).Select(f => f.DisplayOrder + 1).DefaultIfEmpty(0).Max(),
                [FieldTarget.ChangeRequest] = _fields.Where(f => f.Target == FieldTarget.ChangeRequest).Select(f => f.DisplayOrder + 1).DefaultIfEmpty(0).Max(),
            };
            foreach (var source in doc.FieldDefinitions)
            {
                var key = source.Id.ToString();
                if (!Enum.TryParse<FieldTarget>(source.Target, ignoreCase: false, out var target) || !Enum.TryParse<FieldKind>(source.Kind, ignoreCase: false, out var kind))
                {
                    Add(ImportSeverity.Error, ImportEntity.Field, key, ImportFindingCodes.Invalid, "Kind");
                    continue;
                }

                var fieldKey = FieldKey.Rehydrate(source.Key);
                var label = LookupName.Create(source.Label, "Label");
                if (!fieldKey.IsSuccess || !label.IsSuccess)
                {
                    Add(ImportSeverity.Error, ImportEntity.Field, key, ImportFindingCodes.Invalid, fieldKey.IsSuccess ? "Label" : "Key");
                    continue;
                }

                var existing = _fields.FirstOrDefault(f => f.Id.Value == source.Id)
                    ?? _fields.FirstOrDefault(f => f.Target == target && f.Key.Normalized == fieldKey.Value.Normalized);
                if (existing is not null)
                {
                    if (existing.Kind != kind || existing.Target != target)
                    {
                        Add(ImportSeverity.Error, ImportEntity.Field, key, ImportFindingCodes.FieldKindMismatch, "Kind", fieldKey.Value.Display);
                        continue;
                    }

                    _fieldMap[source.Id] = existing.Id.Value;
                    if (existing.Id.Value != source.Id)
                    {
                        Add(ImportSeverity.Info, ImportEntity.Field, key, ImportFindingCodes.FieldMapped, null, fieldKey.Value.Display);
                    }

                    MergeOptions(existing, source);
                    continue;
                }

                var options = new List<FieldOption>();
                foreach (var option in source.Options.OrderBy(o => o.Order))
                {
                    if (LookupName.Create(option.Label, "Options") is { IsSuccess: true } optionLabel)
                    {
                        options.Add(new FieldOption(new FieldOptionId(option.Id), optionLabel.Value, options.Count));
                        _optionMap[option.Id] = option.Id;
                    }
                }

                // B-11: a required field on a dataset that already has records needs a reviewed backfill, so it is
                // created optional there; the preview says so.
                var isRequired = source.IsRequired && IsTargetEmpty;
                if (source.IsRequired && !isRequired)
                {
                    Add(ImportSeverity.Warning, ImportEntity.Field, key, ImportFindingCodes.DefaultUsed, "IsRequired", fieldKey.Value.Display);
                }

                CustomValue? defaultValue = null;
                var probe = FieldDefinition.Create(new FieldId(source.Id), fieldKey.Value, target, label.Value, kind, false, null, 0, options);
                if (probe.IsSuccess && source.DefaultValue is { } sourceDefault)
                {
                    defaultValue = TransferValues.ToDomain(sourceDefault, kind, MapOption, MapLookup, MapLookup);
                }

                var created = FieldDefinition.Create(new FieldId(source.Id), fieldKey.Value, target, label.Value, kind, isRequired, defaultValue, nextOrder[target]++, options);
                if (!created.IsSuccess)
                {
                    Add(ImportSeverity.Error, ImportEntity.Field, key, ImportFindingCodes.Invalid, created.Error!.Field, created.Error.Code);
                    continue;
                }

                _fields.Add(source.IsActive ? created.Value : created.Value.Deactivate());
                _fieldMap[source.Id] = source.Id;
                _workflowChanged = true;
                Add(ImportSeverity.Info, ImportEntity.Field, key, ImportFindingCodes.FieldCreated, null, fieldKey.Value.Display);
            }
        }

        private void MergeOptions(FieldDefinition existing, TransferFieldDefinition source)
        {
            var options = existing.Options.ToList();
            var added = false;
            foreach (var option in source.Options)
            {
                if (options.FirstOrDefault(o => o.Id.Value == option.Id) is { } byId)
                {
                    _optionMap[option.Id] = byId.Id.Value;
                    continue;
                }

                if (LookupName.Create(option.Label, "Options") is not { IsSuccess: true } label)
                {
                    continue;
                }

                if (options.FirstOrDefault(o => o.Label.Normalized == label.Value.Normalized) is { } byLabel)
                {
                    _optionMap[option.Id] = byLabel.Id.Value;
                    continue;
                }

                if (!existing.IsActive)
                {
                    continue;
                }

                options.Add(new FieldOption(new FieldOptionId(option.Id), label.Value, options.Count));
                _optionMap[option.Id] = option.Id;
                added = true;
                Add(ImportSeverity.Info, ImportEntity.Field, source.Id.ToString(), ImportFindingCodes.OptionCreated, existing.Key.Display, label.Value.Display);
            }

            if (added && existing.Edit(existing.Label, existing.IsRequired, existing.DefaultValue, options) is { IsSuccess: true } edited)
            {
                _fields[_fields.IndexOf(existing)] = edited.Value;
                _workflowChanged = true;
            }
        }

        private void MergeDefaults()
        {
            if (doc.Defaults is not { } source)
            {
                return;
            }

            if (!IsTargetEmpty)
            {
                Add(ImportSeverity.Info, ImportEntity.File, string.Empty, ImportFindingCodes.DefaultsKept);
                return;
            }

            // Interim choice: imported business defaults replace the defaults of an
            // empty target only; a dataset in use keeps its own.
            var taskStatus = MapLookup(source.DefaultTaskStatusId);
            var crStatus = MapLookup(source.DefaultCrStatusId);
            var priority = MapLookup(source.DefaultPriorityId);
            var scheduling = MapLookup(source.CalendarSchedulingStatusId);
            var backlog = MapLookup(source.CrBacklogStatusId);
            Guid? owner = source.DefaultCrOwnerId is { } ownerId ? MapLookup(ownerId) : null;
            if (taskStatus is null || crStatus is null || priority is null || scheduling is null || backlog is null
                || (source.DefaultCrOwnerId is not null && owner is null)
                || _crStatuses.FirstOrDefault(s => s.Id.Value == backlog) is not { IsBacklog: true })
            {
                Add(ImportSeverity.Warning, ImportEntity.File, string.Empty, ImportFindingCodes.DefaultsKept);
                return;
            }

            var defaults = new WorkflowDefaults(new TaskStatusId(taskStatus.Value), new CrStatusId(crStatus.Value), new PriorityId(priority.Value),
                owner is { } o ? new PersonId(o) : null, new CrStatusId(scheduling.Value), new CrStatusId(backlog.Value));
            if (defaults != _defaults)
            {
                _defaults = defaults;
                _workflowChanged = true;
                Add(ImportSeverity.Info, ImportEntity.File, string.Empty, ImportFindingCodes.DefaultsApplied);
            }
        }

        // ----- Reference resolution -----
        private Guid? MapLookup(Guid id) => _lookupMap.TryGetValue(id, out var mapped) ? mapped : ExistsInWorkflow(id) ? id : null;

        private bool ExistsInWorkflow(Guid id) =>
            _taskStatuses.Any(s => s.Id.Value == id) || _crStatuses.Any(s => s.Id.Value == id) || _priorities.Any(p => p.Id.Value == id)
            || _squads.Any(s => s.Id.Value == id) || _people.Any(p => p.Id.Value == id) || _tags.Any(t => t.Id.Value == id);

        private Guid? MapOption(Guid id) => _optionMap.TryGetValue(id, out var mapped) ? mapped : null;

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
        /// are dropped with a warning that names them (the file format still allows an array).
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

        private void RequireCustomFields(FieldTarget target, IReadOnlyDictionary<FieldId, CustomValue> values, List<DomainError> errors) =>
            errors.AddRange(CustomFieldPolicy.Errors(target, _fields, values).Where(e => e.Code == DomainErrorCodes.Required));

        private static (DateTimeOffset Created, DateTimeOffset Updated) Audit(DateTimeOffset? created, DateTimeOffset? updated, DateTimeOffset now)
        {
            var c = created ?? updated ?? now;
            var u = updated ?? c;
            return (c, u < c ? c : u);
        }

        // ----- Board columns -----
        private void InitializeColumns()
        {
            var archived = snapshot.Tasks.Where(t => t.IsArchived).Select(t => t.Id).ToHashSet();
            foreach (var task in snapshot.Tasks.Where(t => !t.IsArchived))
            {
                Bump(task.StatusId.Value, task.Rank);
            }

            foreach (var card in snapshot.SubtaskCards.Where(c => !archived.Contains(c.ParentTaskId)))
            {
                Bump(card.StatusId.Value, card.Rank);
            }
        }

        private void Bump(Guid status, long rank) => _columnMax[status] = Math.Max(_columnMax.GetValueOrDefault(status, 0), rank);

        /// <summary>B-07: a record without a supplied rank is appended to the end of its full column.</summary>
        private long Append(Guid status)
        {
            var rank = _columnMax.GetValueOrDefault(status, 0) + RankStep;
            _columnMax[status] = rank;
            return rank;
        }

        // ----- Change Requests -----
        private List<PlannedRecord<ChangeRequest>> PlanChangeRequests()
        {
            const string Entity = ImportEntity.ChangeRequest;
            var planned = new List<PlannedRecord<ChangeRequest>>();
            var byId = snapshot.ChangeRequests.ToDictionary(c => c.Id.Value);
            var byNumber = snapshot.ChangeRequests.ToDictionary(c => c.Number.Normalized, StringComparer.Ordinal);
            var plannedNumbers = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var source in doc.ChangeRequests)
            {
                var key = source.Id.ToString();
                if (_crMap.ContainsKey(source.Id))
                {
                    Add(ImportSeverity.Error, Entity, key, ImportFindingCodes.DuplicateInSource);
                    Count(Entity, ImportAction.Error);
                    continue;
                }

                var errors = new List<DomainError>();
                var supplied = source.SuppliedFields;
                var number = Supplied(supplied, nameof(source.Number)) ? Check(CrNumber.Create(source.Number), errors) : null;
                var existing = byId.GetValueOrDefault(source.Id);
                if (existing is null && number is not null && byNumber.TryGetValue(number.Normalized, out var sameNumber))
                {
                    if (mode == ImportMode.Merge)
                    {
                        // T-10: CRs may match their normalized unique number in a reviewed merge.
                        existing = sameNumber;
                        Add(ImportSeverity.Info, Entity, key, ImportFindingCodes.MatchedByNumber, "Number", number.Display);
                    }
                    else
                    {
                        Add(ImportSeverity.Conflict, Entity, key, ImportFindingCodes.DuplicateCrNumber, "Number", number.Display);
                        Count(Entity, ImportAction.Conflict);
                        _crMap[source.Id] = null;
                        continue;
                    }
                }

                if (existing is null && context.DeletedIds.Contains(source.Id))
                {
                    Add(ImportSeverity.Conflict, Entity, key, ImportFindingCodes.Deleted);
                    Count(Entity, ImportAction.Conflict);
                    _crMap[source.Id] = null;
                    continue;
                }

                if (existing is not null && mode == ImportMode.CreateOnly)
                {
                    Add(ImportSeverity.Info, Entity, key, ExistsCode);
                    Count(Entity, ImportAction.Skip);
                    _crMap[source.Id] = existing.Id.Value;
                    continue;
                }

                var content = CrContent(source, existing, number, errors);
                var status = Supplied(supplied, nameof(source.StatusId))
                    ? Resolve(source.StatusId ?? (existing is null ? _defaults.DefaultCrStatusId.Value : null), _crStatuses, s => s.Id.Value, "StatusId", errors)
                    : _crStatuses.FirstOrDefault(s => s.Id == existing!.StatusId);
                if (status is null && errors.Count == 0)
                {
                    errors.Add(new DomainError(DomainErrorCodes.Required, "StatusId"));
                }

                var preserved = Supplied(supplied, nameof(source.Preserved))
                    ? TransferValues.ToDomain(source.Preserved) with { PreLinkTitle = null }
                    : existing?.Preserved ?? PreservedMetadata.Empty;
                if (content is not null && plannedNumbers.TryGetValue(content.Number.Normalized, out var other) && other != (existing?.Id.Value ?? source.Id))
                {
                    errors.Add(new DomainError(DomainErrorCodes.DuplicateName, "Number"));
                }

                if (content is not null && existing is not null && byNumber.TryGetValue(content.Number.Normalized, out var holder) && holder.Id != existing.Id)
                {
                    Add(ImportSeverity.Conflict, Entity, key, ImportFindingCodes.DuplicateCrNumber, "Number", content.Number.Display);
                    Count(Entity, ImportAction.Conflict);
                    _crMap[source.Id] = existing.Id.Value;
                    continue;
                }

                if (errors.Count > 0 || content is null || status is null)
                {
                    Errors(Entity, key, errors);
                    _crMap[source.Id] = existing?.Id.Value;
                    continue;
                }

                if (existing is null)
                {
                    var (created, updated) = Audit(source.CreatedAtUtc, source.UpdatedAtUtc, now);
                    var record = ChangeRequest.Rehydrate(new ChangeRequestId(source.Id), content, status.Id, preserved, created, updated, Math.Max(1, source.Revision ?? 1));
                    if (!record.IsSuccess)
                    {
                        Errors(Entity, key, [record.Error!]);
                        _crMap[source.Id] = null;
                        continue;
                    }

                    planned.Add(new PlannedRecord<ChangeRequest>(record.Value, null));
                    plannedNumbers[content.Number.Normalized] = source.Id;
                    _crMap[source.Id] = source.Id;
                    AddLedgerEntryIfExternal(source.Id);
                    Count(Entity, ImportAction.Create);
                    continue;
                }

                _crMap[source.Id] = existing.Id.Value;
                plannedNumbers[content.Number.Normalized] = existing.Id.Value;
                if (SameCr(existing, content, status.Id, preserved))
                {
                    Count(Entity, ImportAction.Skip);
                    continue;
                }

                var merged = ChangeRequest.Rehydrate(existing.Id, content, status.Id, preserved, existing.CreatedAtUtc, Later(existing.UpdatedAtUtc), existing.Revision + 1);
                if (!merged.IsSuccess)
                {
                    Errors(Entity, key, [merged.Error!]);
                    continue;
                }

                planned.Add(new PlannedRecord<ChangeRequest>(merged.Value, existing.Revision));
                Count(Entity, ImportAction.Update);
            }

            return planned;
        }

        private DateTimeOffset Later(DateTimeOffset previous) => now > previous ? now : previous;

        private ChangeRequestContent? CrContent(TransferChangeRequest source, ChangeRequest? existing, CrNumber? number, List<DomainError> errors)
        {
            var s = source.SuppliedFields;
            number ??= existing?.Number;
            if (number is null)
            {
                errors.Add(new DomainError(DomainErrorCodes.Required, "Number"));
            }

            var title = Supplied(s, nameof(source.Title)) ? Check(TitleText.Create(source.Title), errors) : existing?.Title;
            PersonId? owner;
            if (Supplied(s, nameof(source.OwnerId)) || existing is null)
            {
                var ownerId = source.OwnerId;
                if (ownerId is null && existing is null && _defaults.DefaultCrOwnerId is { } fallback)
                {
                    ownerId = fallback.Value;
                    Add(ImportSeverity.Warning, ImportEntity.ChangeRequest, source.Id.ToString(), ImportFindingCodes.DefaultUsed, "OwnerId");
                }

                if (ownerId is null)
                {
                    errors.Add(new DomainError(DomainErrorCodes.Required, "OwnerId"));
                }

                owner = Resolve(ownerId, _people, p => p.Id.Value, "OwnerId", errors)?.Id;
            }
            else
            {
                owner = existing.OwnerId;
            }

            var squad = Supplied(s, nameof(source.SquadId))
                ? Resolve(source.SquadId, _squads, q => q.Id.Value, "SquadId", errors)?.Id
                : existing?.SquadId;
            var priority = Supplied(s, nameof(source.PriorityId)) || existing is null
                ? Resolve(source.PriorityId ?? _defaults.DefaultPriorityId.Value, _priorities, p => p.Id.Value, "PriorityId", errors)?.Id
                : existing.PriorityId;
            var planned = Supplied(s, nameof(source.PlannedStart)) || Supplied(s, nameof(source.PlannedEnd))
                ? Check(PlannedRange.CreateOptional(source.PlannedStart, source.PlannedEnd), errors)
                : existing?.Planned;
            var email = Supplied(s, nameof(source.EmailSubject)) ? Check(EmailSubject.CreateOptional(source.EmailSubject), errors) : existing?.EmailSubject;
            var url = Supplied(s, nameof(source.SolutionDesignUrl)) ? Check(HttpUrl.CreateOptional(source.SolutionDesignUrl), errors) : existing?.SolutionDesignUrl;
            var description = Supplied(s, nameof(source.Description)) ? Check(LongText.CreateOptional(source.Description, "Description"), errors) : existing?.Description;
            var taskNumber = Supplied(s, nameof(source.TaskNumber)) ? Check(TaskNumber.CreateOptional(source.TaskNumber), errors) : existing?.TaskNumber;
            var custom = Supplied(s, nameof(source.CustomValues))
                ? CustomFieldPolicy.MergeForUpdate(FieldTarget.ChangeRequest, _fields, existing?.CustomValues ?? CustomValue.NoValues, CustomValues(source.CustomValues, FieldTarget.ChangeRequest, errors))
                : existing?.CustomValues ?? CustomValue.NoValues;
            RequireCustomFields(FieldTarget.ChangeRequest, custom, errors);
            return number is null || title is null || owner is null || priority is null
                ? null
                : new ChangeRequestContent(number, title, owner.Value, squad, priority.Value, planned, email, url, description, taskNumber, custom);
        }

        private static bool SameCr(ChangeRequest existing, ChangeRequestContent content, CrStatusId status, PreservedMetadata preserved) =>
            existing.Number == content.Number && existing.Title == content.Title && existing.OwnerId == content.OwnerId
            && existing.SquadId == content.SquadId && existing.PriorityId == content.PriorityId && existing.Planned == content.Planned
            && existing.EmailSubject == content.EmailSubject && existing.SolutionDesignUrl == content.SolutionDesignUrl
            && existing.Description == content.Description && existing.TaskNumber == content.TaskNumber && existing.StatusId == status
            && TransferValues.SameCustomValues(existing.CustomValues, content.CustomValues) && SamePreserved(existing.Preserved, preserved);

        private static bool SamePreserved(PreservedMetadata left, PreservedMetadata right) =>
            left.PreLinkTitle == right.PreLinkTitle && left.LegacyJson == right.LegacyJson && left.UnselectedCrReferences.SequenceEqual(right.UnselectedCrReferences);

        // ----- Tasks -----
        private (List<PlannedRecord<TaskItem>> Tasks, List<PlannedLink> Links) PlanTasks()
        {
            const string Entity = ImportEntity.Task;
            var planned = new List<PlannedRecord<TaskItem>>();
            var links = new List<PlannedLink>();
            var byId = snapshot.Tasks.ToDictionary(t => t.Id.Value);
            var crIds = snapshot.ChangeRequests.Select(c => c.Id.Value).ToHashSet();
            var linksByTask = doc.Links.GroupBy(l => l.TaskId).ToDictionary(g => g.Key, g => g.ToList());
            var seen = new HashSet<Guid>();
            var documentHasLinks = doc.Links.Count > 0 || doc.ChangeRequests.Count > 0;
            foreach (var source in doc.Tasks)
            {
                var key = source.Id.ToString();
                if (!seen.Add(source.Id))
                {
                    Add(ImportSeverity.Error, Entity, key, ImportFindingCodes.DuplicateInSource);
                    Count(Entity, ImportAction.Error);
                    continue;
                }

                var existing = byId.GetValueOrDefault(source.Id);
                if (existing is null && context.DeletedIds.Contains(source.Id))
                {
                    Add(ImportSeverity.Conflict, Entity, key, ImportFindingCodes.Deleted);
                    Count(Entity, ImportAction.Conflict);
                    continue;
                }

                if (existing is not null && mode == ImportMode.CreateOnly)
                {
                    Add(ImportSeverity.Info, Entity, key, ExistsCode);
                    Count(Entity, ImportAction.Skip);
                    continue;
                }

                // ADR 0006 / T-10: zero or one permanent CR per task.
                Guid? linkCr = null;
                var linkedAt = now;
                if (linksByTask.TryGetValue(source.Id, out var sourceLinks))
                {
                    if (sourceLinks.Select(l => l.ChangeRequestId).Distinct().Count() > 1)
                    {
                        Add(ImportSeverity.Error, Entity, key, ImportFindingCodes.MultipleCrReferences, "ChangeRequestId");
                        Count(Entity, ImportAction.Error);
                        continue;
                    }

                    var link = sourceLinks[0];
                    linkedAt = link.LinkedAtUtc ?? now;
                    if (_crMap.TryGetValue(link.ChangeRequestId, out var mapped))
                    {
                        if (mapped is null)
                        {
                            Add(ImportSeverity.Conflict, Entity, key, ImportFindingCodes.LinkTargetSkipped, "ChangeRequestId");
                            Count(Entity, ImportAction.Conflict);
                            continue;
                        }

                        linkCr = mapped;
                    }
                    else if (crIds.Contains(link.ChangeRequestId))
                    {
                        linkCr = link.ChangeRequestId;
                    }
                    else
                    {
                        Errors(Entity, key, [new DomainError(DomainErrorCodes.UnknownReference, "ChangeRequestId")]);
                        continue;
                    }
                }

                var linkExistingTask = false;
                if (existing is not null)
                {
                    if (existing.ChangeRequestId is { } current)
                    {
                        if (linkCr is { } requested && requested != current.Value)
                        {
                            Add(ImportSeverity.Conflict, Entity, key, ImportFindingCodes.LinkChangeRefused, "ChangeRequestId");
                            Count(Entity, ImportAction.Conflict);
                            continue;
                        }

                        if (linkCr is null && source.SuppliedFields is null && documentHasLinks)
                        {
                            Add(ImportSeverity.Conflict, Entity, key, ImportFindingCodes.UnlinkRefused, "ChangeRequestId");
                            Count(Entity, ImportAction.Conflict);
                            continue;
                        }

                        linkCr = null;
                    }
                    else if (linkCr is not null)
                    {
                        linkExistingTask = true;
                        Add(ImportSeverity.Warning, Entity, key, ImportFindingCodes.PermanentLink, "ChangeRequestId");
                    }
                }

                var result = BuildTask(source, existing, linkCr, linkExistingTask, key);
                if (result is null)
                {
                    continue;
                }

                if (existing is null)
                {
                    planned.Add(new PlannedRecord<TaskItem>(result, null));
                    if (linkCr is { } cr)
                    {
                        links.Add(new PlannedLink(new CrTaskLink(result.Id, new ChangeRequestId(cr), linkedAt), TaskExists: false));
                    }

                    AddLedgerEntryIfExternal(source.Id);
                    Count(Entity, ImportAction.Create);
                    continue;
                }

                if (linkExistingTask)
                {
                    links.Add(new PlannedLink(new CrTaskLink(result.Id, new ChangeRequestId(linkCr!.Value), now), TaskExists: true));
                }

                if (!linkExistingTask && SameTask(existing, result))
                {
                    Count(Entity, ImportAction.Skip);
                    continue;
                }

                planned.Add(new PlannedRecord<TaskItem>(result, existing.Revision));
                Count(Entity, ImportAction.Update);
            }

            return (planned, links);
        }

        private TaskItem? BuildTask(TransferTask source, TaskItem? existing, Guid? linkCr, bool linkExistingTask, string key)
        {
            const string Entity = ImportEntity.Task;
            var s = source.SuppliedFields;
            var errors = new List<DomainError>();
            var isLinkedResult = existing?.IsLinked == true || (existing is null && linkCr is not null);
            TitleText? ownTitle = existing?.OwnTitle;
            var preserved = Supplied(s, nameof(source.Preserved)) ? TransferValues.ToDomain(source.Preserved) : existing?.Preserved ?? PreservedMetadata.Empty;
            if (Supplied(s, nameof(source.OwnTitle)) && source.OwnTitle is not null)
            {
                if (isLinkedResult)
                {
                    // ADR 0005: a linked task's title is its CR's. A supplied title is kept as the pre-link title of a new
                    // task, never as an override (T-13).
                    if (existing is null && preserved.PreLinkTitle is null)
                    {
                        preserved = preserved with { PreLinkTitle = source.OwnTitle };
                    }

                    Add(ImportSeverity.Warning, Entity, key, ImportFindingCodes.TitlePreserved, "OwnTitle");
                }
                else
                {
                    ownTitle = Check(TitleText.Create(source.OwnTitle), errors);
                }
            }
            else if (!isLinkedResult && existing is null)
            {
                errors.Add(new DomainError(DomainErrorCodes.Required, "Title"));
            }

            if (isLinkedResult)
            {
                ownTitle = null;
            }

            var service = Supplied(s, nameof(source.Service)) ? Check(ServiceText.CreateOptional(source.Service), errors) : existing?.Service;
            var status = Supplied(s, nameof(source.StatusId)) || existing is null
                ? Resolve(source.StatusId ?? _defaults.DefaultTaskStatusId.Value, _taskStatuses, t => t.Id.Value, "StatusId", errors)
                : _taskStatuses.FirstOrDefault(t => t.Id == existing.StatusId);
            var priority = Supplied(s, nameof(source.PriorityId)) || existing is null
                ? Resolve(source.PriorityId ?? _defaults.DefaultPriorityId.Value, _priorities, p => p.Id.Value, "PriorityId", errors)
                : _priorities.FirstOrDefault(p => p.Id == existing.PriorityId);
            var designer = Supplied(s, nameof(source.DesignerIds))
                ? SinglePerson(source.DesignerIds, "DesignerIds", Entity, key, errors)
                : existing?.DesignerId;
            var developer = Supplied(s, nameof(source.DeveloperIds))
                ? SinglePerson(source.DeveloperIds, "DeveloperIds", Entity, key, errors)
                : existing?.DeveloperId;
            var tags = Supplied(s, nameof(source.TagIds))
                ? ResolveList(source.TagIds, id => _tags.Any(t => t.Id.Value == id), id => new TagId(id), "TagIds", errors)
                : existing?.TagIds.ToList() ?? [];
            var notes = Supplied(s, nameof(source.Notes)) ? Check(LongText.CreateOptional(source.Notes, "Notes"), errors) : existing?.Notes;
            var start = Supplied(s, nameof(source.StartDate)) ? source.StartDate : existing?.Dates.Start;
            var end = Supplied(s, nameof(source.EndDate)) ? source.EndDate : existing?.Dates.End;
            var archived = Supplied(s, nameof(source.IsArchived)) ? source.IsArchived ?? false : existing?.IsArchived ?? false;
            var custom = Supplied(s, nameof(source.CustomValues))
                ? CustomFieldPolicy.MergeForUpdate(FieldTarget.Task, _fields, existing?.CustomValues ?? CustomValue.NoValues, CustomValues(source.CustomValues, FieldTarget.Task, errors))
                : existing?.CustomValues ?? CustomValue.NoValues;
            RequireCustomFields(FieldTarget.Task, custom, errors);
            var checklist = Supplied(s, nameof(source.ChecklistItems)) ? Checklist(source.ChecklistItems, errors) : existing?.ChecklistItems.ToList() ?? [];
            if (errors.Count > 0 || status is null || priority is null)
            {
                Errors(Entity, key, errors.Count > 0 ? errors : [new DomainError(DomainErrorCodes.Required, "StatusId")]);
                return null;
            }

            long rank;
            if (source.Order is { } order && Supplied(s, nameof(source.Order)))
            {
                rank = order;
                if (!archived)
                {
                    Bump(status.Id.Value, order);
                }
            }
            else if (existing is not null && existing.StatusId == status.Id)
            {
                rank = existing.Rank;
            }
            else
            {
                rank = Append(status.Id.Value);
            }

            var content = new TaskContent(ownTitle, service, designer, developer, tags, notes, custom);
            DomainResult<TaskItem> built;
            if (existing is null)
            {
                var (created, updated) = Audit(source.CreatedAtUtc, source.UpdatedAtUtc, now);
                built = TaskItem.Rehydrate(new TaskId(source.Id), content, status.Id, priority.Id, start, end, archived, rank, checklist,
                    linkCr is { } cr ? new ChangeRequestId(cr) : null, preserved, created, updated, Math.Max(1, source.Revision ?? 1));
            }
            else
            {
                // An unlinked task that the import links is first merged as the unlinked task it still is, then linked
                // exactly as the editor does: its own title moves to PreLinkTitle (ADR 0005).
                built = TaskItem.Rehydrate(existing.Id, content, status.Id, priority.Id, start, end, archived, rank, checklist,
                    existing.ChangeRequestId, preserved, existing.CreatedAtUtc, Later(existing.UpdatedAtUtc), existing.Revision + 1);
                if (built.IsSuccess && linkExistingTask)
                {
                    built = built.Value.LinkTo(new ChangeRequestId(linkCr!.Value), now);
                }
            }

            if (!built.IsSuccess)
            {
                Errors(Entity, key, [built.Error!]);
                return null;
            }

            return built.Value;
        }

        private static List<ChecklistItem> Checklist(IReadOnlyList<TransferChecklistItem>? items, List<DomainError> errors)
        {
            var result = new List<ChecklistItem>();
            var ids = new HashSet<Guid>();
            foreach (var item in items ?? [])
            {
                if (!ids.Add(item.Id))
                {
                    errors.Add(new DomainError(DomainErrorCodes.InvalidStoredValue, "ChecklistItems"));
                    continue;
                }

                if (ChecklistText.Create(item.Text) is { IsSuccess: true } text)
                {
                    result.Add(new ChecklistItem(new ChecklistItemId(item.Id), text.Value, item.IsCompleted, item.Order));
                }
                else
                {
                    errors.Add(new DomainError(DomainErrorCodes.Required, "ChecklistItems"));
                }
            }

            return result;
        }

        private static bool SameTask(TaskItem existing, TaskItem merged) =>
            existing.OwnTitle == merged.OwnTitle && existing.Service == merged.Service && existing.StatusId == merged.StatusId
            && existing.PriorityId == merged.PriorityId && existing.DesignerId == merged.DesignerId
            && existing.DeveloperId == merged.DeveloperId && existing.TagIds.SequenceEqual(merged.TagIds)
            && existing.Notes == merged.Notes && existing.Dates == merged.Dates && existing.IsArchived == merged.IsArchived
            && existing.Rank == merged.Rank && existing.ChecklistItems.SequenceEqual(merged.ChecklistItems)
            && TransferValues.SameCustomValues(existing.CustomValues, merged.CustomValues) && SamePreserved(existing.Preserved, merged.Preserved);

        // ----- Subtask cards -----
        private List<PlannedRecord<SubtaskCard>> PlanSubtaskCards(List<PlannedRecord<TaskItem>> plannedTasks)
        {
            const string Entity = ImportEntity.SubtaskCard;
            var planned = new List<PlannedRecord<SubtaskCard>>();
            var byId = snapshot.SubtaskCards.ToDictionary(c => c.Id.Value);
            var parents = snapshot.Tasks.ToDictionary(t => t.Id.Value);
            foreach (var task in plannedTasks)
            {
                parents[task.Record.Id.Value] = task.Record;
            }

            var perParent = snapshot.SubtaskCards.GroupBy(c => c.ParentTaskId.Value).ToDictionary(g => g.Key, g => g.Count());
            var seen = new HashSet<Guid>();
            foreach (var source in doc.SubtaskCards)
            {
                var key = source.Id.ToString();
                if (!seen.Add(source.Id))
                {
                    Add(ImportSeverity.Error, Entity, key, ImportFindingCodes.DuplicateInSource);
                    Count(Entity, ImportAction.Error);
                    continue;
                }

                var existing = byId.GetValueOrDefault(source.Id);
                if (existing is null && context.DeletedIds.Contains(source.Id))
                {
                    Add(ImportSeverity.Conflict, Entity, key, ImportFindingCodes.Deleted);
                    Count(Entity, ImportAction.Conflict);
                    continue;
                }

                if (existing is not null && mode == ImportMode.CreateOnly)
                {
                    Add(ImportSeverity.Info, Entity, key, ExistsCode);
                    Count(Entity, ImportAction.Skip);
                    continue;
                }

                // B-16.1: a card belongs to exactly one existing or imported parent; it never moves to another parent.
                var parentId = existing?.ParentTaskId.Value ?? source.ParentTaskId;
                if (!parents.TryGetValue(parentId, out var parent))
                {
                    Add(ImportSeverity.Error, Entity, key, ImportFindingCodes.MissingParent, "ParentTaskId");
                    Count(Entity, ImportAction.Error);
                    continue;
                }

                var s = source.SuppliedFields;
                var errors = new List<DomainError>();
                var subtitle = Supplied(s, nameof(source.Subtitle)) || existing is null ? Check(SubtitleText.Create(source.Subtitle), errors) : existing.Subtitle;
                var status = Supplied(s, nameof(source.StatusId)) || existing is null
                    ? Resolve(source.StatusId ?? _defaults.DefaultTaskStatusId.Value, _taskStatuses, t => t.Id.Value, "StatusId", errors)
                    : _taskStatuses.FirstOrDefault(t => t.Id == existing.StatusId);
                var notes = Supplied(s, nameof(source.Notes)) ? Check(LongText.CreateOptional(source.Notes, "Notes"), errors) : existing?.Notes;
                var start = Supplied(s, nameof(source.StartDate)) ? source.StartDate : existing?.Dates.Start;
                var end = Supplied(s, nameof(source.EndDate)) ? source.EndDate : existing?.Dates.End;
                if (existing is null && perParent.GetValueOrDefault(parentId) >= DomainLimits.SubtaskCardsPerTask)
                {
                    errors.Add(new DomainError(DomainErrorCodes.TooManyItems, "SubtaskCards", DomainLimits.SubtaskCardsPerTask));
                }

                if (errors.Count > 0 || subtitle is null || status is null)
                {
                    Errors(Entity, key, errors.Count > 0 ? errors : [new DomainError(DomainErrorCodes.Required, "StatusId")]);
                    continue;
                }

                long rank;
                if (source.Order is { } order && Supplied(s, nameof(source.Order)))
                {
                    rank = order;
                    if (!parent.IsArchived)
                    {
                        Bump(status.Id.Value, order);
                    }
                }
                else
                {
                    rank = existing is not null && existing.StatusId == status.Id ? existing.Rank : Append(status.Id.Value);
                }

                DomainResult<SubtaskCard> built;
                if (existing is null)
                {
                    var (created, updated) = Audit(source.CreatedAtUtc, source.UpdatedAtUtc, now);
                    built = SubtaskCard.Rehydrate(new SubtaskCardId(source.Id), parent.Id, subtitle, status.Id, start, end, notes, rank, created, updated, Math.Max(1, source.Revision ?? 1));
                }
                else
                {
                    built = SubtaskCard.Rehydrate(existing.Id, existing.ParentTaskId, subtitle, status.Id, start, end, notes, rank, existing.CreatedAtUtc, Later(existing.UpdatedAtUtc), existing.Revision + 1);
                }

                if (!built.IsSuccess)
                {
                    Errors(Entity, key, [built.Error!]);
                    continue;
                }

                if (existing is null)
                {
                    perParent[parentId] = perParent.GetValueOrDefault(parentId) + 1;
                    planned.Add(new PlannedRecord<SubtaskCard>(built.Value, null));
                    AddLedgerEntryIfExternal(source.Id);
                    Count(Entity, ImportAction.Create);
                }
                else if (existing.Subtitle == built.Value.Subtitle && existing.StatusId == built.Value.StatusId && existing.Dates == built.Value.Dates
                         && existing.Notes == built.Value.Notes && existing.Rank == built.Value.Rank)
                {
                    Count(Entity, ImportAction.Skip);
                }
                else
                {
                    planned.Add(new PlannedRecord<SubtaskCard>(built.Value, existing.Revision));
                    Count(Entity, ImportAction.Update);
                }
            }

            return planned;
        }

        // ----- Reviews -----
        private List<PlannedRecord<ReviewItem>> PlanReviews()
        {
            const string Entity = ImportEntity.Review;
            var planned = new List<PlannedRecord<ReviewItem>>();
            var byId = snapshot.ReviewItems.ToDictionary(r => r.Id.Value);
            var nextRank = snapshot.ReviewItems.Select(r => r.Rank).DefaultIfEmpty(0).Max();
            var seen = new HashSet<Guid>();
            foreach (var source in doc.Reviews)
            {
                var key = source.Id.ToString();
                if (!seen.Add(source.Id))
                {
                    Add(ImportSeverity.Error, Entity, key, ImportFindingCodes.DuplicateInSource);
                    Count(Entity, ImportAction.Error);
                    continue;
                }

                var existing = byId.GetValueOrDefault(source.Id);
                if (existing is null && context.DeletedIds.Contains(source.Id))
                {
                    Add(ImportSeverity.Conflict, Entity, key, ImportFindingCodes.Deleted);
                    Count(Entity, ImportAction.Conflict);
                    continue;
                }

                if (existing is not null && mode == ImportMode.CreateOnly)
                {
                    Add(ImportSeverity.Info, Entity, key, ExistsCode);
                    Count(Entity, ImportAction.Skip);
                    continue;
                }

                var s = source.SuppliedFields;
                var errors = new List<DomainError>();
                var current = existing?.Content;
                var title = Supplied(s, nameof(source.Title)) || current is null ? Check(TitleText.Create(source.Title), errors) : current.Title;
                var crNumber = Supplied(s, nameof(source.CrNumberText)) ? Check(TaskNumber.CreateOptional(source.CrNumberText, "CrNumberText"), errors) : current?.CrNumberText;
                var taskNumber = Supplied(s, nameof(source.TaskNumberText)) ? Check(TaskNumber.CreateOptional(source.TaskNumberText, "TaskNumberText"), errors) : current?.TaskNumberText;
                var services = new List<ServiceText>();
                if (Supplied(s, nameof(source.Services)))
                {
                    foreach (var raw in source.Services ?? [])
                    {
                        if (Check(ServiceText.CreateOptional(raw, "Services"), errors) is { } service
                            && !services.Any(x => string.Equals(x.Value, service.Value, StringComparison.OrdinalIgnoreCase)))
                        {
                            services.Add(service);
                        }
                    }
                }
                else
                {
                    services.AddRange(current?.Services ?? []);
                }

                var developer = Supplied(s, nameof(source.DeveloperId))
                    ? Resolve(source.DeveloperId, _people, p => p.Id.Value, "DeveloperId", errors)?.Id
                    : current?.DeveloperId;
                var comments = Supplied(s, nameof(source.Comments)) ? Check(LongText.CreateOptional(source.Comments, "Comments"), errors) : current?.Comments;
                var isDone = Supplied(s, nameof(source.IsDone)) ? source.IsDone ?? false : existing?.IsDone ?? false;
                var completedOn = Supplied(s, nameof(source.CompletedOn)) ? source.CompletedOn : existing?.CompletedOn;
                if (isDone && completedOn is null)
                {
                    // B-04: imports never stamp today's date; a done review without its completion day is invalid.
                    errors.Add(new DomainError(DomainErrorCodes.Required, "CompletedOn"));
                }

                if (!isDone)
                {
                    completedOn = null;
                }

                if (errors.Count > 0 || title is null)
                {
                    Errors(Entity, key, errors);
                    continue;
                }

                var content = new ReviewContent(title, crNumber, taskNumber, services, developer, comments);
                var rank = Supplied(s, nameof(source.Order)) && source.Order is { } order ? order : existing?.Rank ?? (nextRank += RankStep);
                var (created, updated) = existing is null ? Audit(source.CreatedAtUtc, source.UpdatedAtUtc, now) : (existing.CreatedAtUtc, Later(existing.UpdatedAtUtc));
                var built = ReviewItem.Rehydrate(new ReviewItemId(source.Id), content, isDone, completedOn, rank, created, updated,
                    existing is null ? Math.Max(1, source.Revision ?? 1) : existing.Revision + 1);
                if (!built.IsSuccess)
                {
                    Errors(Entity, key, [built.Error!]);
                    continue;
                }

                if (existing is null)
                {
                    planned.Add(new PlannedRecord<ReviewItem>(built.Value, null));
                    AddLedgerEntryIfExternal(source.Id);
                    Count(Entity, ImportAction.Create);
                }
                else if (existing.Content.Title == content.Title && existing.Content.CrNumberText == content.CrNumberText
                         && existing.Content.TaskNumberText == content.TaskNumberText && existing.Content.Services.SequenceEqual(content.Services)
                         && existing.Content.DeveloperId == content.DeveloperId && existing.Content.Comments == content.Comments
                         && existing.IsDone == isDone && existing.CompletedOn == completedOn && existing.Rank == rank)
                {
                    Count(Entity, ImportAction.Skip);
                }
                else
                {
                    planned.Add(new PlannedRecord<ReviewItem>(built.Value, existing.Revision));
                    Count(Entity, ImportAction.Update);
                }
            }

            return planned;
        }

        // ----- General to-dos -----
        private List<PlannedRecord<GeneralTodo>> PlanGeneralTodos()
        {
            const string Entity = ImportEntity.GeneralTodo;
            var planned = new List<PlannedRecord<GeneralTodo>>();
            var byId = snapshot.GeneralTodos.ToDictionary(t => t.Id.Value);
            var nextRank = snapshot.GeneralTodos.Select(t => t.Rank).DefaultIfEmpty(0).Max();
            var seen = new HashSet<Guid>();
            foreach (var source in doc.GeneralTodos)
            {
                var key = source.Id.ToString();
                if (!seen.Add(source.Id))
                {
                    Add(ImportSeverity.Error, Entity, key, ImportFindingCodes.DuplicateInSource);
                    Count(Entity, ImportAction.Error);
                    continue;
                }

                var existing = byId.GetValueOrDefault(source.Id);
                if (existing is null && context.DeletedIds.Contains(source.Id))
                {
                    Add(ImportSeverity.Conflict, Entity, key, ImportFindingCodes.Deleted);
                    Count(Entity, ImportAction.Conflict);
                    continue;
                }

                if (existing is not null && mode == ImportMode.CreateOnly)
                {
                    Add(ImportSeverity.Info, Entity, key, ExistsCode);
                    Count(Entity, ImportAction.Skip);
                    continue;
                }

                var s = source.SuppliedFields;
                var errors = new List<DomainError>();
                var text = Supplied(s, nameof(source.Text)) || existing is null ? Check(ChecklistText.Create(source.Text), errors) : existing.Text;
                if (errors.Count > 0 || text is null)
                {
                    Errors(Entity, key, errors);
                    continue;
                }

                var isCompleted = Supplied(s, nameof(source.IsCompleted)) ? source.IsCompleted ?? false : existing?.IsCompleted ?? false;
                var rank = Supplied(s, nameof(source.Order)) && source.Order is { } order ? order : existing?.Rank ?? (nextRank += RankStep);
                var (created, updated) = existing is null ? Audit(source.CreatedAtUtc, source.UpdatedAtUtc, now) : (existing.CreatedAtUtc, Later(existing.UpdatedAtUtc));
                var built = GeneralTodo.Rehydrate(new GeneralTodoId(source.Id), text, isCompleted, rank, created, updated,
                    existing is null ? Math.Max(1, source.Revision ?? 1) : existing.Revision + 1);
                if (!built.IsSuccess)
                {
                    Errors(Entity, key, [built.Error!]);
                    continue;
                }

                if (existing is null)
                {
                    planned.Add(new PlannedRecord<GeneralTodo>(built.Value, null));
                    AddLedgerEntryIfExternal(source.Id);
                    Count(Entity, ImportAction.Create);
                }
                else if (existing.Text == text && existing.IsCompleted == isCompleted && existing.Rank == rank)
                {
                    Count(Entity, ImportAction.Skip);
                }
                else
                {
                    planned.Add(new PlannedRecord<GeneralTodo>(built.Value, existing.Revision));
                    Count(Entity, ImportAction.Update);
                }
            }

            return planned;
        }
    }
}
