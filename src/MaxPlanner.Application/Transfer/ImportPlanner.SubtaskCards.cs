using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Transfer;

public static partial class ImportPlanner
{
    private sealed partial class Run
    {
        // ----- Subtask cards -----
        private List<PlannedRecord<SubtaskCard>> PlanSubtaskCards(List<PlannedRecord<TaskItem>> plannedTasks)
        {
            const string Entity = ImportEntity.SubtaskCard;
            var planned = new List<PlannedRecord<SubtaskCard>>();
            var byId = _snapshot.SubtaskCards.ToDictionary(c => c.Id.Value);
            var parents = _snapshot.Tasks.ToDictionary(t => t.Id.Value);
            foreach (var task in plannedTasks)
            {
                parents[task.Record.Id.Value] = task.Record;
            }

            var perParent = _snapshot.SubtaskCards.GroupBy(c => c.ParentTaskId.Value).ToDictionary(g => g.Key, g => g.Count());
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
    }
}
