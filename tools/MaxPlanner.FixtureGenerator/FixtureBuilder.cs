using System.Collections.Concurrent;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.FixtureGenerator;

internal sealed record CrSpec(
    string Number,
    string Title,
    PersonId Owner,
    SquadId? Squad,
    string? TaskNumber,
    PriorityId? Priority = null,
    CrStatusId? Status = null,
    DateOnly? PlannedStart = null,
    DateOnly? PlannedEnd = null,
    string? Description = null);

internal sealed record TaskSpec(
    string? Title,
    string Service,
    ChangeRequestId? Cr,
    PriorityId? Priority,
    TaskStatusId? Status = null,
    PersonId? Designer = null,
    PersonId? Developer = null,
    IReadOnlyList<TagId>? Tags = null,
    string? Notes = null,
    IReadOnlyList<(string Text, bool Done)>? Checklist = null);

/// <summary>
/// Thin wrappers over the use cases that fail loudly, because a fixture must be complete or absent. Safe to call
/// concurrently: the coordinator orders the commits, and every create preallocates its ID.
/// </summary>
internal sealed class FixtureBuilder(IMutationCoordinator coordinator)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly ConcurrentDictionary<TaskId, long> _taskRevisions = new();
    private readonly ConcurrentDictionary<SubtaskCardId, long> _cardRevisions = new();
    private readonly ConcurrentDictionary<ChangeRequestId, long> _crRevisions = new();

    public async Task<PersonId> PersonAsync(string name)
    {
        var id = Guid.NewGuid();
        Ok(await new AddLookup(coordinator).ExecuteAsync(new AddLookupCommand(OperationId.New(), LookupKind.Person, id, name), None));
        return new PersonId(id);
    }

    public async Task<TagId> TagAsync(string name)
    {
        var id = Guid.NewGuid();
        Ok(await new AddLookup(coordinator).ExecuteAsync(new AddLookupCommand(OperationId.New(), LookupKind.Tag, id, name), None));
        return new TagId(id);
    }

    public async Task<SquadId> SquadAsync(string name)
    {
        var id = Guid.NewGuid();
        Ok(await new AddLookup(coordinator).ExecuteAsync(new AddLookupCommand(OperationId.New(), LookupKind.Squad, id, name), None));
        return new SquadId(id);
    }

    public async Task<ChangeRequestId> CrAsync(CrSpec spec)
    {
        var id = new ChangeRequestId(Guid.NewGuid());
        var draft = new ChangeRequestDraft(
            spec.Number, spec.Title, spec.Owner, spec.Squad, spec.Priority, spec.PlannedStart, spec.PlannedEnd, null, null, spec.Description, spec.TaskNumber, CustomValue.NoValues);
        Ok(await new CreateChangeRequest(coordinator).ExecuteAsync(new CreateChangeRequestCommand(OperationId.New(), id, draft, spec.Status), None));
        _crRevisions[id] = 1;
        return id;
    }

    public async Task<TaskId> TaskAsync(TaskSpec spec)
    {
        var id = new TaskId(Guid.NewGuid());
        var draft = new TaskContentDraft(spec.Title, spec.Service, spec.Designer, spec.Developer, spec.Tags ?? [], spec.Notes, CustomValue.NoValues);
        var link = spec.Cr is { } crId ? new CrLinkTarget(crId, _crRevisions[crId]) : null;
        var checklist = spec.Checklist?.Select(i => new ChecklistItemDraft(new ChecklistItemId(Guid.NewGuid()), i.Text, i.Done)).ToList();
        Ok(await new CreateTask(coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), id, draft, StatusId: spec.Status, PriorityId: spec.Priority, LinkTo: link, ChecklistItems: checklist), None));
        _taskRevisions[id] = 1;
        return id;
    }

    public async Task MoveAsync(TaskId task, TaskStatusId target) =>
        _taskRevisions[task] = Ok(await new MoveTask(coordinator).ExecuteAsync(
            new MoveTaskCommand(OperationId.New(), task, _taskRevisions[task], target, BoardPlacement.AppendToEnd), None));

    public async Task ArchiveAsync(TaskId task) =>
        _taskRevisions[task] = Ok(await new ArchiveTask(coordinator).ExecuteAsync(new ArchiveTaskCommand(OperationId.New(), task, _taskRevisions[task]), None));

    public async Task ChecklistAsync(TaskId task, params (string Text, bool Done)[] items)
    {
        foreach (var (text, done) in items)
        {
            var item = new ChecklistItemId(Guid.NewGuid());
            _taskRevisions[task] = Ok(await new AddChecklistItem(coordinator).ExecuteAsync(
                new AddChecklistItemCommand(OperationId.New(), task, _taskRevisions[task], item, text), None));
            if (done)
            {
                _taskRevisions[task] = Ok(await new SetChecklistItemCompleted(coordinator).ExecuteAsync(
                    new SetChecklistItemCompletedCommand(OperationId.New(), task, _taskRevisions[task], item, true), None));
            }
        }
    }

    public async Task<SubtaskCardId> CardAsync(TaskId parent, string subtitle, string? notes = null, TaskStatusId? status = null)
    {
        var id = new SubtaskCardId(Guid.NewGuid());
        Ok(await new CreateSubtaskCard(coordinator).ExecuteAsync(new CreateSubtaskCardCommand(OperationId.New(), parent, id, subtitle, notes, status), None));
        _cardRevisions[id] = 1;
        return id;
    }

    public async Task MoveCardAsync(SubtaskCardId card, TaskStatusId target) =>
        _cardRevisions[card] = Ok(await new MoveSubtaskCard(coordinator).ExecuteAsync(
            new MoveSubtaskCardCommand(OperationId.New(), card, _cardRevisions[card], target, BoardPlacement.AppendToEnd), None));

    public async Task ReviewAsync(ReviewDraft draft, bool isDone) =>
        Ok(await new CreateReview(coordinator).ExecuteAsync(new CreateReviewCommand(OperationId.New(), new ReviewItemId(Guid.NewGuid()), draft, null, isDone), None));

    public async Task GeneralTodoAsync(string text, bool isCompleted)
    {
        var id = new GeneralTodoId(Guid.NewGuid());
        var revision = Ok(await new AddGeneralTodo(coordinator).ExecuteAsync(new AddGeneralTodoCommand(OperationId.New(), id, text), None));
        if (isCompleted)
        {
            Ok(await new SetGeneralTodoCompleted(coordinator).ExecuteAsync(new SetGeneralTodoCompletedCommand(OperationId.New(), id, revision, true), None));
        }
    }

    private static T Ok<T>(AppResult<CommandOutcome<T>> result) =>
        result.IsSuccess ? result.Value.Value : throw new InvalidOperationException($"Fixture command failed: {result.Error}");
}
