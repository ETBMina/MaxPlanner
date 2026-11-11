using MaxPlanner.Application.Settings;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Transfer.External;

public static partial class ExternalImportPlanner
{
    private sealed partial class Builder
    {
        // ----- Lookup values (B-13.8) -----
        private Guid? Value(LookupKind kind, string? raw, string entity, string key)
        {
            if (raw is null || raw.Trim().Length == 0)
            {
                return null;
            }

            var name = LookupName.Create(raw);
            if (!name.IsSuccess)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, entity, key, ImportFindingCodes.Invalid, kind.ToString(), name.Error!.Code));
                return null;
            }

            var valueKey = ValueKey(kind, name.Value.Normalized);
            if (_values.TryGetValue(valueKey, out var decided))
            {
                return decided.ChosenId ?? NewIdOf(kind, name.Value.Normalized);
            }

            var existing = WorkflowLookups.Entries(workflow, kind).FirstOrDefault(e => e.Name.Normalized == name.Value.Normalized)?.Id;
            var chosen = options.ValueChoices.TryGetValue(valueKey, out var choice) ? choice : existing;
            if (chosen is { } id && WorkflowLookups.Find(workflow, kind, id) is null)
            {
                chosen = null;
            }

            _values[valueKey] = new ExternalValueDecision(valueKey, kind, name.Value.Display, existing, chosen);
            if (chosen is { } resolved)
            {
                return resolved;
            }

            var newId = Guid.CreateVersion7();
            var order = WorkflowLookups.Entries(workflow, kind).Count + NewCount(kind);
            switch (kind)
            {
                case LookupKind.TaskStatus:
                    // Proposed statuses get no semantic flags; the user sets them in Settings (B-04: flags, never labels).
                    _newTaskStatuses.Add(new TransferTaskStatus(newId, name.Value.Display, order, false, false, false));
                    break;
                case LookupKind.CrStatus:
                    _newCrStatuses.Add(new TransferCrStatus(newId, name.Value.Display, order, false, false));
                    break;
                case LookupKind.Priority:
                    _newPriorities.Add(new TransferOrderedLookup(newId, name.Value.Display, order));
                    break;
                case LookupKind.Squad:
                    _newSquads.Add(new TransferOrderedLookup(newId, name.Value.Display, order));
                    break;
                case LookupKind.Person:
                    _newPeople.Add(new TransferNamedLookup(newId, name.Value.Display));
                    break;
                default:
                    _newTags.Add(new TransferNamedLookup(newId, name.Value.Display));
                    break;
            }

            return newId;
        }

        private int NewCount(LookupKind kind) => kind switch
        {
            LookupKind.TaskStatus => _newTaskStatuses.Count,
            LookupKind.CrStatus => _newCrStatuses.Count,
            LookupKind.Priority => _newPriorities.Count,
            LookupKind.Squad => _newSquads.Count,
            LookupKind.Person => _newPeople.Count,
            _ => _newTags.Count,
        };

        private Guid NewIdOf(LookupKind kind, string normalized) => kind switch
        {
            LookupKind.TaskStatus => _newTaskStatuses.First(s => Norm(s.Name) == normalized).Id,
            LookupKind.CrStatus => _newCrStatuses.First(s => Norm(s.Name) == normalized).Id,
            LookupKind.Priority => _newPriorities.First(s => Norm(s.Name) == normalized).Id,
            LookupKind.Squad => _newSquads.First(s => Norm(s.Name) == normalized).Id,
            LookupKind.Person => _newPeople.First(s => Norm(s.Name) == normalized).Id,
            _ => _newTags.First(s => Norm(s.Name) == normalized).Id,
        };

        private static string Norm(string name) => name.Trim().ToUpperInvariant();

        /// <summary>
        /// ADR 0024: a task has at most one Designer and one Developer. The first name that resolves is kept; a name before
        /// it that does not resolve keeps its own error. Names after it are not resolved (so no person is created for a
        /// dropped one) and are reported in a warning.
        /// </summary>
        private Guid? FirstPerson(ExternalValue? value, string field, string entity, string key)
        {
            var names = List(value);
            for (var i = 0; i < names.Count; i++)
            {
                if (Value(LookupKind.Person, names[i], entity, key) is not { } id)
                {
                    continue;
                }

                if (i + 1 < names.Count)
                {
                    _findings.Add(new ImportFinding(ImportSeverity.Warning, entity, key, ImportFindingCodes.PeopleDropped, field, string.Join(", ", names.Skip(i + 1))));
                }

                return id;
            }

            return null;
        }

        private List<Guid> Values(LookupKind kind, ExternalValue? value, string entity, string key) =>
            [.. List(value).Select(v => Value(kind, v, entity, key)).OfType<Guid>().Distinct()];
    }
}
