using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Transfer;

public static partial class ImportPlanner
{
    private sealed partial class Run
    {
        // ----- Reviews -----
        private List<PlannedRecord<ReviewItem>> PlanReviews()
        {
            const string Entity = ImportEntity.Review;
            var planned = new List<PlannedRecord<ReviewItem>>();
            var byId = _snapshot.ReviewItems.ToDictionary(r => r.Id.Value);
            var nextRank = _snapshot.ReviewItems.Select(r => r.Rank).DefaultIfEmpty(0).Max();
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
    }
}
