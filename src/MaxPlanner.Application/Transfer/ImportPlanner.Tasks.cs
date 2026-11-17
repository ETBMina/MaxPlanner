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
        // ----- Tasks -----
        private (List<PlannedRecord<TaskItem>> Tasks, List<PlannedLink> Links) PlanTasks()
        {
            const string Entity = ImportEntity.Task;
            var planned = new List<PlannedRecord<TaskItem>>();
            var links = new List<PlannedLink>();
            var byId = _snapshot.Tasks.ToDictionary(t => t.Id.Value);
            var crIds = _snapshot.ChangeRequests.Select(c => c.Id.Value).ToHashSet();
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

                        // ADR 0006 / AC-29: no CR for a linked task is an unlink attempt when the source states the link: a complete
                        // (JSON) record in a document with links, or a tabular row that supplies a blank ChangeRequestId.
                        var statesLink = source.SuppliedFields is null ? documentHasLinks : source.SuppliedFields.Contains(TransferColumns.ChangeRequestId);
                        if (linkCr is null && statesLink)
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
            var designer = PlannedPerson(source.DesignerId, nameof(source.DesignerId), source.DesignerIds, nameof(source.DesignerIds), existing?.DesignerId);
            var developer = PlannedPerson(source.DeveloperId, nameof(source.DeveloperId), source.DeveloperIds, nameof(source.DeveloperIds), existing?.DeveloperId);
            var tags = Supplied(s, nameof(source.TagIds))
                ? ResolveList(source.TagIds, id => _tags.Any(t => t.Id.Value == id), id => new TagId(id), "TagIds", errors)
                : existing?.TagIds.ToList() ?? [];
            var notes = Supplied(s, nameof(source.Notes)) ? Check(LongText.CreateOptional(source.Notes, "Notes"), errors) : existing?.Notes;
            var start = Supplied(s, nameof(source.StartDate)) ? source.StartDate : existing?.Dates.Start;
            var end = Supplied(s, nameof(source.EndDate)) ? source.EndDate : existing?.Dates.End;
            var archived = Supplied(s, nameof(source.IsArchived)) ? source.IsArchived ?? false : existing?.IsArchived ?? false;
            var custom = PlannedCustomValues(s, source.CustomValues, existing?.CustomValues, FieldTarget.Task, errors);
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

            // ADR 0028: a version 1 array (JSON member or legacy table column) wins when present and keeps its first
            // person with a warning; otherwise the single ID. A supplied but empty legacy column clears the role.
            PersonId? PlannedPerson(Guid? id, string field, IReadOnlyList<Guid>? legacy, string legacyField, PersonId? current)
            {
                if (legacy is not null)
                {
                    return SinglePerson(legacy, legacyField, Entity, key, errors);
                }

                if (Supplied(s, field))
                {
                    return SinglePerson(id is { } value ? [value] : null, field, Entity, key, errors);
                }

                return Supplied(s, legacyField) ? null : current;
            }
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
    }
}
