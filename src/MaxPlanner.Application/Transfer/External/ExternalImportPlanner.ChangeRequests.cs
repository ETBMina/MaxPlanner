using System.Globalization;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Transfer.External;

public static partial class ExternalImportPlanner
{
    private sealed partial class Builder
    {
        // ----- Change Requests -----
        private void ReadChangeRequest(ExternalTable table, ExternalRow row, TableColumns map)
        {
            const string Entity = ImportEntity.ChangeRequest;
            var columns = map.Fields;
            var displayKey = table.Name + "#" + row.RowNumber.ToString(CultureInfo.InvariantCulture);
            var number = Text(row, columns, ExternalField.CrNumber);
            if (number is null)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.Invalid, "Number", "required"));
                return;
            }

            var normalized = Norm(number);
            if (_crs.ContainsKey(normalized))
            {
                // B-13.8: duplicate CR numbers in the source are reported, never merged silently.
                _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.DuplicateCrNumber, "Number", number));
                return;
            }

            var sourceKey = SourceKey(row, columns);

            // A CR number already in the dataset is the same CR (its unique key, B-03.2): the row updates or skips it.
            Guid? id = existingCrs.TryGetValue(normalized, out var existingId) && !ledger.ContainsKey((Entity, sourceKey))
                ? existingId
                : RecordId(Entity, sourceKey, displayKey);
            if (id is not { } recordId)
            {
                return;
            }

            if (existingCrs.ContainsKey(normalized))
            {
                _usedKeys.Add((Entity, sourceKey));
            }

            var draft = new CrDraft { Id = recordId, Number = number, Key = normalized, IsFromSource = true, IsExisting = existingCrs.ContainsKey(normalized) };
            draft.Supplied.Add("Number");
            var supplied = draft.Supplied;
            void Map(ExternalField field)
            {
                if (columns.ContainsKey(field))
                {
                    supplied.Add(MemberOf(field));
                }
            }

            foreach (var field in new[] { ExternalField.Status, ExternalField.Priority, ExternalField.EmailSubject, ExternalField.SolutionDesignUrl, ExternalField.Notes })
            {
                Map(field);
            }

            draft.Title = Text(row, columns, ExternalField.Title);
            draft.TaskNumber = Text(row, columns, ExternalField.TaskNumber);
            draft.SquadName = Text(row, columns, ExternalField.Squad);
            draft.HasSquadColumn |= columns.ContainsKey(ExternalField.Squad);
            draft.OwnerName = Text(row, columns, ExternalField.Owner);
            var legacy = Legacy(table, row, map, null);
            var created = Created(row, columns, Entity, displayKey);
            draft.Record = new TransferChangeRequest
            {
                Id = recordId,
                Number = number,
                StatusId = Value(LookupKind.CrStatus, Text(row, columns, ExternalField.Status), Entity, displayKey),
                PriorityId = Value(LookupKind.Priority, Text(row, columns, ExternalField.Priority), Entity, displayKey),
                PlannedStart = Date(row, columns, ExternalField.PlannedStart, Entity, displayKey, supplied),
                PlannedEnd = Date(row, columns, ExternalField.PlannedEnd, Entity, displayKey, supplied),
                EmailSubject = Text(row, columns, ExternalField.EmailSubject),
                SolutionDesignUrl = Text(row, columns, ExternalField.SolutionDesignUrl),
                Description = Text(row, columns, ExternalField.Notes) is { } description ? description : row.Body,
                CustomValues = CustomValues(row, map, Entity, displayKey, supplied),
                Preserved = legacy is null ? null : new TransferPreserved(null, null, legacy),
                CreatedAtUtc = created,
                UpdatedAtUtc = UpdatedFor(created),
            };
            if (draft.Record.Description is not null)
            {
                supplied.Add("Description");
            }

            if (legacy is not null)
            {
                supplied.Add("Preserved");
            }

            _crs[normalized] = draft;
        }

        private CrDraft? CrFor(string number)
        {
            var normalized = Norm(number);
            if (_crs.TryGetValue(normalized, out var draft))
            {
                return draft;
            }

            if (existingCrs.TryGetValue(normalized, out var existing))
            {
                draft = new CrDraft { Id = existing, Number = number, Key = normalized, IsExisting = true };
                _crs[normalized] = draft;
                return draft;
            }

            // B-13.6: a CR referred to only by tasks becomes a new CR whose values come from those tasks.
            if (RecordId(ImportEntity.ChangeRequest, "cr-number:" + normalized, number) is not { } id)
            {
                return null;
            }

            draft = new CrDraft { Id = id, Number = number, Key = normalized };
            draft.Supplied.Add("Number");
            _crs[normalized] = draft;
            return draft;
        }

        private List<TransferChangeRequest> ResolveChangeRequests()
        {
            var result = new List<TransferChangeRequest>();
            foreach (var cr in _crs.Values)
            {
                if (cr.IsExisting && !cr.IsFromSource)
                {
                    // The dataset's CR owns its values (T-13); linked task values were preserved on the tasks.
                    continue;
                }

                var record = cr.Record ?? new TransferChangeRequest { Id = cr.Id, Number = cr.Number };
                var title = Propose(cr, "Title", cr.Title);
                var taskNumber = Propose(cr, "TaskNumber", cr.TaskNumber);
                var squad = Propose(cr, "Squad", cr.SquadName);
                var isGeneralSquad = squad is null ? cr.HasSquadColumn : IsGeneralSquad(squad);
                if (isGeneralSquad)
                {
                    squad = null;
                }

                var owner = Propose(cr, "Owner", cr.OwnerName);
                var key = cr.Number;
                Guid? ownerId = owner is not null ? Value(LookupKind.Person, owner, ImportEntity.ChangeRequest, key) : null;
                if (ownerId is null && !cr.IsExisting)
                {
                    _missingOwners++;
                    if (FallbackOwner(key) is { } fallback)
                    {
                        ownerId = fallback;
                        _findings.Add(new ImportFinding(ImportSeverity.Warning, ImportEntity.ChangeRequest, key, ImportFindingCodes.DefaultUsed, "OwnerId"));
                    }
                    else
                    {
                        _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.ChangeRequest, key, ImportFindingCodes.OwnerRequired, "OwnerId", cr.Number));
                    }
                }

                if (title is null && !cr.IsExisting)
                {
                    _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.ChangeRequest, key, ImportFindingCodes.NoTitle, "Title", cr.Number));
                }

                var supplied = new HashSet<string>(cr.Supplied, StringComparer.Ordinal);
                if (title is not null)
                {
                    supplied.Add("Title");
                }

                if (taskNumber is not null)
                {
                    supplied.Add("TaskNumber");
                }

                if (squad is not null || isGeneralSquad)
                {
                    supplied.Add("SquadId");
                }

                if (ownerId is not null)
                {
                    supplied.Add("OwnerId");
                }

                result.Add(record with
                {
                    Title = title,
                    TaskNumber = taskNumber,
                    SquadId = squad is null ? null : Value(LookupKind.Squad, squad, ImportEntity.ChangeRequest, key),
                    OwnerId = ownerId,
                    SuppliedFields = supplied,
                });
            }

            return result;
        }

        // B-13.7: an existing person, or a source person the import creates (offered after people are mapped).
        private Guid? FallbackOwner(string key) =>
            options.FallbackOwnerId is { } id && workflow.FindPerson(new Domain.Identifiers.PersonId(id)) is not null ? id
            : options.FallbackOwnerName is { } name ? Value(LookupKind.Person, name, ImportEntity.ChangeRequest, key)
            : null;

        // ADR 0020: no squad displays as "General"; an exported "General" label or an empty cell is no squad, never a new squad.
        private static bool IsGeneralSquad(string name) =>
            name.Equals(GeneralSquadName, StringComparison.OrdinalIgnoreCase) || name.Equals(GeneralNoSquadName, StringComparison.OrdinalIgnoreCase);

        /// <summary>B-13.7: the CR's own value, else the single value its linked tasks agree on, else the user's choice.</summary>
        private string? Propose(CrDraft cr, string field, string? own)
        {
            if (own is not null)
            {
                return own;
            }

            var candidates = cr.TaskValues.Where(v => v.Field == field).Select(v => v.Value)
                .Distinct(field == "Title" ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Warning, ImportEntity.ChangeRequest, cr.Number, ImportFindingCodes.CrValueProposed, field, cr.Number));
                return candidates[0];
            }

            var key = cr.Key + "|" + field;
            var chosen = options.CrValueChoices.TryGetValue(key, out var choice) && choice is not null && candidates.Contains(choice) ? choice : null;
            _conflicts.Add(new ExternalCrConflict(key, cr.Number, field, candidates, chosen));
            if (chosen is null)
            {
                _findings.Add(new ImportFinding(ImportSeverity.Error, ImportEntity.ChangeRequest, cr.Number, ImportFindingCodes.ConflictingCrValues, field, cr.Number));
            }

            return chosen;
        }
    }
}
