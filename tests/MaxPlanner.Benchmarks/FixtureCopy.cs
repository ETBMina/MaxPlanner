using System.Reactive.Linq;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Storage;
using MaxPlanner.Presentation.Common.Store;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Benchmarks;

/// <summary>
/// A temporary copy of the benchmark fixture, opened as the app opens it. Benchmarks that write work on the copy, so the
/// generated fixture stays the same for every run.
/// </summary>
internal sealed class FixtureCopy : IAsyncDisposable
{
    public const string FixtureVariable = "MAXPLANNER_FIXTURE";

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    private FixtureCopy(string root, SqliteDataset dataset)
    {
        Root = root;
        Dataset = dataset;
    }

    public string Root { get; }

    public SqliteDataset Dataset { get; }

    /// <summary>The fixture app folder (it contains <c>data/data.db</c>), from <see cref="FixtureVariable"/>.</summary>
    public static DataPaths SourcePaths()
    {
        var folder = Environment.GetEnvironmentVariable(FixtureVariable);
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException($"Set {FixtureVariable} to the app folder that holds the benchmark fixture (data/data.db).");
        }

        var paths = DataPaths.ForDirectory(Path.GetFullPath(folder));
        return File.Exists(paths.DatabaseFile)
            ? paths
            : throw new InvalidOperationException($"No fixture dataset at {paths.DatabaseFile}. Run MaxPlanner.FixtureGenerator benchmark first.");
    }

    /// <summary>A fresh empty folder for a benchmark that needs its own dataset (an import target).</summary>
    public static string NewTempFolder() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "MaxPlanner Bench", Guid.NewGuid().ToString("N"))).FullName;

    public static async Task<FixtureCopy> OpenAsync()
    {
        var source = SourcePaths();
        var root = NewTempFolder();
        var target = DataPaths.ForDirectory(root);
        Directory.CreateDirectory(target.DataDirectory);

        // The fixture is closed; a WAL file left by an interrupted run is copied too, so no commit is lost.
        foreach (var file in Directory.GetFiles(source.DataDirectory, Path.GetFileName(source.DatabaseFile) + "*"))
        {
            File.Copy(file, Path.Combine(target.DataDirectory, Path.GetFileName(file)));
        }
        var opened = await SqliteDataset.OpenExistingAsync(target);
        return opened.Dataset is { } dataset
            ? new FixtureCopy(root, dataset)
            : throw new InvalidOperationException($"The fixture copy did not open: {opened.Status} {opened.Error?.Code}");
    }

    /// <summary>Starts a projection store on the dataset and waits for its initial load (ADR 0010 handoff).</summary>
    public static async Task<WorkspaceProjectionStore> StartStoreAsync(SqliteDataset dataset, IDayBoundarySource days)
    {
        var store = new WorkspaceProjectionStore(dataset.Coordinator, dataset.Snapshots, new ReadModelComposer(), TimeProvider.System, days);
        var settled = new TaskCompletionSource<ProjectionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (store.State.Subscribe(s =>
        {
            if (s.Status is ProjectionStatus.Ready or ProjectionStatus.Faulted)
            {
                settled.TrySetResult(s.Status);
            }
        }))
        {
            store.Start();
            var status = await settled.Task.WaitAsync(ReadyTimeout);
            if (status != ProjectionStatus.Ready)
            {
                store.Dispose();
                throw new InvalidOperationException("The projection store faulted while loading the fixture.");
            }
        }

        return store;
    }

    /// <summary>Waits until the store has applied the commit with <paramref name="sequence"/> (publication latency).</summary>
    public static async Task WaitForSequenceAsync(WorkspaceProjectionStore store, long sequence)
    {
        if (store.Sequence >= sequence)
        {
            return;
        }

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (store.Committed.Subscribe(c =>
        {
            if (c.Sequence >= sequence)
            {
                reached.TrySetResult();
            }
        }))
        {
            if (store.Sequence < sequence)
            {
                await reached.Task.WaitAsync(ReadyTimeout);
            }
        }
    }

    public static void DeleteFolder(string folder)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the folder is in the temp directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Dataset.DisposeAsync();
        DeleteFolder(Root);
    }
}

/// <summary>A day source that never changes the local day during a benchmark.</summary>
internal sealed class FixedDayBoundarySource : IDayBoundarySource
{
    public IObservable<DateOnly> LocalDates { get; } = Observable.Return(DateOnly.FromDateTime(DateTime.Today));
}
