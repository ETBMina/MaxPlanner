using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Transfer;

public static partial class ImportPlanner
{
    private sealed partial class Run
    {
        // ----- Workflow -----
        private void MergeWorkflow()
        {
            var w = _snapshot.Workflow;
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

                    if (context.SourceNamespace is not null && !existing.IsActive)
                    {
                        // ADR 0027: an external source only creates fields, and never writes values into a turned-off one.
                        Add(ImportSeverity.Error, ImportEntity.Field, key, ImportFindingCodes.FieldInactive, null, fieldKey.Value.Display);
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
                owner is { } o ? new PersonId(o) : null, new CrStatusId(scheduling.Value), new CrStatusId(backlog.Value),
                CopyIdFormatter.Normalize(source.CopyIdFormat));
            if (defaults != _defaults)
            {
                _defaults = defaults;
                _workflowChanged = true;
                Add(ImportSeverity.Info, ImportEntity.File, string.Empty, ImportFindingCodes.DefaultsApplied);
            }
        }
    }
}
