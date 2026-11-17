using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Transfer;

public static partial class ImportPlanner
{
    private sealed partial class Run
    {
        // ----- General to-dos -----
        private List<PlannedRecord<GeneralTodo>> PlanGeneralTodos()
        {
            const string Entity = ImportEntity.GeneralTodo;
            var planned = new List<PlannedRecord<GeneralTodo>>();
            var byId = _snapshot.GeneralTodos.ToDictionary(t => t.Id.Value);
            var nextRank = _snapshot.GeneralTodos.Select(t => t.Rank).DefaultIfEmpty(0).Max();
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
