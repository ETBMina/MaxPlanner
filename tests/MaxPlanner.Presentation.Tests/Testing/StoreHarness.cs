using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Changes;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Store;

namespace MaxPlanner.Presentation.Tests.Testing;

/// <summary>A projection store over fake ports and a manual scheduler, recording every state and commit.</summary>
internal sealed class StoreHarness : IDisposable
{
    public StoreHarness()
    {
        Store = new WorkspaceProjectionStore(Feed, Snapshots, new ReadModelComposer(), Clock, Days, Scheduler);
        Store.Committed.Subscribe(Commits.Add);
        Store.State.Subscribe(States.Add);
    }

    public ManualScheduler Scheduler { get; } = new();

    public FakeChangeFeed Feed { get; } = new();

    public FakeSnapshotSource Snapshots { get; } = new();

    public FakeDayBoundarySource Days { get; } = new();

    public TestClock Clock { get; } = new(Records.Now);

    public WorkspaceProjectionStore Store { get; }

    public List<ProjectionCommit> Commits { get; } = [];

    public List<ProjectionState> States { get; } = [];

    public ProjectionStatus Status => Store.CurrentState.Status;

    /// <summary>Starts the store and completes its first load with <paramref name="snapshot"/>.</summary>
    public StoreHarness StartWith(ReadSnapshot snapshot)
    {
        Store.Start();
        Scheduler.RunAll();
        Snapshots.Complete(snapshot);
        Scheduler.RunAll();
        return this;
    }

    public void Publish(CommittedChangeSet changeSet)
    {
        Feed.Publish(changeSet);
        Scheduler.RunAll();
    }

    public void CompleteLoad(ReadSnapshot snapshot)
    {
        Snapshots.Complete(snapshot);
        Scheduler.RunAll();
    }

    public TaskReadModel Task(TaskId id) => Store.Tasks.Lookup(id).Value;

    public SubtaskCardReadModel Card(SubtaskCardId id) => Store.SubtaskCards.Lookup(id).Value;

    public ChangeRequestReadModel Cr(ChangeRequestId id) => Store.ChangeRequests.Lookup(id).Value;

    public void Dispose()
    {
        Store.Dispose();
        Days.Dispose();
    }
}
