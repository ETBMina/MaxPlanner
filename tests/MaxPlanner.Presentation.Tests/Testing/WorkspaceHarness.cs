using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Storage;
using MaxPlanner.Presentation.Common.Scheduling;
using MaxPlanner.Presentation.Common.Store;
using MaxPlanner.Presentation.Workspace;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Presentation.Tests.Testing;

/// <summary>
/// The Phase 2 slice end to end, without XAML: a real SQLite dataset in a temp folder, the real coordinator
/// and use cases, the projection store on a manual scheduler, and the page ViewModels on a manual UI
/// dispatcher. Everything the UI thread would run (store notifications and the ViewModels' await
/// continuations) runs only when the test pumps, on the test thread.
/// </summary>
internal sealed class WorkspaceHarness : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly SynchronizationContext? _previousContext;
    private readonly UiSynchronizationContext _uiContext;

    private WorkspaceHarness(SqliteDataset dataset, DataPaths paths)
    {
        Dataset = dataset;
        Paths = paths;
        Coordinator = new ScriptedCoordinator(dataset.Coordinator);
        UseCases = WorkspaceUseCases.Create(Coordinator);
        Store = new WorkspaceProjectionStore(dataset.Coordinator, dataset.Snapshots, new ReadModelComposer(), Clock, Days, StoreScheduler);
        _uiContext = new UiSynchronizationContext(Ui);
        _previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_uiContext);
        Workspace = new WorkspaceViewModel(Store, UseCases, new DispatcherQueueScheduler(Ui), Ui, Strings, Clock);
        Store.Start();
        PumpUntil(() => Store.CurrentState.Status == ProjectionStatus.Ready && !Workspace.Context.IsEditingBlocked);
    }

    public SqliteDataset Dataset { get; }

    public DataPaths Paths { get; }

    public ScriptedCoordinator Coordinator { get; }

    public WorkspaceUseCases UseCases { get; }

    public WorkspaceProjectionStore Store { get; }

    public WorkspaceViewModel Workspace { get; }

    public ManualScheduler StoreScheduler { get; } = new();

    public ManualUiDispatcher Ui { get; } = new();

    public TestClock Clock { get; } = new(Records.Now);

    public FakeDayBoundarySource Days { get; } = new();

    public FakeStrings Strings { get; } = new FakeStrings()
        .With("Error_Storage", "Not saved. {0}")
        .With("StorageFailure_DiskFull", "The drive is full.")
        .With("Error_Conflict", "This {0} changed elsewhere.")
        .With("Error_Required", "{0} is required.")
        .With("SaveStatus_OperationDetails", "Operation {0}");

    /// <summary>Creates a dataset, lets <paramref name="arrange"/> add records through the real use cases, then starts the pages.</summary>
    public static WorkspaceHarness Create(Func<Seeder, Task>? arrange = null)
    {
        // Arranged before the UI context is installed, on the thread pool, so no await here needs pumping.
        var (dataset, paths) = System.Threading.Tasks.Task.Run(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "MaxPlanner Tëst Slice", Guid.NewGuid().ToString("N"));
            var paths = DataPaths.ForDirectory(root);
            var created = await SqliteDataset.CreateNewAsync(paths, new SqliteDatasetOptions(new TestClock(Records.Now)));
            created.Error.Should().BeNull();
            if (arrange is not null)
            {
                await arrange(new Seeder(created.Dataset!, paths));
            }

            return (created.Dataset!, paths);
        }).GetAwaiter().GetResult();
        return new WorkspaceHarness(dataset, paths);
    }

    /// <summary>Runs store and UI work until <paramref name="condition"/> holds (the database writer runs on its own thread).</summary>
    public void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            StoreScheduler.RunAll();
            Ui.RunAll();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The slice did not reach the expected state.");
            }

            Thread.Sleep(1);
        }
    }

    /// <summary>Pumps until <paramref name="task"/> completes, then returns its result.</summary>
    public T Await<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    public void Await(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Lets every queued store and UI notification settle (nothing left to run for a few cycles).</summary>
    public void Settle()
    {
        for (var quiet = 0; quiet < 20;)
        {
            var work = StoreScheduler.Pending + Ui.Pending;
            StoreScheduler.RunAll();
            Ui.RunAll();
            quiet = work == 0 ? quiet + 1 : 0;
            Thread.Sleep(1);
        }
    }

    /// <summary>Another route (another editor or page) committing through the real use cases, then settling.</summary>
    public T External<T>(Func<WorkspaceUseCases, Task<AppResult<CommandOutcome<T>>>> command)
    {
        var result = Await(command(UseCases));
        result.Error.Should().BeNull();
        PumpUntil(() => Store.Sequence >= result.Value.CommitSequence);
        Settle();
        return result.Value.Value;
    }

    public TaskReadModel Task(TaskId id) => Store.Tasks.Lookup(id).Value;

    public void Dispose()
    {
        Workspace.Dispose();
        Store.Dispose();
        SynchronizationContext.SetSynchronizationContext(_previousContext);
        System.Threading.Tasks.Task.Run(async () => await Dataset.DisposeAsync()).GetAwaiter().GetResult();
        Days.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Paths.ApplicationDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: temp folders are disposable.
        }
    }

    /// <summary>Arranges records through the real use cases before the pages start.</summary>
    public sealed class Seeder(SqliteDataset dataset, DataPaths paths)
    {
        public IMutationCoordinator Coordinator => dataset.Coordinator;

        public async Task<TaskId> TaskAsync(string? title, string service = "Mobile Banking", ChangeRequestId? cr = null, params string[] checklist)
        {
            var id = new TaskId(Guid.NewGuid());
            var link = cr is { } crId ? new CrLinkTarget(crId, 1) : null;
            Ok(await new CreateTask(dataset.Coordinator).ExecuteAsync(
                new CreateTaskCommand(OperationId.New(), id, new TaskContentDraft(title, service, [], [], [], null, CustomValue.NoValues), LinkTo: link), default));
            var revision = 1L;
            foreach (var text in checklist)
            {
                revision = Ok(await new AddChecklistItem(dataset.Coordinator).ExecuteAsync(
                    new AddChecklistItemCommand(OperationId.New(), id, revision, new ChecklistItemId(Guid.NewGuid()), text), default));
            }

            return id;
        }

        public async Task<SubtaskCardId> CardAsync(TaskId parent, string subtitle)
        {
            var id = new SubtaskCardId(Guid.NewGuid());
            Ok(await new CreateSubtaskCard(dataset.Coordinator).ExecuteAsync(new CreateSubtaskCardCommand(OperationId.New(), parent, id, subtitle), default));
            return id;
        }

        public async Task<ChangeRequestId> CrAsync(string number, string title)
        {
            var owner = Guid.NewGuid();
            using (var connection = new SqliteConnection($"Data Source={paths.DatabaseFile};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA busy_timeout = 5000; INSERT INTO people (id, name, name_norm) VALUES ($id, $name, $norm);";
                command.Parameters.AddWithValue("$id", owner.ToByteArray());
                command.Parameters.AddWithValue("$name", "Owner " + number);
                command.Parameters.AddWithValue("$norm", ("OWNER " + number).ToUpperInvariant());
                command.ExecuteNonQuery();
            }

            var id = new ChangeRequestId(Guid.NewGuid());
            var draft = new ChangeRequestDraft(number, title, new PersonId(owner), null, null, null, null, null, null, null, "0042", CustomValue.NoValues);
            Ok(await new CreateChangeRequest(dataset.Coordinator).ExecuteAsync(new CreateChangeRequestCommand(OperationId.New(), id, draft), default));
            return id;
        }

        private static T Ok<T>(AppResult<CommandOutcome<T>> result)
        {
            result.Error.Should().BeNull();
            return result.Value.Value;
        }
    }

    /// <summary>Posts await continuations to the manual UI dispatcher, like the window's DispatcherQueue context.</summary>
    private sealed class UiSynchronizationContext(ManualUiDispatcher ui) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ui.Post(() => d(state));

        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>
/// The real coordinator with scripted interference for one command at a time: fail it with a typed error
/// (as SQLITE_FULL is reported) or hold it until the test releases it. Counts calls that reach it.
/// </summary>
internal sealed class ScriptedCoordinator(IMutationCoordinator inner) : IMutationCoordinator
{
    private AppError? _failNext;
    private TaskCompletionSource? _holdNext;

    public int Calls { get; private set; }

    public void FailNext(AppError error) => _failNext = error;

    /// <summary>The next command waits until the returned source is completed, then runs normally.</summary>
    public TaskCompletionSource HoldNext()
    {
        _holdNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _holdNext;
    }

    public async Task<AppResult<CommandOutcome<T>>> ExecuteAsync<T>(
        OperationId operationId,
        string operationKind,
        Func<IWriteTransaction, TxResult<T>> work,
        CancellationToken cancellationToken)
    {
        Calls++;
        if (_holdNext is { } hold)
        {
            _holdNext = null;
            await hold.Task.ConfigureAwait(false);
        }

        if (_failNext is { } error)
        {
            _failNext = null;
            return AppResult.Failure<CommandOutcome<T>>(error);
        }

        return await inner.ExecuteAsync(operationId, operationKind, work, cancellationToken).ConfigureAwait(false);
    }
}
