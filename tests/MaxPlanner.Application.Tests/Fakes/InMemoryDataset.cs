using MaxPlanner.Application.Common;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.ChangeRequests;
using MaxPlanner.Domain.Checklists;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Tasks;

namespace MaxPlanner.Application.Tests.Fakes;

/// <summary>One committed operation in the ledger: what slice 1d persists in <c>operation_ledger</c>.</summary>
public sealed record LedgerEntry(string OperationKind, long Sequence, object? Value);

/// <summary>
/// The whole dataset as plain maps. Entities are immutable records, so a shallow copy of every map is a
/// complete, isolated working copy for one transaction.
/// </summary>
public sealed class InMemoryDataset
{
    public InMemoryDataset(WorkflowSnapshot workflow) => Workflow = workflow;

    private InMemoryDataset(InMemoryDataset source)
    {
        Workflow = source.Workflow;
        CommitSequence = source.CommitSequence;
        Tasks = new(source.Tasks);
        SubtaskCards = new(source.SubtaskCards);
        ChangeRequests = new(source.ChangeRequests);
        Links = new(source.Links);
        Reviews = new(source.Reviews);
        GeneralTodos = new(source.GeneralTodos);
        Tombstones = new(source.Tombstones);
        Ledger = new(source.Ledger);
    }

    public WorkflowSnapshot Workflow { get; set; }

    /// <summary>The sequence of the last commit; written in the same "transaction" as the data.</summary>
    public long CommitSequence { get; set; }

    public Dictionary<TaskId, TaskItem> Tasks { get; } = [];

    public Dictionary<SubtaskCardId, SubtaskCard> SubtaskCards { get; } = [];

    public Dictionary<ChangeRequestId, ChangeRequest> ChangeRequests { get; } = [];

    public Dictionary<TaskId, CrTaskLink> Links { get; } = [];

    public Dictionary<ReviewItemId, ReviewItem> Reviews { get; } = [];

    public Dictionary<GeneralTodoId, GeneralTodo> GeneralTodos { get; } = [];

    public HashSet<(EntityKind Kind, Guid Id)> Tombstones { get; } = [];

    public Dictionary<OperationId, LedgerEntry> Ledger { get; } = [];

    public InMemoryDataset Clone() => new(this);

    /// <summary>
    /// The relational constraints SQLite enforces at commit (foreign keys, link authority). A violation
    /// means a use case wrote an inconsistent state, so the fake fails loudly instead of committing it.
    /// </summary>
    public void AssertIntegrity()
    {
        foreach (var task in Tasks.Values)
        {
            var linked = Links.GetValueOrDefault(task.Id)?.ChangeRequestId;
            if (task.ChangeRequestId != linked)
            {
                throw new InvalidOperationException($"Task {task.Id} says CR {task.ChangeRequestId} but the link table says {linked}.");
            }
        }

        foreach (var link in Links.Values.Where(l => !ChangeRequests.ContainsKey(l.ChangeRequestId) || !Tasks.ContainsKey(l.TaskId)))
        {
            throw new InvalidOperationException($"Link {link} has a missing endpoint.");
        }

        foreach (var card in SubtaskCards.Values.Where(c => !Tasks.ContainsKey(c.ParentTaskId)))
        {
            throw new InvalidOperationException($"Subtask card {card.Id} has no parent.");
        }
    }
}
