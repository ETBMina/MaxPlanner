using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Desktop.Startup;
using MaxPlanner.Infrastructure.Backup;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Presentation.Common.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Desktop.Composition;

/// <summary>
/// The services of the one open dataset (one session per process): the dataset and its ports, the
/// projection store, the daily backup, and the use cases. Built only after the dataset opened, because
/// none of these can exist without it.
/// </summary>
/// <remarks>
/// <see cref="DisposeAsync"/> runs the T-06.6 shutdown order explicitly: the store first (no more
/// projection work), then the dataset (drain queued commits, checkpoint the WAL, close), then the container.
/// </remarks>
internal sealed class DatasetSession : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private int _isDisposed;

    private DatasetSession(ServiceProvider provider, SqliteDataset dataset)
    {
        _provider = provider;
        Dataset = dataset;
        Store = provider.GetRequiredService<WorkspaceProjectionStore>();
        Backups = provider.GetRequiredService<DailyBackupService>();
    }

    public SqliteDataset Dataset { get; }

    public WorkspaceProjectionStore Store { get; }

    public DailyBackupService Backups { get; }

    public IServiceProvider Services => _provider;

    public static DatasetSession Create(IServiceProvider app, SqliteDataset dataset)
    {
        var services = new ServiceCollection();

        // Shared, app-lifetime services are forwarded as instances; the app container owns them.
        services.AddSingleton(app.GetRequiredService<TimeProvider>());
        services.AddSingleton(app.GetRequiredService<ILoggerFactory>());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(app.GetRequiredService<ReadModelComposer>());
        services.AddSingleton(app.GetRequiredService<IDayBoundarySource>());

        // Owned and disposed explicitly by DisposeAsync, in the required order.
        services.AddSingleton(dataset);
        services.AddSingleton<IMutationCoordinator>(dataset.Coordinator);
        services.AddSingleton<IChangeFeed>(dataset.Coordinator);
        services.AddSingleton<IOperationStatusQuery>(dataset.Coordinator);
        services.AddSingleton(dataset.Snapshots);

        services.AddSingleton(sp => new WorkspaceProjectionStore(
            sp.GetRequiredService<IChangeFeed>(),
            sp.GetRequiredService<IReadSnapshotSource>(),
            sp.GetRequiredService<ReadModelComposer>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IDayBoundarySource>()));
        services.AddSingleton(sp => new DailyBackupService(
            dataset, sp.GetRequiredService<TimeProvider>(), StartupContext.AppVersion, sp.GetRequiredService<ILogger<DailyBackupService>>()));

        // Use cases are stateless; each command resolves a fresh one (Phase 2 pages and editors).
        services.AddTransient<CreateTask>();
        services.AddTransient<UpdateTaskContent>();
        services.AddTransient<MoveTask>();
        services.AddTransient<CreateChangeRequest>();
        services.AddTransient<DeleteChangeRequest>();
        services.AddTransient<LinkTaskToChangeRequest>();
        services.AddTransient<AddChecklistItem>();
        services.AddTransient<SetChecklistItemCompleted>();
        services.AddTransient<CreateSubtaskCard>();
        services.AddTransient<PopOutChecklistItem>();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return new DatasetSession(provider, dataset);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        Store.Dispose();
        await Dataset.DisposeAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}
