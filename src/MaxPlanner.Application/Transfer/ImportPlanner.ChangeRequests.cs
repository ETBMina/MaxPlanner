using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Transfer;

public static partial class ImportPlanner
{
    private sealed partial class Run
    {
        // ----- Change Requests -----
        private List<PlannedRecord<ChangeRequest>> PlanChangeRequests()
        {
            const string Entity = ImportEntity.ChangeRequest;
            var planned = new List<PlannedRecord<ChangeRequest>>();
            var byId = _snapshot.ChangeRequests.ToDictionary(c => c.Id.Value);
            var byNumber = _snapshot.ChangeRequests.ToDictionary(c => c.Number.Normalized, StringComparer.Ordinal);
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
                // A new CR without a supplied status gets the default CR status, as tasks and cards do.
                var status = Supplied(supplied, nameof(source.StatusId)) || existing is null
                    ? Resolve(source.StatusId ?? (existing is null ? _defaults.DefaultCrStatusId.Value : null), _crStatuses, s => s.Id.Value, "StatusId", errors)
                    : _crStatuses.FirstOrDefault(s => s.Id == existing.StatusId);
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

            var title = Supplied(s, nameof(source.Title)) || existing is null ? Check(TitleText.Create(source.Title), errors) : existing.Title;
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
            var custom = PlannedCustomValues(s, source.CustomValues, existing?.CustomValues, FieldTarget.ChangeRequest, errors);
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
    }
}
