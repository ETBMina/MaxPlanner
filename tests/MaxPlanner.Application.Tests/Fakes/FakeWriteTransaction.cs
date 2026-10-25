using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Tests.Fakes;

/// <summary>
/// Stores over a transaction's working copy. They enforce the same primary-key, unique, and foreign-key
/// constraints as the SQLite schema by throwing, so a use case that skips a business check fails here too.
/// Updates follow the same revision rule as the SQLite stores (<see cref="FakeRevisionGuard"/>).
/// </summary>
internal sealed class FakeWriteTransaction : IWriteTransaction
{
    public FakeWriteTransaction(InMemoryDataset data, TimeProvider time, Action<string> beforeWrite)
    {
        UtcNow = time.GetUtcNow();
        LocalToday = time.LocalToday();
        Tasks = new TaskStore(data, beforeWrite);
        SubtaskCards = new SubtaskCardStore(data, beforeWrite);
        ChangeRequests = new ChangeRequestStore(data, beforeWrite);
        Links = new LinkStore(data, beforeWrite);
        Reviews = new ReviewStore(data, beforeWrite);
        GeneralTodos = new GeneralTodoStore(data, beforeWrite);
        Workflow = new WorkflowReader(data);
        Lookups = new LookupStore(data, beforeWrite);
        Tombstones = new TombstoneStore(data, beforeWrite);
    }

    public DateTimeOffset UtcNow { get; }

    public DateOnly LocalToday { get; }

    public ChangeSetBuilder Changes { get; } = new();

    public ITaskStore Tasks { get; }

    public ISubtaskCardStore SubtaskCards { get; }

    public IChangeRequestStore ChangeRequests { get; }

    public ILinkStore Links { get; }

    public IReviewStore Reviews { get; }

    public IGeneralTodoStore GeneralTodos { get; }

    public IWorkflowReader Workflow { get; }

    public ILookupStore Lookups { get; }

    public ITombstoneStore Tombstones { get; }

    /// <summary>Compile fix for the Phase 13 port: transaction-local only; the import tests of Phase 19 extend the fake.</summary>
    public IImportLedgerStore ImportLedger { get; } = new ImportLedgerStore();

    private static InvalidOperationException Constraint(string message) => new($"Constraint violated: {message}");

    private sealed class TaskStore(InMemoryDataset data, Action<string> beforeWrite) : ITaskStore
    {
        private readonly FakeRevisionGuard _revisions = new("task");

        public TaskItem? Find(TaskId id) => _revisions.Seen(data.Tasks.GetValueOrDefault(id), t => t.Id.Value, t => t.Revision);

        public IReadOnlyList<TaskItem> ListAll() => [.. data.Tasks.Values.Select(t => _revisions.Seen(t, x => x.Id.Value, x => x.Revision))];

        public void Insert(TaskItem task)
        {
            beforeWrite("tasks.insert");
            if (!data.Tasks.TryAdd(task.Id, task))
            {
                throw Constraint($"task {task.Id} already exists");
            }

            _revisions.Seen(task, t => t.Id.Value, t => t.Revision);
        }

        public void Update(TaskItem task)
        {
            beforeWrite("tasks.update");
            _revisions.CheckUpdate(task.Id.Value, task.Revision);
            data.Tasks[task.Id] = data.Tasks.ContainsKey(task.Id) ? task : throw Constraint($"task {task.Id} does not exist");
        }

        public void Delete(TaskId id)
        {
            beforeWrite("tasks.delete");
            if (data.Links.ContainsKey(id) || data.SubtaskCards.Values.Any(c => c.ParentTaskId == id))
            {
                throw Constraint($"task {id} still has a link or Subtask cards");
            }

            if (!data.Tasks.Remove(id))
            {
                throw Constraint($"task {id} does not exist");
            }

            _revisions.Forget(id.Value);
        }

        public long? MaxBoardRank(TaskStatusId status) => Column(status).Select(c => (long?)c.Rank).Max();

        public long? MinBoardRank(TaskStatusId status) => Column(status).Select(c => (long?)c.Rank).Min();

        public IReadOnlyList<(BoardCardKey Key, long Rank)> ListBoardColumn(TaskStatusId status) =>
            [.. Column(status).OrderBy(c => c.Rank).ThenBy(c => c.Key.Id)];

        private IEnumerable<(BoardCardKey Key, long Rank)> Column(TaskStatusId status) =>
            data.Tasks.Values
                .Where(t => t.StatusId == status && !t.IsArchived)
                .Select(t => (new BoardCardKey(BoardCardKind.Task, t.Id.Value), t.Rank))
                .Concat(data.SubtaskCards.Values
                    .Where(c => c.StatusId == status && data.Tasks.TryGetValue(c.ParentTaskId, out var parent) && !parent.IsArchived)
                    .Select(c => (new BoardCardKey(BoardCardKind.SubtaskCard, c.Id.Value), c.Rank)));
    }

    private sealed class SubtaskCardStore(InMemoryDataset data, Action<string> beforeWrite) : ISubtaskCardStore
    {
        private readonly FakeRevisionGuard _revisions = new("Subtask card");

        public SubtaskCard? Find(SubtaskCardId id) => _revisions.Seen(data.SubtaskCards.GetValueOrDefault(id), c => c.Id.Value, c => c.Revision);

        public IReadOnlyList<SubtaskCard> ListAll() => [.. data.SubtaskCards.Values.Select(c => _revisions.Seen(c, x => x.Id.Value, x => x.Revision))];

        public void Insert(SubtaskCard card)
        {
            beforeWrite("subtask-cards.insert");
            if (!data.Tasks.ContainsKey(card.ParentTaskId) || !data.SubtaskCards.TryAdd(card.Id, card))
            {
                throw Constraint($"Subtask card {card.Id} exists or has no parent");
            }

            _revisions.Seen(card, c => c.Id.Value, c => c.Revision);
        }

        public void Update(SubtaskCard card)
        {
            beforeWrite("subtask-cards.update");
            _revisions.CheckUpdate(card.Id.Value, card.Revision);
            data.SubtaskCards[card.Id] = data.SubtaskCards.ContainsKey(card.Id) ? card : throw Constraint($"Subtask card {card.Id} does not exist");
        }

        public void Delete(SubtaskCardId id)
        {
            beforeWrite("subtask-cards.delete");
            if (!data.SubtaskCards.Remove(id))
            {
                throw Constraint($"Subtask card {id} does not exist");
            }

            _revisions.Forget(id.Value);
        }

        public IReadOnlyList<SubtaskCard> ListByParent(TaskId parentTaskId) =>
            [.. data.SubtaskCards.Values.Where(c => c.ParentTaskId == parentTaskId).OrderBy(c => c.Rank).ThenBy(c => c.Id.Value)
                .Select(c => _revisions.Seen(c, card => card.Id.Value, card => card.Revision))];
    }

    private sealed class ChangeRequestStore(InMemoryDataset data, Action<string> beforeWrite) : IChangeRequestStore
    {
        private readonly FakeRevisionGuard _revisions = new("CR");

        public ChangeRequest? Find(ChangeRequestId id) => _revisions.Seen(data.ChangeRequests.GetValueOrDefault(id), c => c.Id.Value, c => c.Revision);

        public IReadOnlyList<ChangeRequest> ListAll() => [.. data.ChangeRequests.Values.Select(c => _revisions.Seen(c, x => x.Id.Value, x => x.Revision))];

        public ChangeRequest? FindByNormalizedNumber(string normalizedNumber) =>
            _revisions.Seen(ByNumber(normalizedNumber), c => c.Id.Value, c => c.Revision);

        public void Insert(ChangeRequest changeRequest)
        {
            beforeWrite("change-requests.insert");
            if (ByNumber(changeRequest.Number.Normalized) is not null || !data.ChangeRequests.TryAdd(changeRequest.Id, changeRequest))
            {
                throw Constraint($"CR {changeRequest.Id} or number {changeRequest.Number} already exists");
            }

            _revisions.Seen(changeRequest, c => c.Id.Value, c => c.Revision);
        }

        public void Update(ChangeRequest changeRequest)
        {
            beforeWrite("change-requests.update");
            _revisions.CheckUpdate(changeRequest.Id.Value, changeRequest.Revision);
            if (ByNumber(changeRequest.Number.Normalized) is { } other && other.Id != changeRequest.Id)
            {
                throw Constraint($"number {changeRequest.Number} is used by CR {other.Id}");
            }

            data.ChangeRequests[changeRequest.Id] = data.ChangeRequests.ContainsKey(changeRequest.Id)
                ? changeRequest
                : throw Constraint($"CR {changeRequest.Id} does not exist");
        }

        public void Delete(ChangeRequestId id)
        {
            beforeWrite("change-requests.delete");
            if (data.Links.Values.Any(l => l.ChangeRequestId == id))
            {
                throw Constraint($"CR {id} still has links");
            }

            if (!data.ChangeRequests.Remove(id))
            {
                throw Constraint($"CR {id} does not exist");
            }

            _revisions.Forget(id.Value);
        }

        private ChangeRequest? ByNumber(string normalizedNumber) =>
            data.ChangeRequests.Values.SingleOrDefault(c => c.Number.Normalized == normalizedNumber);
    }

    private sealed class LinkStore(InMemoryDataset data, Action<string> beforeWrite) : ILinkStore
    {
        public CrTaskLink? FindByTask(TaskId taskId) => data.Links.GetValueOrDefault(taskId);

        public void Insert(CrTaskLink link)
        {
            beforeWrite("links.insert");
            if (!data.Tasks.ContainsKey(link.TaskId) || !data.ChangeRequests.ContainsKey(link.ChangeRequestId) || !data.Links.TryAdd(link.TaskId, link))
            {
                throw Constraint($"link for task {link.TaskId} exists or has a missing endpoint");
            }
        }

        public IReadOnlyList<TaskId> ListTaskIds(ChangeRequestId changeRequestId) =>
            [.. data.Links.Values.Where(l => l.ChangeRequestId == changeRequestId).Select(l => l.TaskId)];

        public void DeleteForTask(TaskId taskId)
        {
            beforeWrite("links.delete");
            if (!data.Links.Remove(taskId))
            {
                throw Constraint($"task {taskId} has no link");
            }
        }
    }

    private sealed class ReviewStore(InMemoryDataset data, Action<string> beforeWrite) : IReviewStore
    {
        private readonly FakeRevisionGuard _revisions = new("review item");

        public ReviewItem? Find(ReviewItemId id) => _revisions.Seen(data.Reviews.GetValueOrDefault(id), r => r.Id.Value, r => r.Revision);

        public IReadOnlyList<ReviewItem> ListAll() => [.. data.Reviews.Values.Select(r => _revisions.Seen(r, x => x.Id.Value, x => x.Revision))];

        public void Insert(ReviewItem item)
        {
            beforeWrite("reviews.insert");
            if (!data.Reviews.TryAdd(item.Id, item))
            {
                throw Constraint($"review {item.Id} already exists");
            }

            _revisions.Seen(item, r => r.Id.Value, r => r.Revision);
        }

        public void Update(ReviewItem item)
        {
            beforeWrite("reviews.update");
            _revisions.CheckUpdate(item.Id.Value, item.Revision);
            data.Reviews[item.Id] = data.Reviews.ContainsKey(item.Id) ? item : throw Constraint($"review {item.Id} does not exist");
        }

        public void Delete(ReviewItemId id)
        {
            beforeWrite("reviews.delete");
            if (!data.Reviews.Remove(id))
            {
                throw Constraint($"review {id} does not exist");
            }

            _revisions.Forget(id.Value);
        }

        public long? MaxRank() => data.Reviews.Values.Select(r => (long?)r.Rank).Max();

        public IReadOnlyList<(ReviewItemId Id, long Rank)> ListOrder() => [.. data.Reviews.Values.Select(r => (r.Id, r.Rank))];
    }

    private sealed class GeneralTodoStore(InMemoryDataset data, Action<string> beforeWrite) : IGeneralTodoStore
    {
        private readonly FakeRevisionGuard _revisions = new("General to-do");

        public GeneralTodo? Find(GeneralTodoId id) => _revisions.Seen(data.GeneralTodos.GetValueOrDefault(id), t => t.Id.Value, t => t.Revision);

        public void Insert(GeneralTodo todo)
        {
            beforeWrite("general-todos.insert");
            if (!data.GeneralTodos.TryAdd(todo.Id, todo))
            {
                throw Constraint($"to-do {todo.Id} already exists");
            }

            _revisions.Seen(todo, t => t.Id.Value, t => t.Revision);
        }

        public void Update(GeneralTodo todo)
        {
            beforeWrite("general-todos.update");
            _revisions.CheckUpdate(todo.Id.Value, todo.Revision);
            data.GeneralTodos[todo.Id] = data.GeneralTodos.ContainsKey(todo.Id) ? todo : throw Constraint($"to-do {todo.Id} does not exist");
        }

        public void Delete(GeneralTodoId id)
        {
            beforeWrite("general-todos.delete");
            if (!data.GeneralTodos.Remove(id))
            {
                throw Constraint($"to-do {id} does not exist");
            }

            _revisions.Forget(id.Value);
        }

        public long? MaxRank() => data.GeneralTodos.Values.Select(t => (long?)t.Rank).Max();

        public IReadOnlyList<(GeneralTodoId Id, long Rank)> ListOrder() => [.. data.GeneralTodos.Values.Select(t => (t.Id, t.Rank))];
    }

    private sealed class WorkflowReader(InMemoryDataset data) : IWorkflowReader
    {
        public WorkflowSnapshot Read() => data.Workflow;
    }

    private sealed class LookupStore(InMemoryDataset data, Action<string> beforeWrite) : ILookupStore
    {
        public void InsertPerson(PersonDefinition person)
        {
            beforeWrite("people.insert");
            if (data.Workflow.People.Any(p => p.Id == person.Id || p.Name.Normalized == person.Name.Normalized))
            {
                throw Constraint($"person {person.Id} or name {person.Name} already exists");
            }

            data.Workflow = data.Workflow with { People = [.. data.Workflow.People, person] };
        }

        public void InsertTag(TagDefinition tag)
        {
            beforeWrite("tags.insert");
            if (data.Workflow.Tags.Any(t => t.Id == tag.Id || t.Name.Normalized == tag.Name.Normalized))
            {
                throw Constraint($"tag {tag.Id} or name {tag.Name} already exists");
            }

            data.Workflow = data.Workflow with { Tags = [.. data.Workflow.Tags, tag] };
        }

        public void SetDefaultCrOwner(PersonId ownerId)
        {
            beforeWrite("app_metadata.default-cr-owner");
            if (data.Workflow.FindPerson(ownerId) is null)
            {
                throw Constraint($"person {ownerId} does not exist");
            }

            data.Workflow = data.Workflow with { Defaults = data.Workflow.Defaults with { DefaultCrOwnerId = ownerId } };
        }

        // Settings changes (Phase 10). The fake keeps no referential checks for lookups yet; Phase 19 adds the
        // foreign-key checks the SQLite store gets from the schema when it adds the Settings use case tests.
        public void SaveWorkflow(WorkflowSnapshot workflow)
        {
            beforeWrite("workflow.save");
            data.Workflow = workflow;
        }
    }

    private sealed class TombstoneStore(InMemoryDataset data, Action<string> beforeWrite) : ITombstoneStore
    {
        public void Add(EntityKind kind, Guid id, DateTimeOffset deletedAtUtc)
        {
            beforeWrite("tombstones.insert");
            data.Tombstones.Add((kind, id));
        }

        public bool Contains(EntityKind kind, Guid id) => data.Tombstones.Contains((kind, id));
    }

    private sealed class ImportLedgerStore : IImportLedgerStore
    {
        private readonly Dictionary<(string, string, string), Guid> _rows = [];

        public Guid? Find(string sourceNamespace, string entityKind, string sourceKey) =>
            _rows.TryGetValue((sourceNamespace, entityKind, sourceKey), out var id) ? id : null;

        public void Insert(string sourceNamespace, string entityKind, string sourceKey, Guid recordId)
        {
            if (!_rows.TryAdd((sourceNamespace, entityKind, sourceKey), recordId))
            {
                throw Constraint("import_ledger primary key");
            }
        }
    }
}
