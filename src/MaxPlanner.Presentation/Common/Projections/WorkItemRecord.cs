using System.Reactive.Linq;
using DynamicData;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Store;

namespace MaxPlanner.Presentation.Common.Projections;

/// <summary>
/// A task or a Subtask card as one list record, because the Board columns and the Grid show both kinds
/// together (B-07, B-16.5). Exactly one of <see cref="Task"/> and <see cref="Card"/> is set.
/// </summary>
public sealed record WorkItemRecord
{
    private WorkItemRecord(TaskReadModel? task, SubtaskCardReadModel? card)
    {
        Task = task;
        Card = card;
    }

    public TaskReadModel? Task { get; }

    public SubtaskCardReadModel? Card { get; }

    /// <summary>The record's ID. Task and Subtask card IDs are GUIDs, so they never collide.</summary>
    public Guid Id => Task?.Id.Value ?? Card!.Id.Value;

    public BoardCardKind Kind => Task is not null ? BoardCardKind.Task : BoardCardKind.SubtaskCard;

    public LookupRef<TaskStatusId> Status => Task?.Status ?? Card!.Status;

    public long Rank => Task?.Rank ?? Card!.Rank;

    public long Revision => Task?.Revision ?? Card!.Revision;

    public DateTimeOffset CreatedAtUtc => Task?.CreatedAtUtc ?? Card!.CreatedAtUtc;

    public InheritedCrValues Cr => Task?.Cr ?? Card!.Cr;

    /// <summary>B-05, B-16.6: archived tasks and the cards of archived parents are hidden from active views.</summary>
    public bool IsActive => Task is { } task ? !task.IsArchived : !Card!.IsParentArchived;

    public static WorkItemRecord From(TaskReadModel task) => new(task ?? throw new ArgumentNullException(nameof(task)), null);

    public static WorkItemRecord From(SubtaskCardReadModel card) => new(null, card ?? throw new ArgumentNullException(nameof(card)));

    /// <summary>
    /// Tasks and Subtask cards from the store as one change stream keyed by record ID. It emits on the
    /// store scheduler; subscribe it through a <see cref="ProjectionView{TModel, TKey, TViewModel}"/>.
    /// </summary>
    public static IObservable<IChangeSet<WorkItemRecord, Guid>> Connect(WorkspaceProjectionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var tasks = store.Tasks.Connect().Transform(From).ChangeKey(record => record.Id);
        var cards = store.SubtaskCards.Connect().Transform(From).ChangeKey(record => record.Id);
        return tasks.Or(cards);
    }
}
