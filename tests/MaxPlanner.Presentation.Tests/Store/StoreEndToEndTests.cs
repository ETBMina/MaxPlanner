using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Storage;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Presentation.Tests.Store;

/// <summary>
/// The store over the real SQLite dataset, coordinator, and use cases, on its own background event
/// loop as in production. The dataset lives in a temp folder whose path has a space and non-ASCII text.
/// </summary>
[Trait("Req", "T-07")]
public sealed class StoreEndToEndTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MaxPlanner Tëst Store", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(Testing.Records.Now);
    private readonly List<IAsyncDisposable> _open = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Req", "T-13")]
    [Trait("Req", "AC-22")]
    public async Task The_store_follows_real_commits_and_reloads_the_same_state_after_reopening()
    {
        var paths = DataPaths.ForDirectory(_root);
        var created = await SqliteDataset.CreateNewAsync(paths, new SqliteDatasetOptions(_clock));
        created.Error.Should().BeNull();
        var dataset = Track(created.Dataset!);
        var alice = AddPerson(paths, "Alice");
        var store = await StartStoreAsync(dataset);

        var workflow = store.CurrentWorkflow!;
        var retailCards = workflow.Squads.Single(s => s.Name.Display == "Retail Cards").Id;
        var inProgress = workflow.TaskStatuses.Single(s => s.Name.Display == "In Progress").Id;

        var taskId = new TaskId(Guid.NewGuid());
        await CommitAsync(store, new CreateTask(dataset.Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), taskId, new TaskContentDraft("Card freeze", "Mobile Banking", [], [], [], null, CustomValue.NoValues)), Ct));
        TaskModel(store, taskId).EffectiveTitle.Should().Be("Card freeze");
        TaskModel(store, taskId).Cr.SquadName.Should().Be("General");

        var crId = new ChangeRequestId(Guid.NewGuid());
        var draft = new ChangeRequestDraft("CR-1042", "Instant card freeze", alice, retailCards, null, null, null, null, null, null, "0042", CustomValue.NoValues);
        await CommitAsync(store, new CreateChangeRequest(dataset.Coordinator).ExecuteAsync(new CreateChangeRequestCommand(OperationId.New(), crId, draft), Ct));
        Cr(store, crId).LinkedTaskCount.Should().Be(0);

        // The editor previews inherited values from the store's read models and sends their revisions (T-13).
        await CommitAsync(store, new LinkTaskToChangeRequest(dataset.Coordinator).ExecuteAsync(
            new LinkTaskToChangeRequestCommand(OperationId.New(), taskId, TaskModel(store, taskId).Revision, crId, Cr(store, crId).Revision), Ct));
        TaskModel(store, taskId).EffectiveTitle.Should().Be("Instant card freeze");
        TaskModel(store, taskId).Cr.Should().Be(new InheritedCrValues(crId, "CR-1042", "0042", retailCards, "Retail Cards", alice, "Alice"));
        Cr(store, crId).LinkedTaskCount.Should().Be(1);

        var cardId = new SubtaskCardId(Guid.NewGuid());
        await CommitAsync(store, new CreateSubtaskCard(dataset.Coordinator).ExecuteAsync(
            new CreateSubtaskCardCommand(OperationId.New(), taskId, cardId, "Copy fix"), Ct));
        store.SubtaskCards.Lookup(cardId).Value.ParentTitle.Should().Be("Instant card freeze");
        store.SubtaskCards.Lookup(cardId).Value.Cr.OwnerName.Should().Be("Alice");
        TaskModel(store, taskId).SubtaskCardCount.Should().Be(1);

        await CommitAsync(store, new MoveTask(dataset.Coordinator).ExecuteAsync(
            new MoveTaskCommand(OperationId.New(), taskId, TaskModel(store, taskId).Revision, inProgress, BoardPlacement.AppendToEnd), Ct));
        TaskModel(store, taskId).Status.Name.Should().Be("In Progress");
        TaskModel(store, taskId).StartDate.Should().Be(Testing.Records.Today);

        var tasksBefore = store.Tasks.Items.ToList();
        var cardsBefore = store.SubtaskCards.Items.ToList();
        var crsBefore = store.ChangeRequests.Items.ToList();
        var sequenceBefore = store.Sequence;

        store.Dispose();
        _open.Remove(dataset);
        await dataset.DisposeAsync();
        var reopened = await SqliteDataset.OpenExistingAsync(paths, new SqliteDatasetOptions(_clock));
        reopened.Error.Should().BeNull();
        dataset = Track(reopened.Dataset!);
        store = await StartStoreAsync(dataset);

        store.Sequence.Should().Be(sequenceBefore);
        store.Tasks.Items.Should().BeEquivalentTo(tasksBefore);
        store.SubtaskCards.Items.Should().BeEquivalentTo(cardsBefore);
        store.ChangeRequests.Items.Should().BeEquivalentTo(crsBefore);

        await CommitAsync(store, new DeleteChangeRequest(dataset.Coordinator).ExecuteAsync(
            new DeleteChangeRequestCommand(OperationId.New(), crId, Cr(store, crId).Revision, [taskId]), Ct));
        store.ChangeRequests.Count.Should().Be(0);
        store.Tasks.Count.Should().Be(0);
        store.SubtaskCards.Count.Should().Be(0);
        store.CurrentState.Status.Should().Be(ProjectionStatus.Ready);
        store.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var open in _open)
        {
            await open.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: temp folders are disposable.
        }
    }

    private static TaskReadModel TaskModel(WorkspaceProjectionStore store, TaskId id) => store.Tasks.Lookup(id).Value;

    private static ChangeRequestReadModel Cr(WorkspaceProjectionStore store, ChangeRequestId id) => store.ChangeRequests.Lookup(id).Value;

    private static async Task<WorkspaceProjectionStore> StartStoreAsync(SqliteDataset dataset)
    {
        var store = new WorkspaceProjectionStore(dataset.Coordinator, dataset.Snapshots, new ReadModelComposer(), new TestClock(Testing.Records.Now), new FakeDayBoundarySource());
        var settled = new TaskCompletionSource<ProjectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = store.State.Subscribe(s =>
        {
            if (s.Status is ProjectionStatus.Ready or ProjectionStatus.Faulted)
            {
                settled.TrySetResult(s);
            }
        });
        store.Start();
        (await settled.Task.WaitAsync(Timeout, Ct)).Status.Should().Be(ProjectionStatus.Ready);
        return store;
    }

    /// <summary>Runs a use case and waits until the store has applied its commit.</summary>
    private static async Task CommitAsync<T>(WorkspaceProjectionStore store, Task<AppResult<CommandOutcome<T>>> execution)
    {
        var result = await execution;
        result.Error.Should().BeNull();
        var sequence = result.Value.CommitSequence;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = store.Committed.Subscribe(c =>
        {
            if (c.Sequence >= sequence)
            {
                reached.TrySetResult();
            }
        });
        if (store.Sequence < sequence)
        {
            await reached.Task.WaitAsync(Timeout, Ct);
        }

        store.CurrentState.Status.Should().Be(ProjectionStatus.Ready);
    }

    /// <summary>People have no use case yet; arrange one directly, before the store loads its snapshot.</summary>
    private static PersonId AddPerson(DataPaths paths, string name)
    {
        var id = Guid.NewGuid();
        using var connection = new SqliteConnection($"Data Source={paths.DatabaseFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; INSERT INTO people (id, name, name_norm) VALUES ($id, $name, $norm);";
        command.Parameters.AddWithValue("$id", id.ToByteArray());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$norm", name.ToUpperInvariant());
        command.ExecuteNonQuery();
        return new PersonId(id);
    }

    private SqliteDataset Track(SqliteDataset dataset)
    {
        _open.Add(dataset);
        return dataset;
    }
}
