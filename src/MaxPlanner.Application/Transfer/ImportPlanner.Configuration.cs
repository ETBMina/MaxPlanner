using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Transfer;

// ADR 0026: "Data only" matches the configuration the records use without changing it; "Configuration only" and
// "Both" replace the current configuration with the file's, remapping in-use removed values per the B-04 rules.
public static partial class ImportPlanner
{
    /// <summary>
    /// ADR 0026: whether a file holds a complete configuration, which alone may replace one: an entire-dataset export
    /// (not a Current view) with business defaults. A view or data-only export holds only what its records use.
    /// </summary>
    public static bool HasCompleteConfiguration(TransferDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Scope?.Kind != TransferFormat.ScopeCurrentView && document.Defaults is not null;
    }

    private sealed partial class Run
    {
        private readonly List<ConfigurationRemoval> _removals = [];
        private readonly List<LookupRemap> _lookupRemaps = [];
        private readonly List<FieldOptionRemap> _optionRemaps = [];
        private readonly List<(LookupKind Kind, Guid Id, LookupName Name)> _removedLookupValues = [];
        private readonly List<(FieldId Field, FieldOption Option)> _removedOptionValues = [];
        private ConfigurationReplacement? _replacement;

        private readonly HashSet<FieldId> _untouchedFields = [];

        private (bool HasChoice, Guid? Chosen) Choice(ConfigurationRemovalKey key) =>
            options?.Replacements is { } chosen && chosen.TryGetValue(key, out var value) ? (true, value) : (false, null);

        private bool HasErrorFindings => _findings.Any(f => f.Severity == ImportSeverity.Error);

        // ----- Data only -----
        private void MatchWorkflowForData()
        {
            var w = _snapshot.Workflow;
            _taskStatuses = [.. w.TaskStatuses];
            _crStatuses = [.. w.CrStatuses];
            _priorities = [.. w.Priorities];
            _squads = [.. w.Squads];
            _people = [.. w.People];
            _tags = [.. w.Tags];
            _fields = [.. w.Fields];

            var references = TransferReferences.Of(doc);
            var l = doc.Lookups;
            MatchLookups(LookupKind.TaskStatus, l.TaskStatuses.Select(s => (s.Id, s.Name)), references);
            MatchLookups(LookupKind.CrStatus, l.CrStatuses.Select(s => (s.Id, s.Name)), references);
            MatchLookups(LookupKind.Priority, l.Priorities.Select(p => (p.Id, p.Name)), references);
            MatchLookups(LookupKind.Squad, l.Squads.Select(s => (s.Id, s.Name)), references);
            MatchLookups(LookupKind.Person, l.People.Select(p => (p.Id, p.Name)), references);
            MatchLookups(LookupKind.Tag, l.Tags.Select(t => (t.Id, t.Name)), references);
            MatchFields(references);
            if (doc.Defaults is not null)
            {
                Add(ImportSeverity.Info, ImportEntity.File, string.Empty, ImportFindingCodes.DefaultsKept);
            }
        }

        private void MatchLookups(LookupKind kind, IEnumerable<(Guid Id, string Name)> sources, TransferReferences references)
        {
            var entries = WorkflowLookups.Entries(_snapshot.Workflow, kind);
            foreach (var (id, rawName) in sources)
            {
                if (entries.Any(e => e.Id == id))
                {
                    _lookupMap[id] = id;
                    continue;
                }

                var name = LookupName.Create(rawName);
                if (name.IsSuccess && entries.FirstOrDefault(e => e.Name.Normalized == name.Value.Normalized) is { } byName)
                {
                    _lookupMap[id] = byName.Id;
                    Add(ImportSeverity.Info, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.LookupMapped, kind.ToString(), name.Value.Display);
                    continue;
                }

                // Only a value the records use must exist; the rest of a full export's configuration is ignored.
                if (references.Lookups.Contains(id))
                {
                    Add(ImportSeverity.Error, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.ConfigurationMissing, kind.ToString(), rawName);
                }
            }
        }

        private void MatchFields(TransferReferences references)
        {
            foreach (var source in doc.FieldDefinitions)
            {
                var key = source.Id.ToString();
                var isUsed = references.Fields.Contains(source.Id);
                var fieldKey = FieldKey.Rehydrate(source.Key);
                var parsed = Enum.TryParse<FieldTarget>(source.Target, ignoreCase: false, out var target) & Enum.TryParse<FieldKind>(source.Kind, ignoreCase: false, out var kind);
                var existing = _fields.FirstOrDefault(f => f.Id.Value == source.Id)
                    ?? (parsed && fieldKey.IsSuccess ? _fields.FirstOrDefault(f => f.Target == target && f.Key.Normalized == fieldKey.Value.Normalized) : null);
                if (existing is null || !parsed || existing.Kind != kind || existing.Target != target)
                {
                    if (isUsed)
                    {
                        var code = existing is null ? ImportFindingCodes.ConfigurationMissing : ImportFindingCodes.FieldKindMismatch;
                        Add(ImportSeverity.Error, ImportEntity.Field, key, code, null, source.Key);
                    }

                    continue;
                }

                _fieldMap[source.Id] = existing.Id.Value;
                foreach (var option in source.Options)
                {
                    var label = LookupName.Create(option.Label, "Options");
                    var match = existing.Options.FirstOrDefault(o => o.Id.Value == option.Id)
                        ?? (label.IsSuccess ? existing.Options.FirstOrDefault(o => o.Label.Normalized == label.Value.Normalized) : null);
                    if (match is not null)
                    {
                        _optionMap[option.Id] = match.Id.Value;
                    }
                    else if (references.Options.Contains(option.Id))
                    {
                        Add(ImportSeverity.Error, ImportEntity.Field, key, ImportFindingCodes.ConfigurationMissing, existing.Key.Display, option.Label);
                    }
                }
            }
        }

        // ----- Configuration replace -----
        private void ReplaceWorkflow()
        {
            if (!HasCompleteConfiguration(doc))
            {
                // ADR 0026: a view or data-only export holds only the configuration its records use; it never replaces one.
                Add(ImportSeverity.Error, ImportEntity.File, string.Empty, ImportFindingCodes.ConfigurationIncomplete);
                MatchWorkflowForData();
                return;
            }

            var current = _snapshot.Workflow;
            var l = doc.Lookups;
            _taskStatuses = ReplaceLookups(LookupKind.TaskStatus, l.TaskStatuses, current.TaskStatuses, s => s.Id.Value, s => s.Name, s => s.Id, s => s.Name,
                (id, name, s) => new TaskStatusDefinition(new TaskStatusId(id), name, s.Order, s.IsCompleted, s.SetsStartDate, s.IsBacklog));
            _crStatuses = ReplaceLookups(LookupKind.CrStatus, l.CrStatuses, current.CrStatuses, s => s.Id.Value, s => s.Name, s => s.Id, s => s.Name,
                (id, name, s) => new CrStatusDefinition(new CrStatusId(id), name, s.Order, s.IsCompleted, s.IsBacklog));
            _priorities = ReplaceLookups(LookupKind.Priority, l.Priorities, current.Priorities, p => p.Id.Value, p => p.Name, p => p.Id, p => p.Name,
                (id, name, p) => new PriorityDefinition(new PriorityId(id), name, p.Order));
            _squads = ReplaceLookups(LookupKind.Squad, l.Squads, current.Squads, s => s.Id.Value, s => s.Name, s => s.Id, s => s.Name,
                (id, name, s) => new SquadDefinition(new SquadId(id), name, s.Order));
            _people = ReplaceLookups(LookupKind.Person, l.People, current.People, p => p.Id.Value, p => p.Name, p => p.Id, p => p.Name,
                (id, name, _) => new PersonDefinition(new PersonId(id), name));
            _tags = ReplaceLookups(LookupKind.Tag, l.Tags, current.Tags, t => t.Id.Value, t => t.Name, t => t.Id, t => t.Name,
                (id, name, _) => new TagDefinition(new TagId(id), name));
            ReplaceFields(current);

            // B-04: at least one task status, CR status, and priority must always exist.
            RequireLookup(LookupKind.TaskStatus, _taskStatuses.Count);
            RequireLookup(LookupKind.CrStatus, _crStatuses.Count);
            RequireLookup(LookupKind.Priority, _priorities.Count);

            var replaced = new WorkflowSnapshot(_taskStatuses, _crStatuses, _priorities, _squads, _people, _tags, _fields, current.Defaults);
            ResolveLookupRemovals(current, replaced);
            ResolveOptionRemovals();
            RemapUntouchedFieldDefaults();
            ReplaceDefaults(current);
            var final = replaced with { Fields = _fields, Defaults = _defaults };
            ReportChanges(current, final);
            if (WorkflowStamp(final) == WorkflowStamp(current))
            {
                return;
            }

            _workflowChanged = true;
            if (!HasErrorFindings)
            {
                _replacement = BuildReplacement(current, final);
            }
        }

        private void RequireLookup(LookupKind kind, int count)
        {
            if (count == 0)
            {
                Add(ImportSeverity.Error, ImportEntity.Lookup, string.Empty, ImportFindingCodes.ConfigurationRequired, kind.ToString());
            }
        }

        /// <summary>
        /// The file's values of one kind. Each matches an existing value by ID, then by normalized name, and keeps that
        /// value's ID with the file's name, order, and flags; the rest are added. Unmatched existing values are removed.
        /// </summary>
        private List<TDef> ReplaceLookups<TDef, TSrc>(
            LookupKind kind,
            IReadOnlyList<TSrc> sources,
            IReadOnlyList<TDef> existing,
            Func<TDef, Guid> idOf,
            Func<TDef, LookupName> nameOf,
            Func<TSrc, Guid> sourceId,
            Func<TSrc, string> sourceName,
            Func<Guid, LookupName, TSrc, TDef> create)
        {
            var valid = new List<(TSrc Source, Guid Id, LookupName Name)>();
            var ids = new HashSet<Guid>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                var id = sourceId(source);
                var name = LookupName.Create(sourceName(source));
                if (!name.IsSuccess)
                {
                    Add(ImportSeverity.Error, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.Invalid, kind.ToString(), name.Error!.Code);
                }
                else if (!ids.Add(id) || !names.Add(name.Value.Normalized))
                {
                    Add(ImportSeverity.Error, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.DuplicateInSource, kind.ToString(), name.Value.Display);
                }
                else
                {
                    valid.Add((source, id, name.Value));
                }
            }

            // Every ID match first, so a name match never takes a value that another file value names by ID.
            var matched = new Dictionary<Guid, Guid>();
            var taken = new HashSet<Guid>();
            foreach (var (_, id, _) in valid.Where(v => existing.Any(e => idOf(e) == v.Id)))
            {
                matched[id] = id;
                taken.Add(id);
            }

            foreach (var (_, id, name) in valid.Where(v => !matched.ContainsKey(v.Id)))
            {
                if (existing.FirstOrDefault(e => !taken.Contains(idOf(e)) && nameOf(e).Normalized == name.Normalized) is { } byName)
                {
                    matched[id] = idOf(byName);
                    taken.Add(idOf(byName));
                    Add(ImportSeverity.Info, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.LookupMapped, kind.ToString(), name.Display);
                }
            }

            var result = new List<TDef>();
            foreach (var (source, id, name) in valid)
            {
                var finalId = matched.GetValueOrDefault(id, id);
                _lookupMap[id] = finalId;
                result.Add(create(finalId, name, source));
            }

            _removedLookupValues.AddRange(existing.Where(e => !taken.Contains(idOf(e))).Select(e => (kind, idOf(e), nameOf(e))));
            return result;
        }

        /// <summary>
        /// The file's field definitions, matched by ID first, then by target and key (two passes, as lookups and options).
        /// A matched field keeps its ID, key, target, and kind (B-11: no retyping) and takes the file's label, flags, order,
        /// default, and options. Fields that are not in the file are deactivated, never deleted, so their values are kept.
        /// </summary>
        private void ReplaceFields(WorkflowSnapshot current)
        {
            var valid = new List<(TransferFieldDefinition Source, FieldTarget Target, FieldKind Kind, FieldKey Key, LookupName Label)>();
            var seen = new HashSet<Guid>();
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

                if (!seen.Add(source.Id))
                {
                    Add(ImportSeverity.Error, ImportEntity.Field, key, ImportFindingCodes.DuplicateInSource, null, fieldKey.Value.Display);
                    continue;
                }

                valid.Add((source, target, kind, fieldKey.Value, label.Value));
            }

            // Every ID match first, so a key match never takes a field that another file field names by ID.
            var matched = new Dictionary<Guid, FieldDefinition>();
            var taken = new HashSet<FieldId>();
            foreach (var v in valid)
            {
                if (current.Fields.FirstOrDefault(f => f.Id.Value == v.Source.Id) is { } byId)
                {
                    matched[v.Source.Id] = byId;
                    taken.Add(byId.Id);
                }
            }

            foreach (var v in valid.Where(v => !matched.ContainsKey(v.Source.Id)))
            {
                if (current.Fields.FirstOrDefault(f => !taken.Contains(f.Id) && f.Target == v.Target && f.Key.Normalized == v.Key.Normalized) is { } byKey)
                {
                    matched[v.Source.Id] = byKey;
                    taken.Add(byKey.Id);
                }
            }

            var result = new List<FieldDefinition>();
            foreach (var (source, target, kind, fieldKey, label) in valid)
            {
                var key = source.Id.ToString();
                var existing = matched.GetValueOrDefault(source.Id);
                if (existing is not null && (existing.Kind != kind || existing.Target != target))
                {
                    Add(ImportSeverity.Error, ImportEntity.Field, key, ImportFindingCodes.FieldKindMismatch, "Kind", fieldKey.Display);
                    continue;
                }

                // A new field never takes the ID of a current field.
                var id = existing?.Id ?? (current.Fields.Any(f => f.Id.Value == source.Id) ? new FieldId(Guid.NewGuid()) : new FieldId(source.Id));
                var options = ReplaceOptions(existing, source);

                // B-11: making a field required on a dataset with records needs a reviewed backfill, so a field that is
                // not already required stays optional there; the preview says so.
                var isRequired = source.IsRequired && (existing?.IsRequired == true || IsTargetEmpty);
                if (source.IsRequired && !isRequired)
                {
                    Add(ImportSeverity.Warning, ImportEntity.Field, key, ImportFindingCodes.DefaultUsed, "IsRequired", fieldKey.Display);
                }

                var created = FieldDefinition.Create(id, existing?.Key ?? fieldKey, target, label, kind, isRequired, FileDefault(source, kind, fieldKey), source.DisplayOrder, options);
                if (!created.IsSuccess)
                {
                    Add(ImportSeverity.Error, ImportEntity.Field, key, ImportFindingCodes.Invalid, created.Error!.Field, created.Error.Code);
                    continue;
                }

                if (existing is not null)
                {
                    _removedOptionValues.AddRange(existing.Options.Where(o => options.All(n => n.Id != o.Id)).Select(o => (existing.Id, o)));
                }

                _fieldMap[source.Id] = id.Value;
                result.Add(source.IsActive ? created.Value : created.Value.Deactivate());
            }

            var untouched = current.Fields.Where(f => !taken.Contains(f.Id)).ToList();
            _untouchedFields.UnionWith(untouched.Select(f => f.Id));
            result.AddRange(untouched.Select(f => f.IsActive ? f.Deactivate() : f));
            foreach (var duplicate in result.GroupBy(f => (f.Target, f.Key.Normalized)).Where(g => g.Count() > 1))
            {
                Add(ImportSeverity.Error, ImportEntity.Field, duplicate.First().Id.Value.ToString(), ImportFindingCodes.DuplicateInSource, null, duplicate.First().Key.Display);
            }

            foreach (var duplicate in result.GroupBy(f => f.Id).Where(g => g.Count() > 1))
            {
                Add(ImportSeverity.Error, ImportEntity.Field, duplicate.Key.Value.ToString(), ImportFindingCodes.DuplicateInSource, null, duplicate.First().Key.Display);
            }

            _fields = result;
        }

        /// <summary>
        /// The file's default of a field. A People or Tags default keeps only the people and tags the replaced
        /// configuration has, so it never names a missing value; a dropped one is reported.
        /// </summary>
        private CustomValue? FileDefault(TransferFieldDefinition source, FieldKind kind, FieldKey key)
        {
            if (source.DefaultValue is not { } value)
            {
                return null;
            }

            if (kind is FieldKind.People or FieldKind.Tags && value.Ids is { } ids)
            {
                var known = KnownIds(kind, ids.Select(MapLookup));
                if (known.Count != ids.Distinct().Count())
                {
                    Add(ImportSeverity.Warning, ImportEntity.Field, source.Id.ToString(), ImportFindingCodes.DefaultDropped, null, key.Display);
                }

                return IdsValue(kind, known);
            }

            return TransferValues.ToDomain(value, kind, MapOption, MapLookup, MapLookup);
        }

        /// <summary>The distinct IDs that name a person (People) or tag (Tags) of the replaced configuration.</summary>
        private List<Guid> KnownIds(FieldKind kind, IEnumerable<Guid?> ids) =>
            [.. ids.OfType<Guid>().Distinct().Where(id => kind == FieldKind.People ? _people.Any(p => p.Id.Value == id) : _tags.Any(t => t.Id.Value == id))];

        private static CustomValue? IdsValue(FieldKind kind, List<Guid> ids) => ids.Count == 0 ? null
            : kind == FieldKind.People ? new PeopleCustomValue([.. ids.Select(i => new PersonId(i))])
            : new TagsCustomValue([.. ids.Select(i => new TagId(i))]);

        /// <summary>
        /// The People and Tags defaults of the fields the file does not have follow the removals like records: a removed
        /// value becomes its replacement, or is dropped, so no default names a value that no longer exists.
        /// </summary>
        private void RemapUntouchedFieldDefaults()
        {
            var replacements = _lookupRemaps.ToDictionary(r => r.RemovedId, r => r.ReplacementId);
            for (var i = 0; i < _fields.Count; i++)
            {
                var field = _fields[i];
                IReadOnlyList<Guid>? ids = field.DefaultValue switch
                {
                    PeopleCustomValue people => [.. people.PersonIds.Select(p => p.Value)],
                    TagsCustomValue tags => [.. tags.TagIds.Select(t => t.Value)],
                    _ => null,
                };
                if (ids is null || !_untouchedFields.Contains(field.Id))
                {
                    continue;
                }

                var known = KnownIds(field.Kind, ids.Select(id => replacements.TryGetValue(id, out var replacement) ? replacement : id));
                if (!known.SequenceEqual(ids))
                {
                    _fields[i] = FieldDefinition.Rehydrate(field.Id, field.Key, field.Target, field.Label, field.Kind, field.IsRequired, IdsValue(field.Kind, known),
                        field.DisplayOrder, field.Options, field.IsActive).Value;
                }
            }
        }

        /// <summary>The file's options of a field, matched to the existing ones by ID, then by label; matches keep their ID.</summary>
        private List<FieldOption> ReplaceOptions(FieldDefinition? existing, TransferFieldDefinition source)
        {
            var labeled = new List<(TransferFieldOption Source, LookupName Label)>();
            foreach (var option in source.Options.OrderBy(o => o.Order))
            {
                var label = LookupName.Create(option.Label, "Options");
                if (label.IsSuccess)
                {
                    labeled.Add((option, label.Value));
                }
                else
                {
                    Add(ImportSeverity.Error, ImportEntity.Field, source.Id.ToString(), ImportFindingCodes.Invalid, "Options", label.Error!.Code);
                }
            }

            var existingOptions = existing?.Options ?? [];
            var matched = new Dictionary<Guid, FieldOptionId>();
            foreach (var (option, _) in labeled.Where(o => existingOptions.Any(e => e.Id.Value == o.Source.Id)))
            {
                matched[option.Id] = new FieldOptionId(option.Id);
            }

            foreach (var (option, label) in labeled.Where(o => !matched.ContainsKey(o.Source.Id)))
            {
                if (existingOptions.FirstOrDefault(e => !matched.ContainsValue(e.Id) && e.Label.Normalized == label.Normalized) is { } byLabel)
                {
                    matched[option.Id] = byLabel.Id;
                }
            }

            var result = new List<FieldOption>();
            foreach (var (option, label) in labeled)
            {
                var id = matched.GetValueOrDefault(option.Id, new FieldOptionId(option.Id));
                _optionMap[option.Id] = id.Value;
                result.Add(new FieldOption(id, label, result.Count));
            }

            return result;
        }

        /// <summary>B-04: an in-use removed value needs a replacement from the file, or may be cleared where allowed.</summary>
        private void ResolveLookupRemovals(WorkflowSnapshot current, WorkflowSnapshot replaced)
        {
            // Only a complete configuration replaces one, so the file's defaults replace the current ones and only records
            // need a replacement value.
            foreach (var (kind, id, name) in _removedLookupValues)
            {
                var usage = LookupUsageCounter.Count(kind, id, _snapshot.Tasks, _snapshot.SubtaskCards, _snapshot.ChangeRequests, _snapshot.ReviewItems, current.Defaults)
                    with { Defaults = WorkspaceDefaultRoles.None };

                if (usage.IsUnused)
                {
                    Add(ImportSeverity.Info, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.ConfigurationRemoved, kind.ToString(), name.Display);
                    continue;
                }

                var entries = WorkflowLookups.Entries(replaced, kind);
                List<Guid> ids = [.. entries.Select(e => e.Id), id];
                var policyUsage = usage.ToPolicyUsage();
                var key = ConfigurationRemovalKey.ForLookup(kind, id);
                var (hasChoice, chosen) = Choice(key);
                var isResolved = hasChoice && LookupRemovalPolicy.Validate(kind, ids, id, chosen, policyUsage) is null;
                chosen = isResolved ? chosen : null;
                _removals.Add(new ConfigurationRemoval(
                    key,
                    name.Display,
                    null,
                    usage,
                    LookupRemovalPolicy.Validate(kind, ids, id, (Guid?)null, policyUsage) is null,
                    [.. entries.Select(e => new ConfigurationChoice(e.Id, e.Name.Display))],
                    isResolved,
                    chosen));
                if (isResolved)
                {
                    _lookupRemaps.Add(new LookupRemap(kind, id, chosen));
                    Add(ImportSeverity.Warning, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.ConfigurationRemoved, kind.ToString(), name.Display);
                }
                else
                {
                    Add(ImportSeverity.Error, ImportEntity.Lookup, id.ToString(), ImportFindingCodes.RemovalUnresolved, kind.ToString(), name.Display);
                }
            }
        }

        /// <summary>A removed option of a kept field is resolved like a lookup: replaced, or cleared when the field is optional.</summary>
        private void ResolveOptionRemovals()
        {
            foreach (var (fieldId, option) in _removedOptionValues)
            {
                var field = _fields.First(f => f.Id == fieldId);
                var probe = new FieldOptionRemap(fieldId, option.Id, null);
                var count = field.Target == FieldTarget.Task
                    ? _snapshot.Tasks.Count(t => probe.References(t.CustomValues))
                    : _snapshot.ChangeRequests.Count(c => probe.References(c.CustomValues));
                if (count == 0)
                {
                    Add(ImportSeverity.Info, ImportEntity.Field, fieldId.Value.ToString(), ImportFindingCodes.ConfigurationRemoved, field.Key.Display, option.Label.Display);
                    continue;
                }

                var key = ConfigurationRemovalKey.ForOption(fieldId.Value, option.Id.Value);
                var canClear = !field.IsRequired;
                var (hasChoice, chosen) = Choice(key);
                var isResolved = hasChoice && (chosen is { } c ? field.Options.Any(o => o.Id.Value == c) : canClear);
                chosen = isResolved ? chosen : null;
                var usage = field.Target == FieldTarget.Task
                    ? LookupUsageCounts.None with { Tasks = count }
                    : LookupUsageCounts.None with { ChangeRequests = count };
                _removals.Add(new ConfigurationRemoval(
                    key,
                    option.Label.Display,
                    field.Label.Display,
                    usage,
                    canClear,
                    [.. field.Options.Select(o => new ConfigurationChoice(o.Id.Value, o.Label.Display))],
                    isResolved,
                    chosen));
                if (isResolved)
                {
                    _optionRemaps.Add(new FieldOptionRemap(fieldId, option.Id, chosen is { } replacement ? new FieldOptionId(replacement) : null));
                    Add(ImportSeverity.Warning, ImportEntity.Field, fieldId.Value.ToString(), ImportFindingCodes.ConfigurationRemoved, field.Key.Display, option.Label.Display);
                }
                else
                {
                    Add(ImportSeverity.Error, ImportEntity.Field, fieldId.Value.ToString(), ImportFindingCodes.RemovalUnresolved, field.Key.Display, option.Label.Display);
                }
            }
        }

        /// <summary>The file's defaults, which must name the file's own values.</summary>
        private void ReplaceDefaults(WorkflowSnapshot current)
        {
            var source = doc.Defaults!;
            var taskStatus = MapLookup(source.DefaultTaskStatusId);
            var crStatus = MapLookup(source.DefaultCrStatusId);
            var priority = MapLookup(source.DefaultPriorityId);
            var scheduling = MapLookup(source.CalendarSchedulingStatusId);
            var backlog = MapLookup(source.CrBacklogStatusId);
            Guid? owner = source.DefaultCrOwnerId is { } ownerId ? MapLookup(ownerId) : null;
            if (taskStatus is null || crStatus is null || priority is null || scheduling is null || backlog is null || (source.DefaultCrOwnerId is not null && owner is null))
            {
                Add(ImportSeverity.Error, ImportEntity.File, string.Empty, ImportFindingCodes.DefaultsInvalid);
                return;
            }

            var defaults = new WorkflowDefaults(new TaskStatusId(taskStatus.Value), new CrStatusId(crStatus.Value), new PriorityId(priority.Value),
                owner is { } o ? new PersonId(o) : null, new CrStatusId(scheduling.Value), new CrStatusId(backlog.Value));
            if (!DefaultsResolve(defaults))
            {
                Add(ImportSeverity.Error, ImportEntity.File, string.Empty, ImportFindingCodes.DefaultsInvalid);
                return;
            }

            _defaults = defaults;
            if (defaults != current.Defaults)
            {
                Add(ImportSeverity.Info, ImportEntity.File, string.Empty, ImportFindingCodes.DefaultsApplied);
            }
        }

        private bool DefaultsResolve(WorkflowDefaults d) =>
            _taskStatuses.Any(s => s.Id == d.DefaultTaskStatusId)
            && _crStatuses.Any(s => s.Id == d.DefaultCrStatusId)
            && _priorities.Any(p => p.Id == d.DefaultPriorityId)
            && (d.DefaultCrOwnerId is not { } owner || _people.Any(p => p.Id == owner))
            && _crStatuses.Any(s => s.Id == d.CalendarSchedulingStatusId)
            && WorkflowConfigurationPolicy.ValidateCrBacklogStatus(_crStatuses.FirstOrDefault(s => s.Id == d.CrBacklogStatusId)) is null;

        /// <summary>The preview lists what the replace adds, updates, and deactivates (removals are listed as resolved).</summary>
        private void ReportChanges(WorkflowSnapshot current, WorkflowSnapshot final)
        {
            foreach (var kind in Enum.GetValues<LookupKind>())
            {
                foreach (var entry in WorkflowLookups.Entries(final, kind))
                {
                    var before = WorkflowStamps.Item(current, kind, entry.Id);
                    if (before.Length == 0)
                    {
                        Add(ImportSeverity.Info, ImportEntity.Lookup, entry.Id.ToString(), ImportFindingCodes.ConfigurationAdded, kind.ToString(), entry.Name.Display);
                    }
                    else if (before != WorkflowStamps.Item(final, kind, entry.Id))
                    {
                        Add(ImportSeverity.Info, ImportEntity.Lookup, entry.Id.ToString(), ImportFindingCodes.ConfigurationUpdated, kind.ToString(), entry.Name.Display);
                    }
                }
            }

            foreach (var field in final.Fields)
            {
                var previous = current.Fields.FirstOrDefault(f => f.Id == field.Id);
                var key = field.Id.Value.ToString();
                if (previous is null)
                {
                    Add(ImportSeverity.Info, ImportEntity.Field, key, ImportFindingCodes.ConfigurationAdded, null, field.Key.Display);
                }
                else if (previous.IsActive && !field.IsActive)
                {
                    Add(ImportSeverity.Warning, ImportEntity.Field, key, ImportFindingCodes.FieldDeactivated, null, field.Key.Display);
                }
                else if (WorkflowStamps.Field(current, field.Id) != WorkflowStamps.Field(final, field.Id))
                {
                    Add(ImportSeverity.Info, ImportEntity.Field, key, ImportFindingCodes.ConfigurationUpdated, null, field.Key.Display);
                }
            }
        }

        /// <summary>
        /// The writes that move records off removed values, and the workflows Apply saves before them. The records the
        /// data import plans are then planned against the remapped ones.
        /// </summary>
        private ConfigurationReplacement BuildReplacement(WorkflowSnapshot current, WorkflowSnapshot final)
        {
            var staging = new List<WorkflowSnapshot>();
            var baseline = current;

            // Name uniqueness is checked per statement, so values that are renamed or removed first get unique
            // temporary names; the final save then gives every value its new name without a collision.
            foreach (var kind in Enum.GetValues<LookupKind>())
            {
                foreach (var entry in WorkflowLookups.Entries(current, kind))
                {
                    if (WorkflowLookups.Find(final, kind, entry.Id) is not { } kept || kept.Name.Normalized != entry.Name.Normalized)
                    {
                        baseline = WorkflowLookups.Rename(baseline, kind, entry.Id, LookupName.Create("~" + entry.Id.ToString("N")).Value);
                    }
                }
            }

            if (!ReferenceEquals(baseline, current))
            {
                staging.Add(baseline);
            }

            // Remapped and imported records point at the file's values, so those exist beside the removed ones until
            // the final save deletes them.
            var removed = _removedLookupValues.Select(r => r.Id).ToHashSet();
            staging.Add(final with
            {
                TaskStatuses = [.. final.TaskStatuses, .. baseline.TaskStatuses.Where(s => removed.Contains(s.Id.Value))],
                CrStatuses = [.. final.CrStatuses, .. baseline.CrStatuses.Where(s => removed.Contains(s.Id.Value))],
                Priorities = [.. final.Priorities, .. baseline.Priorities.Where(p => removed.Contains(p.Id.Value))],
                Squads = [.. final.Squads, .. baseline.Squads.Where(s => removed.Contains(s.Id.Value))],
                People = [.. final.People, .. baseline.People.Where(p => removed.Contains(p.Id.Value))],
                Tags = [.. final.Tags, .. baseline.Tags.Where(t => removed.Contains(t.Id.Value))],
            });

            var tasks = _snapshot.Tasks.ToList();
            var cards = _snapshot.SubtaskCards.ToList();
            var changeRequests = _snapshot.ChangeRequests.ToList();
            var reviews = _snapshot.ReviewItems.ToList();
            foreach (var remap in _lookupRemaps)
            {
                var ranks = remap.Kind == LookupKind.TaskStatus ? AppendRanks(tasks, cards, remap.RemovedId, remap.RequiredReplacement) : [];
                for (var i = 0; i < tasks.Count; i++)
                {
                    tasks[i] = tasks[i].Remap(remap, ranks.TryGetValue((0, tasks[i].Id.Value), out var rank) ? rank : null, now) ?? tasks[i];
                }

                for (var i = 0; i < cards.Count; i++)
                {
                    cards[i] = cards[i].Remap(remap, ranks.TryGetValue((1, cards[i].Id.Value), out var rank) ? rank : null, now) ?? cards[i];
                }

                for (var i = 0; i < changeRequests.Count; i++)
                {
                    changeRequests[i] = changeRequests[i].Remap(remap, now) ?? changeRequests[i];
                }

                for (var i = 0; i < reviews.Count; i++)
                {
                    reviews[i] = reviews[i].Remap(remap, now) ?? reviews[i];
                }
            }

            foreach (var remap in _optionRemaps)
            {
                for (var i = 0; i < tasks.Count; i++)
                {
                    tasks[i] = tasks[i].Remap(remap, now) ?? tasks[i];
                }

                for (var i = 0; i < changeRequests.Count; i++)
                {
                    changeRequests[i] = changeRequests[i].Remap(remap, now) ?? changeRequests[i];
                }
            }

            var replacement = new ConfigurationReplacement
            {
                Staging = staging,
                Tasks = Changed(_snapshot.Tasks, tasks, t => t.Revision),
                SubtaskCards = Changed(_snapshot.SubtaskCards, cards, c => c.Revision),
                ChangeRequests = Changed(_snapshot.ChangeRequests, changeRequests, c => c.Revision),
                Reviews = Changed(_snapshot.ReviewItems, reviews, r => r.Revision),
                RemovedLookups = [.. _removedLookupValues.Select(r => new RemovedLookup(r.Kind, r.Id, RecordUsage(r.Kind, r.Id)))],
                RemovedOptions = [.. _removedOptionValues.Select(r => RemovedOptionUsage(r.Field, r.Option.Id))],
            };

            // The data is planned against the records as the replace leaves them.
            _snapshot = _snapshot with { Tasks = tasks, SubtaskCards = cards, ChangeRequests = changeRequests, ReviewItems = reviews };
            return replacement;
        }

        private LookupUsageCounts RecordUsage(LookupKind kind, Guid id) =>
            LookupUsageCounter.Count(kind, id, _snapshot.Tasks, _snapshot.SubtaskCards, _snapshot.ChangeRequests, _snapshot.ReviewItems, _snapshot.Workflow.Defaults)
                with { Defaults = WorkspaceDefaultRoles.None };

        private RemovedOption RemovedOptionUsage(FieldId fieldId, FieldOptionId optionId)
        {
            var probe = new FieldOptionRemap(fieldId, optionId, null);
            var target = _fields.First(f => f.Id == fieldId).Target;
            var count = target == FieldTarget.Task
                ? _snapshot.Tasks.Count(t => probe.References(t.CustomValues))
                : _snapshot.ChangeRequests.Count(c => probe.References(c.CustomValues));
            return new RemovedOption(probe, target, count);
        }

        private static List<PlannedRecord<T>> Changed<T>(IReadOnlyList<T> before, List<T> after, Func<T, long> revisionOf)
            where T : class =>
            [.. before.Select((record, i) => (record, updated: after[i])).Where(p => !ReferenceEquals(p.record, p.updated))
                .Select(p => new PlannedRecord<T>(p.updated, revisionOf(p.record)))];

        /// <summary>
        /// B-04 (as <see cref="RemoveLookup"/>): the active cards of a removed column, tasks and Subtask cards alike, move
        /// to the end of the replacement column in their previous order. Archived work keeps its rank.
        /// </summary>
        private static Dictionary<(int Kind, Guid Id), long> AppendRanks(List<TaskItem> tasks, List<SubtaskCard> cards, Guid removed, Guid replacement)
        {
            var archived = tasks.Where(t => t.IsArchived).Select(t => t.Id).ToHashSet();
            var column = tasks.Where(t => !t.IsArchived).Select(t => (Kind: 0, Id: t.Id.Value, Status: t.StatusId.Value, t.Rank))
                .Concat(cards.Where(c => !archived.Contains(c.ParentTaskId)).Select(c => (Kind: 1, Id: c.Id.Value, Status: c.StatusId.Value, c.Rank)))
                .ToList();
            long? last = column.Where(c => c.Status == replacement).Select(c => (long?)c.Rank).Max();
            var ranks = new Dictionary<(int Kind, Guid Id), long>();
            foreach (var card in column.Where(c => c.Status == removed).OrderBy(c => c.Rank).ThenBy(c => c.Kind).ThenBy(c => c.Id))
            {
                var rank = RankPolicy.After(last);
                ranks[(card.Kind, card.Id)] = rank;
                last = rank;
            }

            return ranks;
        }
    }
}
