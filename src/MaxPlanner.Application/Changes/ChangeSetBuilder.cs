using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;

namespace MaxPlanner.Application.Changes;

/// <summary>
/// Collects the changes of one write transaction. Use cases record every write here next to the
/// store call; the coordinator builds the <see cref="CommittedChangeSet"/> only after commit.
/// </summary>
/// <remarks>
/// Owned by a single transaction and not thread-safe. A later upsert of the same record replaces the
/// earlier one and a delete cancels an earlier upsert, so each record appears once with its final state.
/// </remarks>
public sealed class ChangeSetBuilder
{
    private readonly Keyed<TaskId, TaskItem> _tasks = new();
    private readonly Keyed<SubtaskCardId, SubtaskCard> _subtaskCards = new();
    private readonly Keyed<ChangeRequestId, ChangeRequest> _changeRequests = new();
    private readonly Keyed<ReviewItemId, ReviewItem> _reviewItems = new();
    private readonly Keyed<GeneralTodoId, GeneralTodo> _generalTodos = new();
    private readonly List<CrTaskLink> _addedLinks = [];
    private readonly List<CrTaskLink> _removedLinks = [];
    private WorkflowSnapshot? _workflow;
    private bool _requiresFullReload;

    public bool IsEmpty =>
        _tasks.IsEmpty && _subtaskCards.IsEmpty && _changeRequests.IsEmpty && _reviewItems.IsEmpty && _generalTodos.IsEmpty
        && _addedLinks.Count == 0 && _removedLinks.Count == 0 && _workflow is null && !_requiresFullReload;

    public void UpsertTask(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _tasks.Upsert(task.Id, task);
    }

    public void DeleteTask(TaskId id) => _tasks.Delete(id);

    public void UpsertSubtaskCard(SubtaskCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        _subtaskCards.Upsert(card.Id, card);
    }

    public void DeleteSubtaskCard(SubtaskCardId id) => _subtaskCards.Delete(id);

    public void UpsertChangeRequest(ChangeRequest changeRequest)
    {
        ArgumentNullException.ThrowIfNull(changeRequest);
        _changeRequests.Upsert(changeRequest.Id, changeRequest);
    }

    public void DeleteChangeRequest(ChangeRequestId id) => _changeRequests.Delete(id);

    public void AddLink(CrTaskLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        _addedLinks.Add(link);
    }

    public void RemoveLink(CrTaskLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        _removedLinks.Add(link);
    }

    public void UpsertReviewItem(ReviewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _reviewItems.Upsert(item.Id, item);
    }

    public void DeleteReviewItem(ReviewItemId id) => _reviewItems.Delete(id);

    public void UpsertGeneralTodo(GeneralTodo todo)
    {
        ArgumentNullException.ThrowIfNull(todo);
        _generalTodos.Upsert(todo.Id, todo);
    }

    public void DeleteGeneralTodo(GeneralTodoId id) => _generalTodos.Delete(id);

    public void ReplaceWorkflow(WorkflowSnapshot workflow) => _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));

    public void RequireFullReload() => _requiresFullReload = true;

    /// <summary>Called by the mutation coordinator after commit, with the sequence it wrote in the transaction.</summary>
    public CommittedChangeSet Build(long sequence, OperationId operationId, string operationKind, DateTimeOffset committedAtUtc) => new()
    {
        Sequence = sequence,
        OperationId = operationId,
        OperationKind = operationKind,
        CommittedAtUtc = committedAtUtc,
        UpsertedTasks = _tasks.Upserts(),
        DeletedTasks = _tasks.Deletes(),
        UpsertedSubtaskCards = _subtaskCards.Upserts(),
        DeletedSubtaskCards = _subtaskCards.Deletes(),
        UpsertedChangeRequests = _changeRequests.Upserts(),
        DeletedChangeRequests = _changeRequests.Deletes(),
        AddedLinks = [.. _addedLinks],
        RemovedLinks = [.. _removedLinks],
        UpsertedReviewItems = _reviewItems.Upserts(),
        DeletedReviewItems = _reviewItems.Deletes(),
        UpsertedGeneralTodos = _generalTodos.Upserts(),
        DeletedGeneralTodos = _generalTodos.Deletes(),
        Workflow = _workflow,
        RequiresFullReload = _requiresFullReload,
    };

    private sealed class Keyed<TKey, TValue>
        where TKey : notnull
    {
        private readonly OrderedDictionary<TKey, TValue> _upserts = [];
        private readonly OrderedDictionary<TKey, bool> _deletes = [];

        public bool IsEmpty => _upserts.Count == 0 && _deletes.Count == 0;

        public void Upsert(TKey key, TValue value)
        {
            _deletes.Remove(key);
            _upserts[key] = value;
        }

        public void Delete(TKey key)
        {
            _upserts.Remove(key);
            _deletes[key] = true;
        }

        public IReadOnlyList<TValue> Upserts() => [.. _upserts.Values];

        public IReadOnlyList<TKey> Deletes() => [.. _deletes.Keys];
    }
}
