using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;

namespace MaxPlanner.Presentation.Common.Store;

/// <summary>The read models one batch adds or replaces and the keys it removes, per cache.</summary>
internal sealed record ProjectionDelta(
    WorkflowSnapshot? Workflow,
    IReadOnlyList<ChangeRequestReadModel> ChangeRequests,
    IReadOnlyCollection<ChangeRequestId> RemovedChangeRequests,
    IReadOnlyList<TaskReadModel> Tasks,
    IReadOnlyCollection<TaskId> RemovedTasks,
    IReadOnlyList<SubtaskCardReadModel> SubtaskCards,
    IReadOnlyCollection<SubtaskCardId> RemovedSubtaskCards,
    IReadOnlyList<ReviewItem> ReviewItems,
    IReadOnlyCollection<ReviewItemId> RemovedReviewItems,
    IReadOnlyList<GeneralTodo> GeneralTodos,
    IReadOnlyCollection<GeneralTodoId> RemovedGeneralTodos);

/// <summary>
/// The domain records behind the projection, plus the CR-to-task and parent-to-card indexes that let a
/// change set recompose only the read models it affects (ADR 0010, T-13).
/// </summary>
/// <remarks>
/// Not thread-safe: owned by <see cref="WorkspaceProjectionStore"/> and used only on its scheduler.
/// A change set that contradicts the current relationships (a link to an unknown CR, a card whose
/// parent is gone) throws <see cref="InvalidOperationException"/>; the store then resynchronizes from a
/// snapshot instead of guessing a repair.
/// </remarks>
internal sealed class WorkspaceRecords
{
    private readonly ReadModelComposer _composer;
    private readonly Dictionary<TaskId, TaskItem> _tasks = [];
    private readonly Dictionary<SubtaskCardId, SubtaskCard> _cards = [];
    private readonly Dictionary<ChangeRequestId, ChangeRequest> _changeRequests = [];
    private readonly Dictionary<TaskId, CrTaskLink> _links = [];
    private readonly Dictionary<ChangeRequestId, HashSet<TaskId>> _tasksByCr = [];
    private readonly Dictionary<TaskId, HashSet<SubtaskCardId>> _cardsByParent = [];
    private readonly HashSet<ReviewItemId> _reviewIds = [];
    private readonly HashSet<GeneralTodoId> _todoIds = [];
    private WorkflowSnapshot? _workflow;

    public WorkspaceRecords(ReadModelComposer composer) => _composer = composer;

    public DateOnly Today { get; private set; }

    /// <summary>Replaces every record with the snapshot; the delta removes whatever the snapshot no longer has.</summary>
    public ProjectionDelta Load(ReadSnapshot snapshot, DateOnly today)
    {
        var previousTasks = _tasks.Keys.ToHashSet();
        var previousCards = _cards.Keys.ToHashSet();
        var previousCrs = _changeRequests.Keys.ToHashSet();
        var previousReviews = _reviewIds.ToHashSet();
        var previousTodos = _todoIds.ToHashSet();

        _tasks.Clear();
        _cards.Clear();
        _changeRequests.Clear();
        _links.Clear();
        _tasksByCr.Clear();
        _cardsByParent.Clear();
        _reviewIds.Clear();
        _todoIds.Clear();
        _workflow = snapshot.Workflow;
        Today = today;

        foreach (var changeRequest in snapshot.ChangeRequests)
        {
            _changeRequests[changeRequest.Id] = changeRequest;
        }

        foreach (var link in snapshot.Links)
        {
            AddLink(link);
        }

        foreach (var task in snapshot.Tasks)
        {
            _tasks[task.Id] = task;
        }

        foreach (var card in snapshot.SubtaskCards)
        {
            PutCard(card);
        }

        _reviewIds.UnionWith(snapshot.ReviewItems.Select(r => r.Id));
        _todoIds.UnionWith(snapshot.GeneralTodos.Select(t => t.Id));

        previousTasks.ExceptWith(_tasks.Keys);
        previousCards.ExceptWith(_cards.Keys);
        previousCrs.ExceptWith(_changeRequests.Keys);
        previousReviews.ExceptWith(_reviewIds);
        previousTodos.ExceptWith(_todoIds);

        return new ProjectionDelta(
            snapshot.Workflow,
            ComposeChangeRequests(_changeRequests.Keys),
            previousCrs,
            ComposeTasks(_tasks.Keys),
            previousTasks,
            ComposeCards(_cards.Keys),
            previousCards,
            snapshot.ReviewItems,
            previousReviews,
            snapshot.GeneralTodos,
            previousTodos);
    }

    /// <summary>Applies one change set and recomposes every read model whose inputs it touched.</summary>
    public ProjectionDelta Apply(CommittedChangeSet changeSet)
    {
        var tasks = new HashSet<TaskId>();
        var cards = new HashSet<SubtaskCardId>();
        var changeRequests = new HashSet<ChangeRequestId>();
        var editedChangeRequests = new HashSet<ChangeRequestId>();

        foreach (var link in changeSet.RemovedLinks)
        {
            RemoveLink(link);
            changeRequests.Add(link.ChangeRequestId);
        }

        foreach (var id in changeSet.DeletedSubtaskCards)
        {
            if (_cards.Remove(id, out var card))
            {
                Unindex(card);
                tasks.Add(card.ParentTaskId);
            }
        }

        foreach (var id in changeSet.DeletedTasks)
        {
            _tasks.Remove(id);
        }

        foreach (var id in changeSet.DeletedChangeRequests)
        {
            _changeRequests.Remove(id);
        }

        foreach (var link in changeSet.AddedLinks)
        {
            AddLink(link);
            changeRequests.Add(link.ChangeRequestId);
            tasks.Add(link.TaskId);
        }

        foreach (var changeRequest in changeSet.UpsertedChangeRequests)
        {
            _changeRequests[changeRequest.Id] = changeRequest;
            changeRequests.Add(changeRequest.Id);
            editedChangeRequests.Add(changeRequest.Id);
        }

        foreach (var task in changeSet.UpsertedTasks)
        {
            _tasks[task.Id] = task;
            tasks.Add(task.Id);
        }

        foreach (var card in changeSet.UpsertedSubtaskCards)
        {
            if (!_cards.TryGetValue(card.Id, out var previous) || previous.ParentTaskId != card.ParentTaskId)
            {
                // The parent's Subtask card count changes.
                tasks.Add(card.ParentTaskId);
            }

            PutCard(card);
            cards.Add(card.Id);
        }

        _reviewIds.ExceptWith(changeSet.DeletedReviewItems);
        _reviewIds.UnionWith(changeSet.UpsertedReviewItems.Select(r => r.Id));
        _todoIds.ExceptWith(changeSet.DeletedGeneralTodos);
        _todoIds.UnionWith(changeSet.UpsertedGeneralTodos.Select(t => t.Id));

        EnsureNoOrphans(changeSet);

        if (changeSet.Workflow is not null)
        {
            // Lookup names and flags feed every read model, so a workflow change recomposes everything.
            _workflow = changeSet.Workflow;
            tasks.UnionWith(_tasks.Keys);
            cards.UnionWith(_cards.Keys);
            changeRequests.UnionWith(_changeRequests.Keys);
        }
        else
        {
            // T-13: a CR edit changes the inherited values of every linked task, and through each task its Subtask cards.
            foreach (var id in editedChangeRequests)
            {
                if (_tasksByCr.TryGetValue(id, out var linked))
                {
                    tasks.UnionWith(linked);
                }
            }

            foreach (var id in tasks)
            {
                if (_cardsByParent.TryGetValue(id, out var owned))
                {
                    cards.UnionWith(owned);
                }
            }
        }

        tasks.ExceptWith(changeSet.DeletedTasks);
        cards.ExceptWith(changeSet.DeletedSubtaskCards);
        changeRequests.ExceptWith(changeSet.DeletedChangeRequests);
        tasks.IntersectWith(_tasks.Keys);
        changeRequests.IntersectWith(_changeRequests.Keys);

        return new ProjectionDelta(
            changeSet.Workflow,
            ComposeChangeRequests(changeRequests),
            changeSet.DeletedChangeRequests,
            ComposeTasks(tasks),
            changeSet.DeletedTasks,
            ComposeCards(cards),
            changeSet.DeletedSubtaskCards,
            changeSet.UpsertedReviewItems,
            changeSet.DeletedReviewItems,
            changeSet.UpsertedGeneralTodos,
            changeSet.DeletedGeneralTodos);
    }

    /// <summary>B-15: overdue flags are only valid for one local day, so a new day recomposes every CR.</summary>
    public ProjectionDelta RecomposeForDay(DateOnly today)
    {
        Today = today;
        return new ProjectionDelta(null, ComposeChangeRequests(_changeRequests.Keys), [], [], [], [], [], [], [], [], []);
    }

    private List<ChangeRequestReadModel> ComposeChangeRequests(IEnumerable<ChangeRequestId> ids) =>
        [.. ids.Select(id => _composer.ComposeChangeRequest(
            _changeRequests[id], _tasksByCr.TryGetValue(id, out var linked) ? linked.Count : 0, Today, Workflow))];

    private List<TaskReadModel> ComposeTasks(IEnumerable<TaskId> ids) =>
        [.. ids.Select(id =>
        {
            var task = _tasks[id];
            return _composer.ComposeTask(task, LinkedChangeRequest(task), _cardsByParent.TryGetValue(id, out var owned) ? owned.Count : 0, Workflow);
        })];

    private List<SubtaskCardReadModel> ComposeCards(IEnumerable<SubtaskCardId> ids) =>
        [.. ids.Select(id =>
        {
            var card = _cards[id];
            var parent = _tasks.GetValueOrDefault(card.ParentTaskId)
                ?? throw new InvalidOperationException($"Subtask card {id} references a task the projection does not have.");
            return _composer.ComposeSubtaskCard(card, parent, LinkedChangeRequest(parent), Workflow);
        })];

    private WorkflowSnapshot Workflow => _workflow ?? throw new InvalidOperationException("No snapshot has been loaded.");

    private ChangeRequest? LinkedChangeRequest(TaskItem task)
    {
        var linkedId = _links.TryGetValue(task.Id, out var link) ? link.ChangeRequestId : (ChangeRequestId?)null;
        if (linkedId != task.ChangeRequestId)
        {
            throw new InvalidOperationException($"Task {task.Id} and its link row disagree about the linked CR.");
        }

        return linkedId is { } id
            ? _changeRequests.GetValueOrDefault(id) ?? throw new InvalidOperationException($"Task {task.Id} is linked to a CR the projection does not have.")
            : null;
    }

    private void AddLink(CrTaskLink link)
    {
        // ADR 0006: links are permanent, so a second link for the same task can only mean missed changes.
        if (_links.TryGetValue(link.TaskId, out var existing) && existing.ChangeRequestId != link.ChangeRequestId)
        {
            throw new InvalidOperationException($"Task {link.TaskId} is already linked to another CR.");
        }

        _links[link.TaskId] = link;
        Index(_tasksByCr, link.ChangeRequestId, link.TaskId);
    }

    private void RemoveLink(CrTaskLink link)
    {
        _links.Remove(link.TaskId);
        Unindex(_tasksByCr, link.ChangeRequestId, link.TaskId);
    }

    private void PutCard(SubtaskCard card)
    {
        if (_cards.TryGetValue(card.Id, out var previous))
        {
            Unindex(previous);
        }

        _cards[card.Id] = card;
        Index(_cardsByParent, card.ParentTaskId, card.Id);
    }

    private void Unindex(SubtaskCard card) => Unindex(_cardsByParent, card.ParentTaskId, card.Id);

    private void EnsureNoOrphans(CommittedChangeSet changeSet)
    {
        foreach (var id in changeSet.DeletedTasks)
        {
            if (_links.ContainsKey(id) || _cardsByParent.ContainsKey(id))
            {
                throw new InvalidOperationException($"Task {id} was deleted without its link or Subtask cards.");
            }
        }

        foreach (var id in changeSet.DeletedChangeRequests)
        {
            if (_tasksByCr.ContainsKey(id))
            {
                throw new InvalidOperationException($"CR {id} was deleted without its linked tasks.");
            }
        }
    }

    private static void Index<TParent, TChild>(Dictionary<TParent, HashSet<TChild>> index, TParent parent, TChild child)
        where TParent : notnull
    {
        if (!index.TryGetValue(parent, out var children))
        {
            children = [];
            index[parent] = children;
        }

        children.Add(child);
    }

    private static void Unindex<TParent, TChild>(Dictionary<TParent, HashSet<TChild>> index, TParent parent, TChild child)
        where TParent : notnull
    {
        if (index.TryGetValue(parent, out var children) && children.Remove(child) && children.Count == 0)
        {
            index.Remove(parent);
        }
    }
}
